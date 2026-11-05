namespace Miu.Core.Rules;

/// <summary>
/// Decides the safety verdict for an item: protected paths first, then user/learned rules, then built-in
/// rules; within a tier the highest priority, then the most specific rule wins.
/// </summary>
public sealed class RuleEngine
{
    public const string NoMatchReason = "No rule matched: review before deleting.";

    private readonly Rule[] _rules;
    private readonly ProtectedPathGuard _guard;
    private readonly bool _ci;

    public RuleEngine(IEnumerable<Rule> rules, ProtectedPathGuard guard, bool caseInsensitive)
    {
        _guard = guard;
        _ci = caseInsensitive;
        _rules = rules.Where(r => r.Enabled && r.Source != RuleSource.Protected)
            .OrderBy(r => Tier(r.Source))
            .ThenByDescending(r => r.Priority)
            .ThenByDescending(Specificity)
            .ThenBy(r => r.Verdict == Verdict.Keep ? 0 : 1) // on a tie, keeping wins
            .ToArray();
    }

    public IReadOnlyList<Rule> Rules => _rules;

    public VerdictResult Evaluate(ItemFacts f)
    {
        var why = _guard.Explain(f.FullPath, checkLink: f.IsLink);
        if (why is not null) return new VerdictResult(Verdict.Protected, "protected", why, 1, null);

        foreach (var r in _rules)
            if (Matches(r.Match, f) && Holds(r.Conditions, f))
                return new VerdictResult(r.Verdict, r.Id, Fill(r.Reason, f.FullPath), Confidence(r.Source), r.RebuildHint);

        return new VerdictResult(Verdict.Review, null, NoMatchReason, 0.5, null);
    }

    private bool Matches(RuleMatch m, ItemFacts f)
    {
        if (m.IsDir is { } d && d != f.IsDir) return false;
        if (m.Path is not null && !Glob.IsMatch(m.Path, f.FullPath, _ci)) return false;
        if (m.Under is not null && !IsAtOrBelow(f.FullPath, m.Under, includeSelf: true)) return false;
        if (m.Inside is not null && !IsAtOrBelow(f.FullPath, m.Inside, includeSelf: false)) return false;
        if (m.ChildOf is not null && !IsChildOf(f.FullPath, m.ChildOf)) return false;
        if (m.Names is { Length: > 0 } && !m.Names.Any(n => Glob.IsMatch(n, f.Name, _ci))) return false;
        if (m.Ext is { Length: > 0 } && !m.Ext.Any(e => f.Name.EndsWith("." + e.TrimStart('.'),
                _ci ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) return false;
        return true;
    }

    /// <summary>Whole-segment containment: "/a/Clients" contains "/a/Clients/x" but not "/a/Clients2".</summary>
    private bool IsAtOrBelow(string path, string folder, bool includeSelf)
    {
        var cmp = _ci ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var p = path.Replace('\\', '/').TrimEnd('/');
        var d = folder.Replace('\\', '/').TrimEnd('/');
        if (p.Equals(d, cmp)) return includeSelf;
        return p.StartsWith(d + "/", cmp);
    }

    private bool IsChildOf(string path, string folder)
    {
        var cmp = _ci ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var p = path.Replace('\\', '/').TrimEnd('/');
        var cut = p.LastIndexOf('/');
        return cut >= 0 && p[..cut].Equals(folder.Replace('\\', '/').TrimEnd('/'), cmp);
    }

    private static bool Holds(RuleConditions c, ItemFacts f)
    {
        if (c.OlderThanDays is { } days && (f.NowUtc - f.ModifiedUtc).TotalDays < days) return false;
        if (c.MinSizeMB is { } mb && f.Size < mb * 1024 * 1024) return false;
        if (c.ProjectIdleDays is { } idle && (f.ProjectIdleDays is null || f.ProjectIdleDays < idle)) return false;
        if (c.RequiresSiblingMarker is { Length: > 0 } sm && !sm.Any(f.SiblingExists)) return false;
        if (c.RequiresChildMarker is { Length: > 0 } cm && (f.ChildExists is null || !cm.Any(f.ChildExists))) return false;
        if (c.RequiresGitIgnored == true && f.IsGitIgnored?.Invoke() != true) return false;
        return true;
    }

    private static int Tier(RuleSource s) => s is RuleSource.User or RuleSource.Learned ? 0 : 1;

    private static double Confidence(RuleSource s) => s switch
    {
        RuleSource.User => 1.0,
        RuleSource.Learned => 0.9,
        _ => 0.95,
    };

    private static int Specificity(Rule r)
    {
        var literal = (r.Match.Path?.Count(ch => ch is not ('*' or '?')) ?? 0) + (r.Match.Under?.Length ?? 0) + (r.Match.Inside?.Length ?? 0) + (r.Match.ChildOf?.Length ?? 0);
        var c = r.Conditions;
        var conds = new object?[] { c.OlderThanDays, c.MinSizeMB, c.ProjectIdleDays, c.RequiresSiblingMarker, c.RequiresChildMarker, c.RequiresGitIgnored }
            .Count(x => x is not null);
        return literal + conds * 10;
    }

    /// <summary>Fills <c>{project}</c> in a reason: the nearest folder above the item that isn't a tool folder
    /// (".claude", ".worktrees", "worktrees"), e.g. "Miu Music Player" for ...\Miu Music Player\.claude\worktrees\x.</summary>
    public static string Fill(string reason, string fullPath)
    {
        if (!reason.Contains("{project}", StringComparison.Ordinal)) return reason;
        var dir = Path.GetDirectoryName(fullPath.Replace('\\', '/').TrimEnd('/'));
        while (dir is { Length: > 0 })
        {
            var name = Path.GetFileName(dir);
            if (name.Length == 0) break;
            if (!name.StartsWith('.') && !name.Equals("worktrees", StringComparison.OrdinalIgnoreCase))
                return reason.Replace("{project}", name, StringComparison.Ordinal);
            dir = Path.GetDirectoryName(dir);
        }
        return reason.Replace("{project}", "a project", StringComparison.Ordinal);
    }
}
