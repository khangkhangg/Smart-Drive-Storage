using Miu.Core.Rules;

namespace Miu.App.Services;

/// <summary>Plain-language description of what a rule matches.</summary>
public static class RuleDescriber
{
    public static string Describe(Rule r)
    {
        var m = r.Match;
        string Exts(string[] e) => string.Join(", ", e.Select(x => "." + x));
        string? place = m.ChildOf is { } childOf ? $"directly in {childOf}" : m.Inside is { } i ? $"inside {i}" : m.Under is { } u ? $"in {u}" : null;
        var what = m switch
        {
            { Ext.Length: > 0 } => $"{Exts(m.Ext!)} files " + (place ?? (m.Path is null ? "anywhere" : $"matching {m.Path}")),
            { Names.Length: > 0 } => $"{(m.IsDir == true ? "Folders" : "Items")} named {string.Join(", ", m.Names!)} " + (place ?? (m.Path is null ? "anywhere" : $"matching {m.Path}")),
            { ChildOf: { } ch } => $"Items directly in {ch}",
            { Inside: { } ins } => $"Everything inside {ins}",
            { Under: { } un } => un,
            { Path: { } p } => p,
            _ => "Everything",
        };
        var c = r.Conditions;
        var extra = new List<string>();
        if (c.ProjectIdleDays is { } idle) extra.Add($"project idle {idle}+ days");
        if (c.OlderThanDays is { } old) extra.Add($"older than {old} days");
        if (c.MinSizeMB is { } mb) extra.Add($"over {mb} MB");
        if (c.RequiresSiblingMarker is { Length: > 0 } sm) extra.Add("next to " + string.Join(" or ", sm.Take(3)));
        if (c.RequiresGitIgnored == true) extra.Add("listed in .gitignore");
        return extra.Count == 0 ? what : $"{what} · {string.Join(" · ", extra)}";
    }
}
