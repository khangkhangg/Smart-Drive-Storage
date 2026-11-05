namespace Miu.Core.Formatting;

public static class HumanTime
{
    public static string Ago(DateTime thenUtc, DateTime nowUtc)
    {
        if (thenUtc == DateTime.MinValue || thenUtc.Year < 1980) return "unknown";
        var span = nowUtc - thenUtc;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return Plural((int)span.TotalMinutes, "minute");
        if (span.TotalDays < 1) return Plural((int)span.TotalHours, "hour");
        if (span.TotalDays < 2) return "yesterday";
        if (span.TotalDays < 30) return Plural((int)span.TotalDays, "day");
        if (span.TotalDays < 365) return Plural((int)(span.TotalDays / 30), "month");
        return Plural((int)(span.TotalDays / 365), "year");
    }

    private static string Plural(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")} ago";
}
