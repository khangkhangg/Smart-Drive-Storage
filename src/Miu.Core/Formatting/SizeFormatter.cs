using System.Globalization;

namespace Miu.Core.Formatting;

public static class SizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>"1.2 GB" style: one decimal below 100, none above.</summary>
    public static string Format(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        double v = bytes;
        var unit = 0;
        while (v >= 1024 && unit < Units.Length - 1) { v /= 1024; unit++; }
        var text = v < 100 ? v.ToString("0.0", CultureInfo.InvariantCulture) : v.ToString("0", CultureInfo.InvariantCulture);
        return text + " " + Units[unit];
    }
}
