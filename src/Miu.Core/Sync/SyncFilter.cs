using Miu.Core.Rules;

namespace Miu.Core.Sync;

/// <summary>
/// Skip lists for sync jobs and drive backups. A pattern without '/' matches a file or folder name anywhere
/// (like .gitignore); a pattern with '/' matches the path from the job's root. Skipping a folder skips everything in it.
/// Skipped paths are left alone on both sides: never copied, never set aside.
/// </summary>
public static class SyncFilter
{
    public static bool IsExcluded(string relPath, IReadOnlyList<string>? patterns, bool caseInsensitive)
    {
        if (patterns is not { Count: > 0 }) return false;
        var rel = relPath.Replace('\\', '/').Trim('/');
        var segments = rel.Split('/');
        foreach (var raw in patterns)
        {
            var p = raw.Trim().Replace('\\', '/').Trim('/');
            if (p.Length == 0 || p.StartsWith('#')) continue;
            if (!p.Contains('/'))
            {
                if (segments.Any(s => Glob.IsMatch(p, s, caseInsensitive))) return true;
                continue;
            }
            // The path itself or any folder above it.
            var prefix = "";
            foreach (var s in segments)
            {
                prefix = prefix.Length == 0 ? s : prefix + "/" + s;
                if (Glob.IsMatch(p, prefix, caseInsensitive)) return true;
            }
        }
        return false;
    }

    /// <summary>Parses a skip list typed one pattern per line (blank lines and # comments ignored).</summary>
    public static string[] Parse(string? text) =>
        (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToArray();

    /// <summary>Folders that are rebuilt automatically: skipping them makes backups much smaller.</summary>
    public static readonly string[] Rebuildable =
    [
        "node_modules", ".venv", "venv", "__pycache__", ".gradle", ".next", ".nuxt", ".turbo", ".parcel-cache", ".pytest_cache",
        ".mypy_cache", ".dart_tool", ".terraform", "DerivedData",
    ];

    /// <summary>
    /// What a drive backup skips by default: the operating system, programs (reinstall them), swap and hibernation
    /// files, the Recycle Bin, temp files and caches. A file-level backup is for your files; a disk image is for the system.
    /// </summary>
    public static string[] DriveBackupDefaults(bool windows, bool mac) => windows
        ? [
            "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log*", "$Recycle.Bin", "System Volume Information", "$WinREAgent",
            "$Windows.~BT", "$Windows.~WS", "$SysReset", "Config.Msi", "Recovery", "Windows", "Windows.old", "Program Files", "Program Files (x86)",
            "ProgramData", "PerfLogs", "OneDriveTemp", "Users/*/AppData/Local/Temp", "Users/*/AppData/Local/Microsoft/Windows/INetCache",
            "Users/*/AppData/Local/Packages/*/LocalCache", "Users/*/AppData/Local/*/Cache", "Users/*/AppData/Local/*/User Data/*/Cache",
            "Users/*/AppData/Local/Docker", "Users/*/NTUSER.DAT*", "*.tmp", "Thumbs.db",
          ]
        : mac
        ? [
            ".Spotlight-V100", ".fseventsd", ".Trashes", ".DocumentRevisions-V100", ".TemporaryItems", "System", "private/var/vm",
            "Library/Caches", "Users/*/Library/Caches", "Users/*/.Trash", "Volumes", "dev", "cores", ".DS_Store",
          ]
        : [
            "proc", "sys", "dev", "run", "tmp", "var/tmp", "var/cache", "var/lib/docker", "lost+found", "mnt", "media", "swapfile", "swap.img",
            "home/*/.cache", "home/*/.local/share/Trash",
          ];
}
