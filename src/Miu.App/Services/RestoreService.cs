using Miu.Core.Sync;
using Miu.Platform.Portable;

namespace Miu.App.Services;

/// <param name="RelPath">'/'-separated, from the backup's root.</param>
public sealed record BackupTreeEntry(string Name, string RelPath, bool IsDir, long Size, DateTime ModifiedUtc);

/// <param name="FullPath">The file to copy back.</param>
/// <param name="Label">"Current backup" or "Set aside 2026-11-05 093000".</param>
public sealed record BackupFileVersion(string FullPath, string Label, long Size, DateTime ModifiedUtc, bool IsCurrent);

/// <param name="Restored">Files written.</param>
/// <param name="Renamed">Files written under a "(restored date)" name because a different file was already there.</param>
public sealed record FileRestoreOutcome(int Restored, int Renamed, int Skipped, IReadOnlyList<string> Problems)
{
    public string Summary =>
        (Restored == 0 && Problems.Count > 0 ? "Nothing was restored." : $"Restored {Restored} {(Restored == 1 ? "file" : "files")}.")
        + (Renamed > 0 ? $" {Renamed} kept a “(restored …)” name because a different file was already there." : "")
        + (Skipped > 0 ? $" {Skipped} were already the same and were left alone." : "")
        + (Problems.Count > 0 ? " " + string.Join("; ", Problems.Take(3)) : "");
}

/// <summary>
/// Browse a mirror or drive backup and put files back. Restoring never overwrites a different local file: it writes
/// "name (restored YYYY-MM-DD).ext" next to it instead. Copies land in a .miu-partial file first.
/// </summary>
public sealed class RestoreService(Func<DateTime> nowLocal)
{
    /// <summary>What's in one folder of the backup (newest versions; Miu's own set-aside folder is hidden).</summary>
    public IReadOnlyList<BackupTreeEntry> List(SyncJob job, string relFolder)
    {
        var dir = Full(job.Target, relFolder);
        var list = new List<BackupTreeEntry>();
        try
        {
            foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                if (relFolder.Length == 0 && e.Name.Equals(SyncLister.TrashFolder, StringComparison.OrdinalIgnoreCase)) continue;
                if (e.Name.EndsWith(SyncLister.PartialSuffix, StringComparison.Ordinal)) continue;
                var rel = relFolder.Length == 0 ? e.Name : relFolder + "/" + e.Name;
                list.Add(e is FileInfo f ? new BackupTreeEntry(e.Name, rel, false, f.Length, f.LastWriteTimeUtc) : new BackupTreeEntry(e.Name, rel, true, 0, e.LastWriteTimeUtc));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list.OrderByDescending(e => e.IsDir).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The backup's copy of a file, plus every older copy Miu set aside (newest first).</summary>
    public IReadOnlyList<BackupFileVersion> Versions(SyncJob job, string relFile)
    {
        var list = new List<BackupFileVersion>();
        var current = Full(job.Target, relFile);
        if (File.Exists(current))
        {
            var f = new FileInfo(current);
            list.Add(new BackupFileVersion(current, "Current backup", f.Length, f.LastWriteTimeUtc, true));
        }
        var trash = Path.Combine(job.Target, SyncLister.TrashFolder);
        try
        {
            if (Directory.Exists(trash))
                foreach (var stamp in Directory.GetDirectories(trash).OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal))
                {
                    var old = Full(stamp, relFile);
                    if (!File.Exists(old)) continue;
                    var f = new FileInfo(old);
                    list.Add(new BackupFileVersion(old, "Set aside " + Path.GetFileName(stamp), f.Length, f.LastWriteTimeUtc, false));
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list;
    }

    /// <summary>
    /// Puts a file or a whole folder back. <paramref name="destinationRoot"/> is the job's source (its original place)
    /// or another folder you picked; <paramref name="version"/> restores one older copy of a single file.
    /// </summary>
    public Task<FileRestoreOutcome> RestoreAsync(SyncJob job, string relPath, string destinationRoot, BackupFileVersion? version = null,
        CancellationToken ct = default) => Task.Run(() =>
    {
        var restored = 0;
        var renamed = 0;
        var skipped = 0;
        var problems = new List<string>();
        if (relPath.Split('/').Any(p => p is ".." or ".")) return new FileRestoreOutcome(0, 0, 0, ["That path isn't inside the backup."]);
        var from = version?.FullPath ?? Full(job.Target, relPath);
        IEnumerable<(string From, string Rel)> files = Directory.Exists(from)
            ? Directory.EnumerateFiles(from, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Where(f => !f.EndsWith(SyncLister.PartialSuffix, StringComparison.Ordinal))
                .Select(f => (f, relPath + "/" + Path.GetRelativePath(from, f).Replace('\\', '/')))
            : File.Exists(from) ? [(from, relPath)] : [];
        var date = DateOnly.FromDateTime(nowLocal());
        foreach (var (src, rel) in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var dest = Full(destinationRoot, rel);
                if (File.Exists(dest))
                {
                    if (SameFile(src, dest)) { skipped++; continue; }
                    // Never overwrite a different file: keep both.
                    dest = Full(destinationRoot, ConflictNames.For(rel, date, r => File.Exists(Full(destinationRoot, r)), "restored"));
                    renamed++;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                var partial = dest + SyncLister.PartialSuffix;
                File.Copy(src, partial, overwrite: true);
                File.SetLastWriteTimeUtc(partial, File.GetLastWriteTimeUtc(src));
                File.Move(partial, dest, overwrite: false);
                restored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{Path.GetFileName(rel)}: {ex.Message}");
            }
        }
        if (restored == 0 && skipped == 0 && problems.Count == 0) problems.Add("There was nothing to restore there.");
        return new FileRestoreOutcome(restored, renamed, skipped, problems);
    }, ct);

    private static bool SameFile(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        return fa.Length == fb.Length && Math.Abs((fa.LastWriteTimeUtc - fb.LastWriteTimeUtc).TotalSeconds) <= 2;
    }

    /// <summary>A '/'-separated relative path under a root, refusing anything that climbs out of it.</summary>
    public static string Full(string root, string rel)
    {
        var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        var r = Path.GetFullPath(root);
        var withSep = r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar;
        if (full != r && !full.StartsWith(withSep, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("That path isn't inside the backup.");
        return full;
    }
}
