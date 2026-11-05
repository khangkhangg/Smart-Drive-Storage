namespace Miu.Core.Scanning;

/// <summary>Assigns a broad category from the file extension and well-known path fragments.</summary>
public static class Categorizer
{
    private static readonly Dictionary<string, FileCategory> ByExt = Build();

    private static readonly string[] CacheFragments =
        ["/appdata/local/temp/", "/.cache/", "/cache/", "/caches/", "/temp/", "/tmp/", "/inetcache/"];

    private static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
        { "pagefile.sys", "hiberfil.sys", "swapfile.sys", "memory.dmp" };

    public static FileCategory Categorize(string name, bool isDir, string fullPath)
    {
        if (SystemNames.Contains(name)) return FileCategory.System;
        var normalized = fullPath.Replace('\\', '/').ToLowerInvariant();
        if (CacheFragments.Any(normalized.Contains)) return FileCategory.AppDataCache;
        if (isDir) return FileCategory.Other;
        var ext = Path.GetExtension(name);
        return ext.Length > 1 && ByExt.TryGetValue(ext[1..], out var c) ? c : FileCategory.Other;
    }

    private static Dictionary<string, FileCategory> Build()
    {
        var map = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(FileCategory c, string list) { foreach (var e in list.Split(' ')) map[e] = c; }
        Add(FileCategory.Video, "mp4 mkv mov avi wmv webm m4v flv mpg mpeg ts3 mts m2ts vob 3gp");
        Add(FileCategory.Image, "jpg jpeg png gif bmp tif tiff webp heic heif raw cr2 cr3 nef arw dng psd svg ico avif");
        Add(FileCategory.Audio, "mp3 flac wav aac m4a ogg opus wma aiff alac");
        Add(FileCategory.Archive, "zip rar 7z tar gz tgz bz2 xz zst iso img cab");
        Add(FileCategory.Installer, "exe msi msix msixbundle appx dmg pkg deb rpm appimage");
        Add(FileCategory.Document, "pdf doc docx xls xlsx ppt pptx odt ods odp txt md rtf csv epub");
        Add(FileCategory.Code, "cs ts tsx js jsx mjs cjs py rs go java kt swift c cpp h hpp php rb json yaml yml toml xml html css scss sql sh ps1 lock map");
        Add(FileCategory.Game, "pak upk vpk bsa ba2 unity3d");
        Add(FileCategory.System, "sys dll drv");
        return map;
    }
}
