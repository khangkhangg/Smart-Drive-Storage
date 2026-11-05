namespace Miu.Core.Rules;

public enum PathStyle { Windows, Unix }

/// <summary>OS-independent path normalization so Windows rules can be tested anywhere.</summary>
public static class PathNormalizer
{
    public static PathStyle Current => OperatingSystem.IsWindows() ? PathStyle.Windows : PathStyle.Unix;

    public static char Separator(PathStyle style) => style == PathStyle.Windows ? '\\' : '/';

    /// <summary>Strips \\?\ prefixes, unifies separators, resolves "." and "..", removes trailing separators.</summary>
    public static string Normalize(string path, PathStyle style)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        var sep = Separator(style);
        string root, rest;
        if (style == PathStyle.Windows)
        {
            var p = path.Replace('/', '\\');
            if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) p = @"\\" + p[8..];
            else if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal)) p = p[4..];
            if (OperatingSystem.IsWindows() && p.Length >= 2 && p[1] == ':' && (p.Length == 2 || p[2] != '\\')) p = Path.GetFullPath(p);
            // Admin shares (\\host\C$\...) expose a whole drive: treat them like that drive.
            if (p.StartsWith(@"\\", StringComparison.Ordinal) && p[2..].Split('\\') is { Length: >= 2 } unc
                && unc[1].Length == 2 && unc[1][1] == '$' && char.IsLetter(unc[1][0]))
                p = unc[1][0] + @":\" + string.Join('\\', unc.Skip(2));
            if (p.Length >= 2 && p[1] == ':') { root = char.ToUpperInvariant(p[0]) + @":\"; rest = p[2..]; }
            else if (p.StartsWith(@"\\", StringComparison.Ordinal))
            {
                var parts = p[2..].Split('\\', 3);
                root = parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}\" : p;
                rest = parts.Length == 3 ? parts[2] : "";
            }
            else if (OperatingSystem.IsWindows()) return Normalize(Path.GetFullPath(p), style);
            else { root = ""; rest = p; }
        }
        else
        {
            var p = path;
            if (!p.StartsWith('/')) p = OperatingSystem.IsWindows() ? "/" + p : Path.GetFullPath(p);
            root = "/";
            rest = p[1..];
        }

        var stack = new List<string>();
        foreach (var raw in rest.Split(sep, StringSplitOptions.RemoveEmptyEntries))
        {
            // Win32 ignores trailing dots and spaces: "C:\Windows." is "C:\Windows".
            var seg = style == PathStyle.Windows && raw is not ("." or "..") ? raw.TrimEnd('.', ' ') : raw;
            if (seg.Length == 0) continue;
            if (seg == ".") continue;
            if (seg == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); continue; }
            stack.Add(seg);
        }
        return stack.Count == 0 ? root : root + string.Join(sep, stack);
    }

    /// <summary>
    /// Paths whose real target can't be worked out from the text alone (8.3 short names that can't be expanded,
    /// alternate data streams, device/volume paths, drive-relative paths off Windows). The guard treats them as protected.
    /// </summary>
    public static bool IsAmbiguous(string path, PathStyle style)
    {
        if (style != PathStyle.Windows) return false;
        var p = path.Replace('/', '\\');
        if (p.Contains("GLOBALROOT", StringComparison.OrdinalIgnoreCase) || p.Contains("Volume{", StringComparison.OrdinalIgnoreCase)) return true;
        var body = p.Length >= 2 && p[1] == ':' ? p[2..] : p.StartsWith(@"\\?\", StringComparison.Ordinal) && p.Length > 6 && p[5] == ':' ? p[6..] : p;
        if (body.Contains(':')) return true;
        if (!OperatingSystem.IsWindows() && p.Length >= 2 && p[1] == ':' && (p.Length == 2 ? false : p[2] != '\\')) return true;
        return ShortName.IsMatch(p) && TryExpandShortName(p) is null;
    }

    private static readonly System.Text.RegularExpressions.Regex ShortName = new(@"(^|\\)[^\\~]{1,6}~\d+(\.[^\\]{0,3})?(\\|$)");

    /// <summary>Expands 8.3 names on Windows (null when not possible); the guard then checks the long form.</summary>
    public static string? TryExpandShortName(string path)
    {
        if (!OperatingSystem.IsWindows() || !ShortName.IsMatch(path.Replace('/', '\\'))) return null;
        var buffer = new char[32768];
        var len = GetLongPathNameW(path, buffer, buffer.Length);
        return len > 0 && len < buffer.Length ? new string(buffer, 0, (int)len) : null;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, char[] longPath, int bufferLength);
}
