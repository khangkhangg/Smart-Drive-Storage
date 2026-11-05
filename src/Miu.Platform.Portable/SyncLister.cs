using Miu.Core.Sync;

namespace Miu.Platform.Portable;

/// <param name="Entries">null when the root itself is missing or unreadable.</param>
/// <param name="UnreadableDirs">Folders (relative, '/'-separated) Miu couldn't list: their files are unknown, not gone.</param>
/// <param name="Links">Links (symlinks, junctions) inside the root. Miu never follows them, and nothing at or under them is touched.</param>
public sealed record SyncListing(IReadOnlyList<SyncEntry>? Entries, IReadOnlyList<string> UnreadableDirs, IReadOnlyList<string>? Links = null)
{
    public IEnumerable<string> UnknownPaths => UnreadableDirs.Concat(Links ?? []);
}

/// <summary>Lists every file under a sync root. Links are not followed; Miu's own trash and partial files are skipped.</summary>
public static class SyncLister
{
    public const string TrashFolder = ".miu-sync-trash";
    public const string PartialSuffix = ".miu-partial";

    /// <summary>Only Miu's own partial files ("name.&lt;size hex&gt;-&lt;mtime hex&gt;.miu-partial"); other names ending in .miu-partial are your files.</summary>
    public static readonly System.Text.RegularExpressions.Regex OwnPartial = new(@"\.[0-9a-f]+-[0-9a-f]+\.miu-partial$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether this folder's file system ignores upper/lower case (FAT, exFAT, NTFS, most SMB shares), found by
    /// creating and removing a tiny probe file. Falls back to the operating system's usual behaviour if Miu can't write.
    /// </summary>
    public static bool IsCaseInsensitive(string root)
    {
        var name = ".miu-case-Probe-" + Guid.NewGuid().ToString("N")[..8];
        var probe = Path.Combine(root, name);
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
                return File.Exists(Path.Combine(root, name.ToUpperInvariant())) && File.Exists(Path.Combine(root, name.ToLowerInvariant()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(); }
    }
    private static readonly HashSet<string> Junk = new(StringComparer.OrdinalIgnoreCase) { ".DS_Store", "Thumbs.db", "desktop.ini" };

    /// <summary>XxHash128 of a file's content, or null if it can't be read.</summary>
    public static async Task<UInt128?> HashAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
            var h = new System.IO.Hashing.XxHash128();
            await h.AppendAsync(fs, ct);
            return h.GetCurrentHashAsUInt128();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static Task<SyncListing> ListAsync(string root, CancellationToken ct) => Task.Run(() => List(root, ct), ct);

    public static SyncListing List(string root, CancellationToken ct)
    {
        try
        {
            var info = new DirectoryInfo(root);
            if (!info.Exists) return new SyncListing(null, []);
            _ = info.EnumerateFileSystemInfos().GetEnumerator().MoveNext(); // the root itself must be readable
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return new SyncListing(null, []); }

        var entries = new List<SyncEntry>();
        var unreadable = new List<string>();
        var links = new List<string>();
        var stack = new Stack<(DirectoryInfo Dir, string Rel)>();
        stack.Push((new DirectoryInfo(root), ""));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, rel) = stack.Pop();
            FileSystemInfo[] children;
            try { children = dir.GetFileSystemInfos(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                unreadable.Add(rel);
                continue;
            }
            foreach (var c in children)
            {
                var childRel = rel.Length == 0 ? c.Name : rel + "/" + c.Name;
                bool isLink;
                try { isLink = (c.Attributes & FileAttributes.ReparsePoint) != 0 || c.LinkTarget is not null; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { isLink = true; }
                if (isLink)
                {
                    links.Add(childRel);
                    continue;
                }
                if (c is DirectoryInfo d)
                {
                    if (rel.Length == 0 && c.Name.Equals(TrashFolder, StringComparison.OrdinalIgnoreCase)) continue;
                    stack.Push((d, childRel));
                }
                else if (c is FileInfo f)
                {
                    if (OwnPartial.IsMatch(f.Name) || Junk.Contains(f.Name) || f.Name.StartsWith(".miu-case-Probe-", StringComparison.Ordinal)) continue;
                    try { entries.Add(new SyncEntry(childRel, f.Length, f.LastWriteTimeUtc)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable.Add(childRel); }
                }
            }
        }
        return new SyncListing(entries, unreadable, links);
    }
}
