using System.Text.Json;
using System.Text.Json.Serialization;
using Miu.Core.Rules;

namespace Miu.Core.Explain;

/// <summary>How safe it is to remove something, in plain terms.</summary>
public enum Safety
{
    /// <summary>Rebuilt or re-downloaded automatically: fine to remove.</summary>
    Safe,
    /// <summary>Fine to free, but use the tool's own command (it tidies records or keeps what's still in use).</summary>
    UseTool,
    /// <summary>May hold your own work or settings: check first.</summary>
    Careful,
    /// <summary>Part of the system or a running program: don't delete it by hand.</summary>
    Never,
}

public sealed record ExplainCommand(string Label, string Text);

/// <summary>A plain-language explanation of a kind of file or folder.</summary>
/// <param name="WhyBig">Why it tends to be large (shown when it is).</param>
/// <param name="HowTo">Steps to free the space properly.</param>
/// <param name="Search">A good web search for more (only the name is used, never your path).</param>
public sealed record Explanation(
    string Id,
    RuleMatch Match,
    string Title,
    string What,
    Safety Safety,
    string SafetyText,
    string[]? HowTo = null,
    ExplainCommand[]? Commands = null,
    string? WhyBig = null,
    string? Search = null,
    string[]? RequiresChildMarker = null,
    string[]? RequiresSiblingMarker = null,
    int Priority = 500);

/// <summary>Finds the best explanation for an item from the knowledge files (rules/explain/*.json).</summary>
public sealed class Explainer(IReadOnlyList<Explanation> entries, bool caseInsensitive)
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed record Pack(List<Explanation> Entries);

    private readonly Explanation[] _ordered = entries
        .OrderByDescending(e => e.Priority)
        .ThenByDescending(e => (e.Match.Path?.Length ?? 0) + (e.Match.Names?.Sum(n => n.Length) ?? 0))
        .ToArray();

    public IReadOnlyList<Explanation> Entries => _ordered;

    public static IReadOnlyList<Explanation> LoadJson(string json) =>
        JsonSerializer.Deserialize<Pack>(json, JsonOptions)?.Entries ?? [];

    public static Explainer LoadDirectory(string dir, bool caseInsensitive) =>
        new(Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal).SelectMany(f => LoadJson(File.ReadAllText(f))).ToList() : [],
            caseInsensitive);

    /// <param name="childExists">Glob test against names directly inside the item (folders).</param>
    /// <param name="siblingExists">Glob test against names next to the item.</param>
    public Explanation? Find(string fullPath, string name, bool isDir, Func<string, bool>? childExists = null, Func<string, bool>? siblingExists = null)
    {
        foreach (var e in _ordered)
        {
            var m = e.Match;
            if (m.IsDir is { } d && d != isDir) continue;
            if (m.Path is not null && !Glob.IsMatch(m.Path, fullPath, caseInsensitive)) continue;
            if (m.Names is { Length: > 0 } && !m.Names.Any(n => Glob.IsMatch(n, name, caseInsensitive))) continue;
            if (m.Ext is { Length: > 0 } && !m.Ext.Any(x => name.EndsWith("." + x.TrimStart('.'), caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) continue;
            if (e.RequiresChildMarker is { Length: > 0 } cm && (childExists is null || !cm.Any(childExists))) continue;
            if (e.RequiresSiblingMarker is { Length: > 0 } sm && (siblingExists is null || !sm.Any(siblingExists))) continue;
            return e;
        }
        return null;
    }

    /// <summary>A web search for this item: the explanation's own query, or the name (never the path).</summary>
    public static string SearchQuery(string name, bool isDir, Explanation? e) =>
        e?.Search ?? $"\"{name}\" {(isDir ? "folder" : "file")} what is it, is it safe to delete";
}
