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
            .ToArray();
    }

    public IReadOnlyList<Rule> Rules => _rules;

    public VerdictResult Evaluate(ItemFacts f)
    {
        var why = _guard.Explain(f.FullPath);
        if (why is not null) return new VerdictResult(Verdict.Protected, "protected", why, 1, null);

        foreach (var r in _rules)
            if (Matches(r.Match, f) && Holds(r.Conditions, f))
                return new VerdictResult(r.Verdict, r.Id, r.Reason, Confidence(r.Source), r.RebuildHint);

        return new VerdictResult(Verdict.Review, null, NoMatchReason, 0.5, null);
    }

    private bool Matches(RuleMatch m, ItemFacts f)
    {
        if (m.IsDir is { } d && d != f.IsDir) return false;
        if (m.Path is not null && !Glob.IsMatch(m.Path, f.FullPath, _ci)) return false;
        if (m.Names is { Length: > 0 } && !m.Names.Any(n => Glob.IsMatch(n, f.Name, _ci))) return false;
        if (m.Ext is { Length: > 0 } && !m.Ext.Any(e => f.Name.EndsWith("." + e.TrimStart('.'),
                _ci ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) return false;
        return true;
    }

    private static bool Holds(RuleConditions c, ItemFacts f)
    {
        if (c.OlderThanDays is { } days && (f.NowUtc - f.ModifiedUtc).TotalDays < days) return false;
        if (c.MinSizeMB is { } mb && f.Size < mb * 1024 * 1024) return false;
        if (c.ProjectIdleDays is { } idle && (f.ProjectIdleDays is null || f.ProjectIdleDays < idle)) return false;
        if (c.RequiresSiblingMarker is { Length: > 0 } sm && !sm.Any(f.SiblingExists)) return false;
        if (c.RequiresChildMarker is { Length: > 0 } cm && (f.ChildExists is null || !cm.Any(f.ChildExists))) return false;
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
        var literal = r.Match.Path?.Count(ch => ch is not ('*' or '?')) ?? 0;
        var c = r.Conditions;
        var conds = new object?[] { c.OlderThanDays, c.MinSizeMB, c.ProjectIdleDays, c.RequiresSiblingMarker, c.RequiresChildMarker }
            .Count(x => x is not null);
        return literal + conds * 10;
    }
}
