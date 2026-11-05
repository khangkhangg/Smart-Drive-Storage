using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Miu.Core.Rules;

/// <summary>
/// Minimal glob matcher: <c>*</c> (within a segment), <c>?</c>, <c>**</c> (any depth) and <c>{a,b}</c>.
/// Both '/' and '\' are treated as separators. The whole text must match.
/// </summary>
public static class Glob
{
    private static readonly ConcurrentDictionary<(string, bool), Regex> Cache = new();

    public static bool IsMatch(string pattern, string text, bool caseInsensitive) =>
        Cache.GetOrAdd((pattern, caseInsensitive), static k => Compile(k.Item1, k.Item2)).IsMatch(text.Replace('\\', '/'));

    private static Regex Compile(string pattern, bool ci)
    {
        var p = pattern.Replace('\\', '/');
        var sb = new StringBuilder("^");
        var inBraces = false;
        for (var i = 0; i < p.Length; i++)
        {
            var c = p[i];
            switch (c)
            {
                case '*' when i + 1 < p.Length && p[i + 1] == '*':
                    var slashAfter = i + 2 < p.Length && p[i + 2] == '/';
                    sb.Append(slashAfter ? "(?:.*/)?" : ".*");
                    i += slashAfter ? 2 : 1;
                    break;
                case '*': sb.Append("[^/]*"); break;
                case '?': sb.Append("[^/]"); break;
                case '{': inBraces = true; sb.Append("(?:"); break;
                case '}' when inBraces: inBraces = false; sb.Append(')'); break;
                case ',' when inBraces: sb.Append('|'); break;
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        sb.Append('$');
        var opts = RegexOptions.CultureInvariant | RegexOptions.Compiled;
        if (ci) opts |= RegexOptions.IgnoreCase;
        return new Regex(sb.ToString(), opts);
    }
}
