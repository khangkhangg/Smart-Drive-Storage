using System.Text;
using Miu.Core.Formatting;

namespace Miu.App.ViewModels;

public enum PreviewKind { Image, Text, Details }

/// <summary>
/// What the Quick preview window shows: a picture, the first lines of a text file, or details (everything else,
/// folders included). Reads at most <see cref="MaxTextBytes"/> of a file and loads pictures up to <see cref="MaxImageBytes"/>.
/// </summary>
public sealed record PreviewContent(string Name, string FullPath, PreviewKind Kind, string Text, IReadOnlyList<string> Facts)
{
    public const int MaxTextBytes = 64 * 1024;
    public const int MaxTextLines = 200;
    public const long MaxImageBytes = 40L * 1024 * 1024;

    public static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico" };

    public static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".md", ".json", ".xml", ".yml", ".yaml", ".toml", ".ini", ".cfg", ".conf", ".csv", ".tsv", ".sql",
        ".js", ".ts", ".tsx", ".jsx", ".css", ".scss", ".html", ".htm", ".php", ".py", ".rb", ".go", ".rs", ".java", ".kt",
        ".cs", ".c", ".h", ".cpp", ".hpp", ".sh", ".ps1", ".bat", ".cmd", ".env", ".gitignore", ".editorconfig", ".lock", ".properties",
    };

    public bool IsImage => Kind == PreviewKind.Image;
    public bool IsText => Kind == PreviewKind.Text;
    public bool IsDetails => Kind == PreviewKind.Details;

    public static PreviewContent For(string fullPath, string name, bool isDir, long size, string modifiedText, string? whatItIs,
        IReadOnlyList<(string Name, long Size, bool IsDir)>? children)
    {
        var facts = new List<string> { $"{SizeFormatter.Format(size)} · {modifiedText}" };
        if (whatItIs is not null) facts.Add(whatItIs);
        if (isDir)
        {
            if (children is { Count: > 0 })
            {
                facts.Add("Biggest things inside:");
                facts.AddRange(children.Select(c => $"  {(c.IsDir ? "📁" : "📄")} {c.Name} · {SizeFormatter.Format(c.Size)}"));
            }
            return new PreviewContent(name, fullPath, PreviewKind.Details, "", facts);
        }
        var ext = Path.GetExtension(name);
        if (ImageExt.Contains(ext) && size <= MaxImageBytes && File.Exists(fullPath))
            return new PreviewContent(name, fullPath, PreviewKind.Image, "", facts);
        if (ReadText(fullPath, TextExt.Contains(ext)) is { } text)
        {
            if (size > MaxTextBytes) facts.Add($"Showing the first {SizeFormatter.Format(MaxTextBytes)}.");
            return new PreviewContent(name, fullPath, PreviewKind.Text, text, facts);
        }
        facts.Add("Miu can't show this kind of file. Use Search the web to find out what it is.");
        return new PreviewContent(name, fullPath, PreviewKind.Details, "", facts);
    }

    /// <summary>The start of a file as text, or null when it looks binary (a zero byte) or can't be read.</summary>
    /// <param name="knownText">Known text types are shown even when they contain odd bytes.</param>
    public static string? ReadText(string path, bool knownText)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[MaxTextBytes];
            var n = f.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
            var span = buf.AsSpan(0, n);
            if (!knownText && span.Contains((byte)0)) return null;
            var text = Encoding.UTF8.GetString(span).Replace("\0", "");
            var lines = text.Split('\n');
            return lines.Length > MaxTextLines ? string.Join('\n', lines.Take(MaxTextLines)) + "\n…" : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
