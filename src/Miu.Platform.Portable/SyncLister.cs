using Miu.Core.Sync;

namespace Miu.Platform.Portable;

/// <param name="Entries">null when the root itself is missing or unreadable.</param>
/// <param name="UnreadableDirs">Folders (relative, '/'-separated) Miu couldn't list: their files are unknown, not gone.</param>
public sealed record SyncListing(IReadOnlyList<SyncEntry>? Entries, IReadOnlyList<string> UnreadableDirs);

/// <summary>Lists every file under a sync root. Links are not followed; Miu's own trash and partial files are skipped.</summary>
public static class SyncLister
{
    public const string TrashFolder = ".miu-sync-trash";
    public const string PartialSuffix = ".miu-partial";
    private static readonly HashSet<string> Junk = new(StringComparer.OrdinalIgnoreCase) { ".DS_Store", "Thumbs.db", "desktop.ini" };

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
                if ((c.Attributes & FileAttributes.ReparsePoint) != 0 || c.LinkTarget is not null) continue;
                var childRel = rel.Length == 0 ? c.Name : rel + "/" + c.Name;
                if (c is DirectoryInfo d)
                {
                    if (rel.Length == 0 && c.Name.Equals(TrashFolder, StringComparison.OrdinalIgnoreCase)) continue;
                    stack.Push((d, childRel));
                }
                else if (c is FileInfo f)
                {
                    if (f.Name.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase) || Junk.Contains(f.Name)) continue;
                    try { entries.Add(new SyncEntry(childRel, f.Length, f.LastWriteTimeUtc)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable.Add(childRel); }
                }
            }
        }
        return new SyncListing(entries, unreadable);
    }
}
