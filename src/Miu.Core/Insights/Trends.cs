using Miu.Core.Formatting;

namespace Miu.Core.Insights;

public static class SnapshotDiff
{
    /// <summary>Folders that grew the most between two snapshots (new folders count from zero).</summary>
    public static IReadOnlyList<(string Path, long Delta)> Grew(IReadOnlyDictionary<string, long> before, IReadOnlyDictionary<string, long> after, int top) =>
        after.Select(kv => (kv.Key, kv.Value - before.GetValueOrDefault(kv.Key)))
            .Where(x => x.Item2 > 0)
            .OrderByDescending(x => x.Item2)
            .Take(top)
            .ToList();
}

public static class Forecast
{
    /// <summary>
    /// Days until the drive is full at the current rate (least-squares over the samples). Null with fewer than three
    /// samples, less than two days of history, or no growth.
    /// </summary>
    public static double? DaysUntilFull(IReadOnlyList<(DateTime Ts, long Used)> samples, long totalBytes)
    {
        if (samples.Count < 3) return null;
        var t0 = samples.Min(s => s.Ts);
        if ((samples.Max(s => s.Ts) - t0).TotalDays < 2) return null;
        var xs = samples.Select(s => (s.Ts - t0).TotalDays).ToArray();
        var ys = samples.Select(s => (double)s.Used).ToArray();
        double mx = xs.Average(), my = ys.Average();
        double sxy = 0, sxx = 0;
        for (var i = 0; i < xs.Length; i++) { sxy += (xs[i] - mx) * (ys[i] - my); sxx += (xs[i] - mx) * (xs[i] - mx); }
        if (sxx == 0) return null;
        var perDay = sxy / sxx;
        if (perDay <= 0) return null;
        var latest = samples.OrderBy(s => s.Ts).Last();
        var predictedNow = my + perDay * ((latest.Ts - t0).TotalDays - mx);
        return Math.Max(0, (totalBytes - predictedNow) / perDay);
    }

    public static string? Describe(double? days) => days switch
    {
        null or > 180 => null,
        < 14 => $"Full in ~{Math.Max(1, (int)Math.Round(days.Value))} days",
        _ => $"Full in ~{(int)Math.Round(days.Value / 7)} weeks",
    };
}

public static class Digest
{
    public static (string Title, string Body) Build(long freedBytes, IReadOnlyList<(string Path, long Delta)> grew, int pendingSuggestions, long devRebuildableBytes)
    {
        var parts = new List<string>
        {
            freedBytes > 0 ? $"Freed {SizeFormatter.Format(freedBytes)} this week." : "Nothing cleaned this week.",
        };
        if (grew.Count > 0)
            parts.Add($"{System.IO.Path.GetFileName(grew[0].Path.TrimEnd('/', '\\'))} grew {SizeFormatter.Format(grew[0].Delta)}.");
        if (pendingSuggestions > 0)
            parts.Add($"{pendingSuggestions} rule {(pendingSuggestions == 1 ? "suggestion is" : "suggestions are")} waiting for you.");
        if (devRebuildableBytes > 0)
            parts.Add($"Your projects hold {SizeFormatter.Format(devRebuildableBytes)} of rebuildable files.");
        return ("Your week with Miu", string.Join(" ", parts));
    }
}
