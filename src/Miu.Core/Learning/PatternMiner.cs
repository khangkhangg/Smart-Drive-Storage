using System.Security.Cryptography;
using System.Text;
using Miu.Core.Rules;

namespace Miu.Core.Learning;

public enum DecisionAction { Keep, Delete }

/// <summary>One choice the user made about an item (explicitly, or by cleaning / restoring it).</summary>
public sealed record Decision(string Path, bool IsDir, string Name, string? Ext, string ParentPath, DecisionAction Action, string? Reason, DateTime TsUtc)
{
    public static Decision For(string path, bool isDir, DecisionAction action, string? reason, DateTime tsUtc, PathStyle style)
    {
        var p = PathNormalizer.Normalize(path, style);
        var sep = PathNormalizer.Separator(style);
        var cut = p.TrimEnd(sep).LastIndexOf(sep);
        var parent = cut <= 0 ? p[..(cut + 1)] : p[..cut];
        if (parent.Length == 2 && parent[1] == ':') parent += sep;
        var name = p[(cut + 1)..];
        var dot = name.LastIndexOf('.');
        var ext = !isDir && dot > 0 && dot < name.Length - 1 ? name[(dot + 1)..].ToLowerInvariant() : null;
        return new Decision(p, isDir, name, ext, parent, action, string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(), tsUtc);
    }
}

public sealed record RuleSuggestion(string Key, Rule Rule, string Evidence, double Confidence);

/// <summary>
/// Turns repeated, consistent decisions into suggested rules (spec §5.4). Deterministic and explainable:
/// at least 3 matching decisions (5 for "this extension anywhere"), and none that point the other way.
/// Narrow patterns are tried first; decisions used by one suggestion are not reused by a broader one.
/// </summary>
public static class PatternMiner
{
    public const int MinDecisions = 3;
    public const int MinDecisionsAnywhere = 5;

    public static IReadOnlyList<RuleSuggestion> Mine(IReadOnlyList<Decision> decisions, ISet<string> knownKeys, PathStyle style)
    {
        var cmp = style == PathStyle.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var used = new HashSet<Decision>(ReferenceEqualityComparer.Instance);
        var result = new List<RuleSuggestion>();

        void Try(string kind, IEnumerable<IGrouping<string, Decision>> groups, int min, Func<IGrouping<string, Decision>, DecisionAction, RuleMatch> match)
        {
            foreach (var g in groups)
            {
                var all = g.ToList();
                if (all.Select(d => d.Action).Distinct().Count() > 1) continue; // the user went both ways: no rule
                var fresh = all.Where(d => !used.Contains(d)).ToList();
                if (fresh.Count < min) continue;
                var action = all[0].Action;
                var key = $"{kind}|{g.Key}|{action}";
                foreach (var d in fresh) used.Add(d);
                if (knownKeys.Contains(key)) continue;
                result.Add(Build(key, match(g, action), action, fresh));
            }
        }

        var files = decisions.Where(d => !d.IsDir).ToList();
        Try("ext-in", files.Where(d => d.Ext is not null).GroupBy(d => d.ParentPath + "\u0001" + d.Ext, cmp), MinDecisions,
            (g, _) => new RuleMatch(Under: g.First().ParentPath, Ext: [g.First().Ext!], IsDir: false));
        Try("in", decisions.GroupBy(d => d.ParentPath, cmp), MinDecisions,
            (g, a) => a == DecisionAction.Keep ? new RuleMatch(Under: g.Key) : new RuleMatch(Inside: g.Key));
        Try("name", decisions.Where(d => d.IsDir).GroupBy(d => d.Name, cmp), MinDecisions,
            (g, _) => new RuleMatch(Names: [g.First().Name], IsDir: true));
        Try("ext", files.Where(d => d.Ext is not null).GroupBy(d => d.Ext!, cmp), MinDecisionsAnywhere,
            (g, _) => new RuleMatch(Ext: [g.Key], IsDir: false));
        return result;
    }

    private static RuleSuggestion Build(string key, RuleMatch match, DecisionAction action, List<Decision> evidence)
    {
        var n = evidence.Count;
        var topReason = evidence.Where(d => d.Reason is not null).GroupBy(d => d.Reason!).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        var because = topReason is null ? "" : $", reason “{topReason}”";
        var verdict = action == DecisionAction.Keep ? Verdict.Keep : Verdict.Safe;
        var reason = action == DecisionAction.Keep
            ? $"You chose Keep for {n} items like this{because}."
            : $"You removed {n} items like this{because}.";
        var evidenceText = action == DecisionAction.Keep
            ? $"You chose Keep {n} of {n} times{because}."
            : $"You deleted {n} of {n} such items{because}.";
        var id = "learned-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..10].ToLowerInvariant();
        var rule = new Rule(id, match, new RuleConditions(), verdict, reason, RuleSource.Learned, 700);
        return new RuleSuggestion(key, rule, evidenceText, Math.Min(0.99, 0.75 + 0.05 * n));
    }
}
