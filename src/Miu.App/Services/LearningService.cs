using System.Security.Cryptography;
using System.Text;
using Miu.Core.Learning;
using Miu.Core.Rules;

namespace Miu.App.Services;

public sealed record ImportResult(int Imported, string? Error, int Skipped = 0);

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
        path = PathNormalizer.Normalize(path, PathNormalizer.Current);
        services.Decisions.Add(Decision.For(path, isDir, action, reason, DateTime.UtcNow, PathNormalizer.Current), "app");
        var because = string.IsNullOrWhiteSpace(reason) ? "" : $": “{reason}”";
        var rule = new Rule(
            "user-" + Hash(IdKey(path) + "|" + action),
            new RuleMatch(Under: path),
            new RuleConditions(),
            action == DecisionAction.Keep ? Verdict.Keep : Verdict.Safe,
            action == DecisionAction.Keep ? $"You chose Keep{because}." : $"You marked this OK to delete{because}.",
            RuleSource.User, 900);
        // Teaching the opposite replaces the earlier choice for the same item.
        services.RulesStore.Delete("user-" + Hash(IdKey(path) + "|" + (action == DecisionAction.Keep ? DecisionAction.Delete : DecisionAction.Keep)));
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

    /// <summary>
    /// Restoring something you cleaned against Miu's advice is a sign you wanted it after all. Items that were
    /// cleaned because Miu called them Safe taught nothing when cleaned, so restoring them teaches nothing either.
    /// </summary>
    public void RecordRestored(IEnumerable<(string Path, bool IsDir)> items)
    {
        var cmp = services.CaseInsensitivePaths ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var learnedFrom = services.Decisions.All().Where(d => d.Action == DecisionAction.Delete)
            .Select(d => d.Path).ToHashSet(cmp);
        var any = false;
        foreach (var (path, isDir) in items)
        {
            if (!learnedFrom.Contains(PathNormalizer.Normalize(path, PathNormalizer.Current))) continue;
            services.Decisions.Add(Decision.For(path, isDir, DecisionAction.Keep, "Restored after cleaning", DateTime.UtcNow, PathNormalizer.Current), "auto");
            any = true;
        }
        if (any) Mine();
    }

    /// <summary>Looks for new patterns; returns how many new suggestions were added.</summary>
    public int Mine()
    {
        var decisions = services.Decisions.All();
        // A newer opposite choice retires a suggestion that is still waiting.
        foreach (var (id, pending) in services.Suggestions.Pending())
            if (PatternMiner.IsContradicted(pending.Rule, decisions, PathNormalizer.Current))
                services.Suggestions.SetStatus(id, "rejected");
        var known = services.Suggestions.KnownKeys();
        var existing = services.RulesStore.All().Select(r => r.Id).ToHashSet();
        var added = 0;
        foreach (var s in PatternMiner.Mine(decisions, known, PathNormalizer.Current))
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
        if (PatternMiner.IsContradicted(pending.Suggestion.Rule, services.Decisions.All(), PathNormalizer.Current))
        {
            services.Suggestions.SetStatus(suggestionId, "rejected");
            return;
        }
        services.RulesStore.Upsert(pending.Suggestion.Rule with { Enabled = true });
        services.Suggestions.SetStatus(suggestionId, "accepted");
        session?.ReplaceRules(services.AllRules());
    }

    public void Reject(long suggestionId) => services.Suggestions.SetStatus(suggestionId, "rejected");

    public string ExportJson() => RuleLoader.ToJson(services.RulesStore.All());

    /// <summary>
    /// Imports rules from a JSON file, carefully: a rule needs an explicit verdict and something to match; Protected
    /// rules are dropped (only Miu decides what is protected); priorities are capped so an imported rule can't outrank
    /// your own Keep choices; and Safe rules arrive switched off until you turn them on.
    /// </summary>
    public ImportResult ImportJson(string json)
    {
        IReadOnlyList<Rule> rules;
        HashSet<int> withVerdict;
        try
        {
            rules = RuleLoader.LoadJson(json);
            withVerdict = VerdictIndexes(json);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or NotSupportedException or InvalidOperationException)
        {
            return new ImportResult(0, "This isn't a Miu rules file: " + ex.Message);
        }
        var ids = services.RulesStore.All().Select(r => r.Id).ToHashSet();
        int count = 0, skipped = 0, index = -1;
        foreach (var r in rules)
        {
            index++;
            if (r.Source == RuleSource.Protected || string.IsNullOrWhiteSpace(r.Id) || !withVerdict.Contains(index) || r.Match.IsEmpty)
            {
                skipped++;
                continue;
            }
            var id = r.Id;
            for (var n = 2; ids.Contains(id); n++) id = $"{r.Id}-{n}";
            ids.Add(id);
            var source = r.Source == RuleSource.Learned ? RuleSource.Learned : RuleSource.User;
            var cap = source == RuleSource.Learned ? 700 : 900;
            services.RulesStore.Upsert(r with
            {
                Id = id,
                Source = source,
                Priority = Math.Clamp(r.Priority, 0, cap),
                Enabled = r.Verdict != Verdict.Safe && r.Enabled,
            });
            count++;
        }
        return new ImportResult(count, null, skipped);
    }

    /// <summary>Positions (among non-null entries) of rules that state their verdict explicitly.</summary>
    private static HashSet<int> VerdictIndexes(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json, new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip });
        var set = new HashSet<int>();
        if (!doc.RootElement.TryGetProperty("rules", out var list) || list.ValueKind != System.Text.Json.JsonValueKind.Array) return set;
        var i = 0;
        foreach (var e in list.EnumerateArray())
        {
            if (e.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            if (e.EnumerateObject().Any(p => p.Name.Equals("verdict", StringComparison.OrdinalIgnoreCase))) set.Add(i);
            i++;
        }
        return set;
    }

    private string IdKey(string path) => services.CaseInsensitivePaths ? path.ToUpperInvariant() : path;

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..12].ToLowerInvariant();
}
