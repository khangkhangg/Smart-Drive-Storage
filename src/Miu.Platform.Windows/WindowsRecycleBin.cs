using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Miu.Platform.Abstractions;

namespace Miu.Platform.Windows;

/// <summary>
/// Sends items to the Windows Recycle Bin (one shell operation per item so failures stay per item) and restores
/// them by moving the <c>$R…</c> item back and removing its <c>$I…</c> index.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRecycleBin : IRecycleBin
{
    public bool IsSupported => true;

    public Task<RecycleResult> RecycleAsync(IReadOnlyList<string> paths, IProgress<int>? progress, CancellationToken ct) => Task.Run(() =>
    {
        var done = new List<RecycledItem>();
        var failed = new List<FailedItem>();
        var i = 0;
        foreach (var raw in paths)
        {
            // Stop between items, but always report what already moved so it is logged and can be undone.
            if (ct.IsCancellationRequested) break;
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
            progress?.Report(++i);
            try
            {
                RecycleOne(path, done, failed);
            }
            catch (Exception ex)
            {
                failed.Add(new FailedItem(path, "Something went wrong: " + ex.Message));
            }
        }
        return new RecycleResult(done, failed);
    }, ct);

    private static void RecycleOne(string path, List<RecycledItem> done, List<FailedItem> failed)
    {
            if (!File.Exists(path) && !Directory.Exists(path)) { failed.Add(new FailedItem(path, "It is no longer there (moved or deleted after the scan).")); return; }
            // Without a Recycle Bin (network shares, many USB drives, bin turned off) Windows would delete for good.
            if (!HasRecycleBin(path)) { failed.Add(new FailedItem(path, "This drive has no Recycle Bin, so Miu won't delete from it.")); return; }
            var bytes = SizeOf(path);
            var bin = BinFolder(path);
            var before = bin is null ? [] : IndexFiles(bin).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var started = DateTime.UtcNow;

            var rc = Shell.Recycle(path, out var aborted);
            if (rc != 0 || aborted || File.Exists(path) || Directory.Exists(path))
            {
                failed.Add(new FailedItem(path, rc == 0 ? "Cancelled, or it is in use." : $"It is in use or can't be moved (error 0x{rc:X})."));
                return;
            }

            var trashLocation = bin is null ? "" : FindRecycledCopy(bin, path, before, started);
            if (trashLocation.Length == 0)
            {
                // Gone, but there is no Recycle Bin copy: never report that as "moved".
                var seen = bin is null ? "no Recycle Bin folder" : $"{IndexFiles(bin).Count(f => !before.Contains(f))} new entries in {bin}";
                failed.Add(new FailedItem(path, $"Windows removed it but no Recycle Bin copy was found ({seen}); it may be deleted permanently."));
                return;
            }
            done.Add(new RecycledItem(path, trashLocation, bytes));
    }

    /// <summary>
    /// Finds the $R copy the shell just made. The $I record can appear a moment after the call returns, so look a few
    /// times; match by original path, accepting entries that are new or were written since the delete started.
    /// </summary>
    private static string FindRecycledCopy(string bin, string path, HashSet<string> before, DateTime started)
    {
        for (var attempt = 0; attempt < 40; attempt++) // up to 4 s: big folders can take a moment
        {
            foreach (var index in IndexFiles(bin))
            {
                var isNew = !before.Contains(index);
                if (!isNew && SafeWriteTime(index) < started.AddSeconds(-2)) continue;
                if (ReadIndex(index) is { } e && SamePath(e.OriginalPath, path))
                    return Path.Combine(bin, "$R" + Path.GetFileName(index)[2..]);
            }
            Thread.Sleep(100);
        }
        return "";
    }

    private static bool SamePath(string a, string b)
    {
        static string N(string p)
        {
            try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)); } catch (ArgumentException) { return p; }
        }
        return string.Equals(N(a), N(b), StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime SafeWriteTime(string file)
    {
        try { return File.GetLastWriteTimeUtc(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    /// <summary>True only for local fixed drives whose Recycle Bin Windows reports as available.</summary>
    public static bool HasRecycleBin(string path)
    {
        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            if (new DriveInfo(root).DriveType != DriveType.Fixed) return false;
            var info = new Shell.SHQUERYRBINFO { cbSize = Marshal.SizeOf<Shell.SHQUERYRBINFO>() };
            return Shell.SHQueryRecycleBinW(root, ref info) == 0;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return false; }
    }

    private static RecycleBinEntry? ReadIndex(string file)
    {
        try { return RecycleBinIndex.Parse(File.ReadAllBytes(file)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public Task<RestoreResult> RestoreAsync(IReadOnlyList<RecycledItem> items, CancellationToken ct) => Task.Run(() =>
    {
        var restored = new List<string>();
        var failed = new List<FailedItem>();
        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                if (string.IsNullOrEmpty(item.TrashLocation)) { failed.Add(new FailedItem(item.OriginalPath, "Restore it from the Recycle Bin.")); continue; }
                if (File.Exists(item.OriginalPath) || Directory.Exists(item.OriginalPath)) { failed.Add(new FailedItem(item.OriginalPath, "Something with that name already exists there.")); continue; }
                var isDir = Directory.Exists(item.TrashLocation);
                var indexFile = Path.Combine(Path.GetDirectoryName(item.TrashLocation)!, "$I" + Path.GetFileName(item.TrashLocation)[2..]);
                // Names in the bin can be reused after it is emptied: the $I record must still point at the original path.
                if ((!isDir && !File.Exists(item.TrashLocation)) ||
                    ReadIndex(indexFile) is not { } entry || !string.Equals(entry.OriginalPath, item.OriginalPath, StringComparison.OrdinalIgnoreCase))
                {
                    failed.Add(new FailedItem(item.OriginalPath, "It is no longer in the Recycle Bin."));
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(item.OriginalPath)!);
                if (isDir) Directory.Move(item.TrashLocation, item.OriginalPath);
                else File.Move(item.TrashLocation, item.OriginalPath);
                restored.Add(item.OriginalPath);
                try { File.Delete(indexFile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(new FailedItem(item.OriginalPath, ex.Message));
            }
        }
        return new RestoreResult(restored, failed);
    }, ct);

    private static string? BinFolder(string path)
    {
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            var root = Path.GetPathRoot(path);
            if (sid is null || string.IsNullOrEmpty(root)) return null;
            var dir = Path.Combine(root, "$Recycle.Bin", sid);
            return dir; // the shell creates it on first use
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    private static IEnumerable<string> IndexFiles(string bin)
    {
        // $I files are hidden + system: enumerate without skipping those attributes.
        var opts = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true };
        try { return Directory.Exists(bin) ? Directory.GetFiles(bin, "$I*", opts) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static long SizeOf(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            return new DirectoryInfo(path).EnumerateFiles("*", opts).Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    internal static class Shell
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHQueryRecycleBinW(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

        private const uint FO_DELETE = 3;
        private const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040,
            FOF_NOCONFIRMMKDIR = 0x0200, FOF_NOERRORUI = 0x0400, FOF_WANTNUKEWARNING = 0x4000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCTW
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string? pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string? lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW op);

        /// <summary>
        /// FOF_ALLOWUNDO sends to the Recycle Bin; FOF_WANTNUKEWARNING makes Windows ask instead of silently deleting
        /// permanently when an item is too big for the bin.
        /// </summary>
        public static int Recycle(string path, out bool aborted)
        {
            var op = new SHFILEOPSTRUCTW
            {
                wFunc = FO_DELETE,
                pFrom = path + "\0\0",
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_NOCONFIRMMKDIR | FOF_WANTNUKEWARNING),
            };
            var rc = SHFileOperationW(ref op);
            aborted = op.fAnyOperationsAborted;
            return rc;
        }
    }
}
