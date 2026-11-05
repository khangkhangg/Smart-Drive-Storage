using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
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
            // Stop between items, but always report what already moved so it is logged and can be undone.
            if (ct.IsCancellationRequested) break;
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
            string? reserved = null;
            try
            {
                if (!FileSizes.Exists(path)) { failed.Add(new FailedItem(path, "It is no longer there (moved or deleted after the scan).")); continue; }
                if (!SameVolume(path, _files)) { failed.Add(new FailedItem(path, "Can't move to Trash across drives.")); continue; }
                var bytes = FileSizes.Of(path);
                reserved = ReserveName(Path.GetFileName(path), path);
                var target = Path.Combine(_files, reserved);
                Move(path, target);
                done.Add(new RecycledItem(path, target, bytes));
            }
            catch (Exception ex)
            {
                failed.Add(new FailedItem(path, ex is UnauthorizedAccessException ? "Permission denied." : ex.Message));
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
            if (ct.IsCancellationRequested) break;
            try
            {
                if (FileSizes.Exists(item.OriginalPath)) { failed.Add(new FailedItem(item.OriginalPath, "Something with that name already exists there.")); continue; }
                // The trash name can be reused after the Trash is emptied: only restore if the .trashinfo still
                // says this entry came from the original path.
                if (!FileSizes.Exists(item.TrashLocation) || RecordedOrigin(Path.GetFileName(item.TrashLocation)) != item.OriginalPath)
                {
                    failed.Add(new FailedItem(item.OriginalPath, "It is no longer in the Trash."));
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(item.OriginalPath)!);
                Move(item.TrashLocation, item.OriginalPath);
                TryDeleteInfo(Path.GetFileName(item.TrashLocation));
                restored.Add(item.OriginalPath);
            }
            catch (Exception ex)
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
                w.Write("[Trash Info]\nPath=" + Encode(originalPath) + "\nDeletionDate=" +
                        DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "\n");
                return candidate;
            }
            catch (IOException) when (File.Exists(Path.Combine(_info, candidate + ".trashinfo"))) { }
        }
    }

    /// <summary>Percent-encodes UTF-8 bytes as the trash spec asks (RFC 2396), keeping '/' and unreserved characters.</summary>
    public static string Encode(string path)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(path))
        {
            var c = (char)b;
            if (b < 128 && (char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.' or '~')) sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private string? RecordedOrigin(string name)
    {
        try
        {
            var line = File.ReadLines(Path.Combine(_info, name + ".trashinfo")).FirstOrDefault(l => l.StartsWith("Path=", StringComparison.Ordinal));
            return line is null ? null : Uri.UnescapeDataString(line[5..]);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Moves without ever copying: on Linux/macOS this is rename(2), which moves links as links and fails across
    /// file systems instead of silently copying.
    /// </summary>
    private static void Move(string from, string to)
    {
        if (OperatingSystem.IsWindows())
        {
            if (Directory.Exists(from) && !IsLink(from)) Directory.Move(from, to);
            else File.Move(from, to);
            return;
        }
        if (rename(from, to) == 0) return;
        var errno = Marshal.GetLastPInvokeError();
        throw new IOException(errno switch
        {
            18 => "Can't move to Trash across drives.", // EXDEV
            13 or 1 => "Permission denied.",             // EACCES, EPERM
            16 or 26 => "It is in use.",                 // EBUSY, ETXTBSY
            _ => $"It can't be moved (error {errno}).",
        });
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int rename([MarshalAs(UnmanagedType.LPUTF8Str)] string oldpath, [MarshalAs(UnmanagedType.LPUTF8Str)] string newpath);

    private void TryDeleteInfo(string name)
    {
        try { File.Delete(Path.Combine(_info, name + ".trashinfo")); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static bool IsLink(string path)
    {
        try { return new FileInfo(path).LinkTarget is not null; } catch { return false; }
    }

    /// <summary>Two paths are on the same volume when their deepest mount point is the same (rename(2) still guards).</summary>
    private static bool SameVolume(string a, string b)
    {
        string[] mounts;
        try { mounts = DriveInfo.GetDrives().Select(d => d.RootDirectory.FullName).ToArray(); }
        catch { return true; }
        return MountOf(a, mounts) == MountOf(b, mounts);
    }

    /// <summary>The deepest mount point containing the path, matching whole segments ("/home" doesn't contain "/homework").</summary>
    public static string MountOf(string path, IEnumerable<string> mounts)
    {
        var best = "/";
        foreach (var raw in mounts)
        {
            var m = raw.Length > 1 ? raw.TrimEnd('/', '\\') : raw;
            var inside = path == m
                         || (m is "/" && path.StartsWith('/'))
                         || path.StartsWith(m + "/", StringComparison.Ordinal)
                         || path.StartsWith(m + "\\", StringComparison.Ordinal);
            if (inside && m.Length > best.Length) best = m;
        }
        return best;
    }
}
