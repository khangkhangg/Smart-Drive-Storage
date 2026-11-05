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
        foreach (var seg in rest.Split(sep, StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); continue; }
            stack.Add(seg);
        }
        return stack.Count == 0 ? root : root + string.Join(sep, stack);
    }
}
