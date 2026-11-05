using System.Security.Cryptography;
using System.Text;
using Miu.Core.Learning;
using Miu.Core.Rules;

namespace Miu.App.Services;

public sealed record ImportResult(int Imported, string? Error);

/// <summary>
/// How Miu gets smarter: your Keep / Always-OK choices become rules right away, cleaning or restoring items Miu
/// wasn't sure about is remembered, and repeated choices turn into suggested rules you can turn on.
/// </summary>
public sealed class LearningService(AppServices services)
{
    public static readonly string[] Reasons = ["Active project", "Rebuildable", "Backed up elsewhere", "Never used", "Work files"];

    /// <summary>"Keep — never suggest" or "Always OK to delete" for one file or folder.</summary>
    public void Teach(string path, bool isDir, DecisionAction action, string? reason, ScanSession? session)
    {
        services.Decisions.Add(Decision.For(path, isDir, action, reason, DateTime.UtcNow, PathNormalizer.Current), "app");
        var because = string.IsNullOrWhiteSpace(reason) ? "" : $": “{reason}”";
        var rule = new Rule(
            "user-" + Hash(path + "|" + action),
            new RuleMatch(Under: path),
            new RuleConditions(),
            action == DecisionAction.Keep ? Verdict.Keep : Verdict.Safe,
            action == DecisionAction.Keep ? $"You chose Keep{because}." : $"You marked this OK to delete{because}.",
            RuleSource.User, 900);
        // Teaching the opposite replaces the earlier choice for the same item.
        services.RulesStore.Delete("user-" + Hash(path + "|" + (action == DecisionAction.Keep ? DecisionAction.Delete : DecisionAction.Keep)));
        services.RulesStore.Upsert(rule);
        Mine();
        session?.ReplaceRules(services.AllRules());
    }

    /// <summary>Remembers items you cleaned although Miu hadn't called them Safe (those are the informative ones).</summary>
    public void RecordCleaned(IEnumerable<(string Path, bool IsDir, Verdict Verdict, string Reason)> items)
    {
        var any = false;
        foreach (var i in items.Where(i => i.Verdict != Verdict.Safe))
        {
            services.Decisions.Add(Decision.For(i.Path, i.IsDir, DecisionAction.Delete, null, DateTime.UtcNow, PathNormalizer.Current), "auto");
            any = true;
        }
        if (any) Mine();
    }

    /// <summary>Restoring something is a sign you wanted to keep it.</summary>
    public void RecordRestored(IEnumerable<(string Path, bool IsDir)> items)
    {
        var any = false;
        foreach (var (path, isDir) in items)
        {
            services.Decisions.Add(Decision.For(path, isDir, DecisionAction.Keep, "Restored after cleaning", DateTime.UtcNow, PathNormalizer.Current), "auto");
            any = true;
        }
        if (any) Mine();
    }

    /// <summary>Looks for new patterns; returns how many new suggestions were added.</summary>
    public int Mine()
    {
        var known = services.Suggestions.KnownKeys();
        var existing = services.RulesStore.All().Select(r => r.Id).ToHashSet();
        var added = 0;
        foreach (var s in PatternMiner.Mine(services.Decisions.All(), known, PathNormalizer.Current))
        {
            if (existing.Contains(s.Rule.Id)) continue;
            services.Suggestions.Add(s);
            added++;
        }
        return added;
    }

    public void Accept(long suggestionId, ScanSession? session)
    {
        var pending = services.Suggestions.Pending().FirstOrDefault(p => p.Id == suggestionId);
        if (pending.Suggestion is null) return;
        services.RulesStore.Upsert(pending.Suggestion.Rule with { Enabled = true });
        services.Suggestions.SetStatus(suggestionId, "accepted");
        session?.ReplaceRules(services.AllRules());
    }

    public void Reject(long suggestionId) => services.Suggestions.SetStatus(suggestionId, "rejected");

    public string ExportJson() => RuleLoader.ToJson(services.RulesStore.All());

    /// <summary>
    /// Imports rules from a JSON file. Protected rules are dropped (only Miu decides what is protected),
    /// everything else becomes your own rule, and ids that already exist get a new one.
    /// </summary>
    public ImportResult ImportJson(string json)
    {
        IReadOnlyList<Rule> rules;
        try { rules = RuleLoader.LoadJson(json); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or NotSupportedException)
        {
            return new ImportResult(0, "This isn't a Miu rules file: " + ex.Message);
        }
        var ids = services.RulesStore.All().Select(r => r.Id).ToHashSet();
        var count = 0;
        foreach (var r in rules.Where(r => r.Source != RuleSource.Protected && !string.IsNullOrWhiteSpace(r.Id)))
        {
            var id = r.Id;
            for (var n = 2; ids.Contains(id); n++) id = $"{r.Id}-{n}";
            ids.Add(id);
            services.RulesStore.Upsert(r with { Id = id, Source = r.Source == RuleSource.Learned ? RuleSource.Learned : RuleSource.User });
            count++;
        }
        return new ImportResult(count, null);
    }

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..12].ToLowerInvariant();
}
