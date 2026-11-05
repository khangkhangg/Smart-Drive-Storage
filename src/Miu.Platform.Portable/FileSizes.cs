namespace Miu.Platform.Portable;

internal static class FileSizes
{
    private static readonly EnumerationOptions All = new()
    {
        IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public static long Of(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (!Directory.Exists(path)) return 0;
            return new DirectoryInfo(path).EnumerateFiles("*", All).Sum(f => { try { return f.Length; } catch { return 0L; } });
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path) || IsDanglingLink(path);

    private static bool IsDanglingLink(string path)
    {
        try { return new FileInfo(path).LinkTarget is not null; } catch { return false; }
    }
}
