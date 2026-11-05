using System.Globalization;
using Miu.Platform.Abstractions;

namespace Miu.Platform.Portable;

/// <summary>
/// The XDG / freedesktop.org Trash used by Linux desktops (GNOME Files, Dolphin…): items move into
/// <c>Trash/files</c> and a <c>.trashinfo</c> file records where they came from, so the file manager can restore them too.
/// </summary>
public sealed class FreedesktopTrash : IRecycleBin
{
    private readonly string _files;
    private readonly string _info;

    public FreedesktopTrash(string? trashRoot = null)
    {
        trashRoot ??= DefaultRoot();
        _files = Path.Combine(trashRoot, "files");
        _info = Path.Combine(trashRoot, "info");
    }

    public static string DefaultRoot()
    {
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(data))
            data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(data, "Trash");
    }

    public bool IsSupported => true;

    public Task<RecycleResult> RecycleAsync(IReadOnlyList<string> paths, IProgress<int>? progress, CancellationToken ct) => Task.Run(() =>
    {
        Directory.CreateDirectory(_files);
        Directory.CreateDirectory(_info);
        var done = new List<RecycledItem>();
        var failed = new List<FailedItem>();
        var i = 0;
        foreach (var raw in paths)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
            string? reserved = null;
            try
            {
                if (!FileSizes.Exists(path)) { failed.Add(new FailedItem(path, "It is no longer there (moved or deleted after the scan).")); continue; }
                if (!SameVolume(path, _files)) { failed.Add(new FailedItem(path, "Can't move to Trash across drives.")); continue; }
                var bytes = FileSizes.Of(path);
                reserved = ReserveName(Path.GetFileName(path), path);
                var target = Path.Combine(_files, reserved);
                if (Directory.Exists(path) && !IsLink(path)) Directory.Move(path, target);
                else File.Move(path, target);
                done.Add(new RecycledItem(path, target, bytes));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(new FailedItem(path, ex is UnauthorizedAccessException ? "Permission denied." : "It is in use or can't be moved: " + ex.Message));
                if (reserved is not null) TryDeleteInfo(reserved);
            }
            progress?.Report(++i);
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
                if (FileSizes.Exists(item.OriginalPath)) { failed.Add(new FailedItem(item.OriginalPath, "Something with that name already exists there.")); continue; }
                if (!FileSizes.Exists(item.TrashLocation)) { failed.Add(new FailedItem(item.OriginalPath, "It is no longer in the Trash.")); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(item.OriginalPath)!);
                if (Directory.Exists(item.TrashLocation) && !IsLink(item.TrashLocation)) Directory.Move(item.TrashLocation, item.OriginalPath);
                else File.Move(item.TrashLocation, item.OriginalPath);
                TryDeleteInfo(Path.GetFileName(item.TrashLocation));
                restored.Add(item.OriginalPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(new FailedItem(item.OriginalPath, ex.Message));
            }
        }
        return new RestoreResult(restored, failed);
    }, ct);

    /// <summary>Creates the .trashinfo file exclusively (as the spec requires) and returns the name it reserved.</summary>
    private string ReserveName(string name, string originalPath)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        if (stem.Length == 0) { stem = name; ext = ""; }
        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? name : $"{stem}.{n}{ext}";
            if (FileSizes.Exists(Path.Combine(_files, candidate))) continue;
            try
            {
                using var fs = new FileStream(Path.Combine(_info, candidate + ".trashinfo"), FileMode.CreateNew);
                using var w = new StreamWriter(fs);
                w.Write("[Trash Info]\nPath=" + new Uri(originalPath).AbsolutePath + "\nDeletionDate=" +
                        DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "\n");
                return candidate;
            }
            catch (IOException) when (File.Exists(Path.Combine(_info, candidate + ".trashinfo"))) { }
        }
    }

    private void TryDeleteInfo(string name)
    {
        try { File.Delete(Path.Combine(_info, name + ".trashinfo")); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static bool IsLink(string path)
    {
        try { return new FileInfo(path).LinkTarget is not null; } catch { return false; }
    }

    /// <summary>Two paths are on the same volume when their deepest mount point is the same.</summary>
    private static bool SameVolume(string a, string b)
    {
        string[] mounts;
        try { mounts = DriveInfo.GetDrives().Select(d => d.RootDirectory.FullName).ToArray(); }
        catch { return true; }
        string MountOf(string p) => mounts.Where(m => p.StartsWith(m, StringComparison.Ordinal)).OrderByDescending(m => m.Length).FirstOrDefault() ?? "/";
        return MountOf(a) == MountOf(b);
    }
}
