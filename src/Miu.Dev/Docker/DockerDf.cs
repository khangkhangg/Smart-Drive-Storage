using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Miu.Dev.Docker;

public sealed record DockerUsage(string Type, int TotalCount, int Active, long SizeBytes, long ReclaimableBytes);

/// <summary>Parses <c>docker system df --format "{{json .}}"</c> (one JSON object per line, sizes like "2.1GB (45%)").</summary>
public static partial class DockerDf
{
    public static IReadOnlyList<DockerUsage> Parse(string output)
    {
        var list = new List<DockerUsage>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                string S(string name) => r.TryGetProperty(name, out var v) ? v.ToString() : "";
                list.Add(new DockerUsage(S("Type"), Int(S("TotalCount")), Int(S("Active")), ParseSize(S("Size")), ParseSize(S("Reclaimable"))));
            }
            catch (JsonException) { }
        }
        return list;
    }

    /// <summary>Docker prints decimal (SI) units: 1 kB = 1000 B.</summary>
    public static long ParseSize(string text)
    {
        var m = SizeRegex().Match(text);
        if (!m.Success) return 0;
        var value = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var mult = m.Groups[2].Value.ToUpperInvariant() switch
        {
            "KB" => 1e3, "MB" => 1e6, "GB" => 1e9, "TB" => 1e12, _ => 1,
        };
        return (long)(value * mult);
    }

    private static int Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    [GeneratedRegex(@"^\s*([\d.]+)\s*([kKMGT]?B)")]
    private static partial Regex SizeRegex();
}
