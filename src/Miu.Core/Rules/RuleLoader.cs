using System.Text.Json;
using System.Text.Json.Serialization;

namespace Miu.Core.Rules;

/// <summary>Reads rule packs: <c>{ "rules": [ ... ] }</c> JSON files.</summary>
public static class RuleLoader
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed record Pack(List<Rule> Rules);

    public static IReadOnlyList<Rule> LoadJson(string json)
    {
        var pack = JsonSerializer.Deserialize<Pack>(json, JsonOptions) ?? throw new InvalidDataException("Empty rule pack.");
        return pack.Rules.Select(r => r with
        {
            Match = r.Match ?? new RuleMatch(),
            Conditions = r.Conditions ?? new RuleConditions(),
        }).ToList();
    }

    public static IReadOnlyList<Rule> LoadDirectory(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal).SelectMany(f => LoadJson(File.ReadAllText(f))).ToList()
            : [];

    public static string ToJson(IEnumerable<Rule> rules) => JsonSerializer.Serialize(new Pack(rules.ToList()), JsonOptions);
}
