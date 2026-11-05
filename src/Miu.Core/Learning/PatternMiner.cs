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

    /// <summary>Learned rules that delete by name or type must stay under a folder at least this deep (e.g. ~/code).</summary>
    public const int MinScopeDepth = 3;

    public static IReadOnlyList<RuleSuggestion> Mine(IReadOnlyList<Decision> decisions, ISet<string> knownKeys, PathStyle style)
    {
        var cmp = style == PathStyle.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var used = new HashSet<Decision>(ReferenceEqualityComparer.Instance);
        var result = new List<RuleSuggestion>();

        void Try(string kind, IEnumerable<IGrouping<string, Decision>> groups, int min, Func<IGrouping<string, Decision>, DecisionAction, RuleMatch?> match)
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
                if (match(g, action) is not { } m) continue;
                var suggestion = Build(key, m, action, fresh);
                // Any opposite choice the rule would reach (even outside this group) means it would overrule you.
                if (IsContradicted(suggestion.Rule, decisions, style)) continue;
                result.Add(suggestion);
            }
        }

        var files = decisions.Where(d => !d.IsDir).ToList();
        Try("ext-in", files.Where(d => d.Ext is not null).GroupBy(d => d.ParentPath + "\u0001" + d.Ext, cmp), MinDecisions,
            (g, a) => a == DecisionAction.Keep
                ? new RuleMatch(Under: g.First().ParentPath, Ext: [g.First().Ext!], IsDir: false)
                : new RuleMatch(ChildOf: g.First().ParentPath, Ext: [g.First().Ext!], IsDir: false));
        Try("in", decisions.GroupBy(d => d.ParentPath, cmp), MinDecisions,
            (g, a) => a == DecisionAction.Keep ? new RuleMatch(Under: g.Key) : new RuleMatch(ChildOf: g.Key));
        Try("name", decisions.Where(d => d.IsDir).GroupBy(d => d.Name, cmp), MinDecisions,
            (g, a) => a == DecisionAction.Keep
                ? new RuleMatch(Names: [g.First().Name], IsDir: true)
                : Scoped(g, style) is { } scope ? new RuleMatch(Names: [g.First().Name], IsDir: true, Inside: scope) : null);
        Try("ext", files.Where(d => d.Ext is not null).GroupBy(d => d.Ext!, cmp), MinDecisionsAnywhere,
            (g, a) => a == DecisionAction.Keep
                ? new RuleMatch(Ext: [g.Key], IsDir: false)
                : Scoped(g, style) is { } scope ? new RuleMatch(Ext: [g.Key], IsDir: false, Inside: scope) : null);
        return result;
    }

    /// <summary>True when the rule would decide the opposite of something you chose.</summary>
    public static bool IsContradicted(Rule rule, IEnumerable<Decision> decisions, PathStyle style)
    {
        var opposite = rule.Verdict == Verdict.Keep ? DecisionAction.Delete : DecisionAction.Keep;
        return decisions.Any(d => d.Action == opposite && Matches(rule.Match, d, style));
    }

    /// <summary>Whether a rule's match covers a decided item (location, name and type parts).</summary>
    public static bool Matches(RuleMatch m, Decision d, PathStyle style)
    {
        var ci = style == PathStyle.Windows;
        var cmp = ci ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string N(string p) => p.Replace('\\', '/').TrimEnd('/');
        var path = N(d.Path);
        if (m.IsDir is { } dir && dir != d.IsDir) return false;
        if (m.Under is { } u && !(path.Equals(N(u), cmp) || path.StartsWith(N(u) + "/", cmp))) return false;
        if (m.Inside is { } i && !path.StartsWith(N(i) + "/", cmp)) return false;
        if (m.ChildOf is { } c && !N(d.ParentPath).Equals(N(c), cmp)) return false;
        if (m.Names is { Length: > 0 } names && !names.Any(n => Glob.IsMatch(n, d.Name, ci))) return false;
        if (m.Ext is { Length: > 0 } ext && (d.Ext is null || !ext.Contains(d.Ext, StringComparer.OrdinalIgnoreCase))) return false;
        if (m.Path is { } glob && !Glob.IsMatch(glob, d.Path, ci)) return false;
        return true;
    }

    /// <summary>The deepest folder containing every item, or null when that is too shallow (/, a drive, the home folder).</summary>
    private static string? Scoped(IEnumerable<Decision> group, PathStyle style)
    {
        var cmp = style == PathStyle.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var sep = PathNormalizer.Separator(style);
        string[]? common = null;
        foreach (var d in group)
        {
            var parts = d.ParentPath.Split(sep, StringSplitOptions.RemoveEmptyEntries);
            if (common is null) { common = parts; continue; }
            var n = 0;
            while (n < common.Length && n < parts.Length && string.Equals(common[n], parts[n], cmp)) n++;
            common = common[..n];
        }
        var depth = common?.Count(p => !(p.Length == 2 && p[1] == ':')) ?? 0;
        if (common is null || depth < MinScopeDepth) return null;
        var joined = string.Join(sep, common);
        return style == PathStyle.Windows ? joined : sep + joined;
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
