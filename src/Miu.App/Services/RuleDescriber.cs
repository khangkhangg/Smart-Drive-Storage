using Miu.Core.Rules;

namespace Miu.App.Services;

/// <summary>Plain-language description of what a rule matches.</summary>
public static class RuleDescriber
{
    public static string Describe(Rule r)
    {
        var m = r.Match;
        var what = (m.Ext, m.Names, m.Under, m.Inside, m.Path) switch
        {
            ({ Length: > 0 } e, _, { } u, _, _) => $"{string.Join(", ", e.Select(x => "." + x))} files in {u}",
            ({ Length: > 0 } e, _, _, { } i, _) => $"{string.Join(", ", e.Select(x => "." + x))} files in {i}",
            ({ Length: > 0 } e, null, null, null, null) => $"{string.Join(", ", e.Select(x => "." + x))} files anywhere",
            (_, { Length: > 0 } n, _, _, _) => $"{(m.IsDir == true ? "Folders" : "Items")} named {string.Join(", ", n)}" + (m.Path is null ? " anywhere" : $" in {m.Path}"),
            (_, _, { } u, _, _) => u,
            (_, _, _, { } i, _) => $"Everything inside {i}",
            (_, _, _, _, { } p) => p,
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
