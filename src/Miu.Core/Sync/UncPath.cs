using System.Text.RegularExpressions;

namespace Miu.Core.Sync;

/// <summary>A network share location: \\server\share\sub\folders. Also accepts //server/share and smb://server/share.</summary>
public sealed partial record UncPath(string Server, string Share, IReadOnlyList<string> Sub)
{
    [GeneratedRegex(@"^(?:[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)*|\[[0-9A-Fa-f:.]+\])$")]
    private static partial Regex ServerPattern();

    private static readonly char[] Invalid = ['<', '>', ':', '"', '|', '?', '*', '\0'];

    public string ShareRoot => $@"\\{Server}\{Share}";

    public override string ToString() => Sub.Count == 0 ? ShareRoot : ShareRoot + "\\" + string.Join('\\', Sub);

    public string ToSmbUrl() => $"smb://{Server}/{Share}" + string.Concat(Sub.Select(s => "/" + s));

    public static bool TryParse(string? input, out UncPath? path)
    {
        path = null;
        var s = input?.Trim() ?? "";
        string rest;
        if (s.StartsWith("smb://", StringComparison.OrdinalIgnoreCase)) rest = s[6..];
        else if (s.StartsWith(@"\\", StringComparison.Ordinal) || s.StartsWith("//", StringComparison.Ordinal)) rest = s[2..];
        else return false;
        rest = rest.Replace('/', '\\').TrimEnd('\\');
        var parts = rest.Split('\\');
        if (parts.Length < 2 || parts.Any(p => p.Length == 0)) return false;
        if (!ServerPattern().IsMatch(parts[0])) return false;
        foreach (var p in parts.Skip(1))
            if (p is "." or ".." || p.IndexOfAny(Invalid) >= 0 || p.Trim().Length != p.Length || p.Any(char.IsControl)) return false;
        path = new UncPath(parts[0], parts[1], parts.Skip(2).ToArray());
        return true;
    }
}
