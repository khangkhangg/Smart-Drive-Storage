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
            ct.ThrowIfCancellationRequested();
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
            progress?.Report(++i);
            if (!File.Exists(path) && !Directory.Exists(path)) { failed.Add(new FailedItem(path, "It is no longer there (moved or deleted after the scan).")); continue; }
            var bytes = SizeOf(path);
            var bin = BinFolder(path);
            var before = bin is null ? [] : IndexFiles(bin).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var rc = Shell.Recycle(path, out var aborted);
            if (rc != 0 || aborted || File.Exists(path) || Directory.Exists(path))
            {
                failed.Add(new FailedItem(path, rc == 0 ? "Cancelled, or it is in use." : $"It is in use or can't be moved (error 0x{rc:X})."));
                continue;
            }

            var trashLocation = "";
            if (bin is not null)
                foreach (var index in IndexFiles(bin).Where(f => !before.Contains(f)))
                    if (RecycleBinIndex.Parse(File.ReadAllBytes(index)) is { } e && string.Equals(e.OriginalPath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        trashLocation = Path.Combine(bin, "$R" + Path.GetFileName(index)[2..]);
                        break;
                    }
            done.Add(new RecycledItem(path, trashLocation, bytes));
        }
        return new RecycleResult(done, failed);
    }, ct);

    public Task<RestoreResult> RestoreAsync(IReadOnlyList<RecycledItem> items, CancellationToken ct) => Task.Run(() =>
    {
        var restored = new List<string>();
        var failed = new List<FailedItem>();
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrEmpty(item.TrashLocation)) { failed.Add(new FailedItem(item.OriginalPath, "Restore it from the Recycle Bin.")); continue; }
                if (File.Exists(item.OriginalPath) || Directory.Exists(item.OriginalPath)) { failed.Add(new FailedItem(item.OriginalPath, "Something with that name already exists there.")); continue; }
                var isDir = Directory.Exists(item.TrashLocation);
                if (!isDir && !File.Exists(item.TrashLocation)) { failed.Add(new FailedItem(item.OriginalPath, "It is no longer in the Recycle Bin.")); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(item.OriginalPath)!);
                if (isDir) Directory.Move(item.TrashLocation, item.OriginalPath);
                else File.Move(item.TrashLocation, item.OriginalPath);
                var index = Path.Combine(Path.GetDirectoryName(item.TrashLocation)!, "$I" + Path.GetFileName(item.TrashLocation)[2..]);
                try { File.Delete(index); } catch (IOException) { }
                restored.Add(item.OriginalPath);
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
        try { return Directory.Exists(bin) ? Directory.GetFiles(bin, "$I*") : []; }
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

    private static class Shell
    {
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
