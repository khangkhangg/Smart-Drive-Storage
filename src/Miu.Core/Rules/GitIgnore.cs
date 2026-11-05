namespace Miu.Core.Rules;

/// <summary>
/// Just enough .gitignore matching to tell whether a direct child folder is ignored: name patterns
/// (<c>dist</c>, <c>/build/</c>, <c>**/out</c>, <c>[Ll]ibrary/</c>) and negations. Patterns with a path in the
/// middle (<c>src/build</c>) are ignored, which errs towards "not ignored".
/// </summary>
public static class GitIgnore
{
    /// <param name="allowAnchored">False when the .gitignore belongs to an ancestor folder: "/dist" then doesn't apply.</param>
    public static bool IsIgnored(IEnumerable<string> lines, string name, bool caseInsensitive, bool allowAnchored = true)
    {
        var ignored = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var negate = line.StartsWith('!');
            if (negate) line = line[1..];
            var anchored = line.StartsWith('/');
            line = line.Trim('/');
            if (line.StartsWith("**/", StringComparison.Ordinal)) { line = line[3..]; anchored = false; }
            if (line.Length == 0 || line.Contains('/')) continue;
            if (anchored && !allowAnchored) continue;
            if (Glob.IsMatch(line, name, caseInsensitive)) ignored = !negate;
        }
        return ignored;
    }
}
