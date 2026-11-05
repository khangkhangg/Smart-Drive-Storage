using System.IO.Hashing;
using Miu.Core.Scanning;

namespace Miu.Platform.Portable;

/// <summary>Identical files. <see cref="KeepNode"/> is the copy Miu suggests keeping (the oldest; then the shortest path).</summary>
public sealed record DuplicateGroup(long Size, IReadOnlyList<int> Nodes, int KeepNode)
{
    public long WastedBytes => Size * (Nodes.Count - 1);
}

/// <summary>
/// Finds identical files in a scan: group by size, then compare a quick hash of the first and last 64 KB, then a full
/// XxHash128 of the whole file. Files that change or disappear while hashing are skipped. Installed packages and
/// repositories (.git, node_modules, virtual environments) are left out — duplicates there are expected.
/// </summary>
public static class DuplicateFinder
{
    private const int Edge = 64 * 1024;

    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
        { ".git", "node_modules", ".venv", "venv", "site-packages", "dist-packages", ".pnpm-store", "$Recycle.Bin", "Trash" };

    public static Task<IReadOnlyList<DuplicateGroup>> FindAsync(ScanTree tree, int nodeId, long minBytes, IProgress<int>? progress, CancellationToken ct) =>
        Task.Run<IReadOnlyList<DuplicateGroup>>(() =>
        {
            var bySize = Files(tree, nodeId)
                .Where(id => tree.Size(id) >= minBytes && !tree.IsLink(id))
                .GroupBy(tree.Size)
                .Where(g => g.Count() > 1)
                .ToList();

            var groups = new List<DuplicateGroup>();
            var done = 0;
            foreach (var sameSize in bySize)
            {
                ct.ThrowIfCancellationRequested();
                var size = sameSize.Key;
                foreach (var quick in sameSize.Select(id => (Id: id, Hash: Hash(tree.GetFullPath(id), size, partial: true)))
                             .Where(x => x.Hash is not null).GroupBy(x => x.Hash!).Where(g => g.Count() > 1))
                {
                    var candidates = quick.Select(x => x.Id).ToList();
                    // Small files: the quick hash already covered every byte.
                    var full = size <= 2L * Edge
                        ? [candidates]
                        : candidates.Select(id => (Id: id, Hash: Hash(tree.GetFullPath(id), size, partial: false)))
                            .Where(x => x.Hash is not null).GroupBy(x => x.Hash!).Where(g => g.Count() > 1)
                            .Select(g => g.Select(x => x.Id).ToList()).ToList();
                    foreach (var same in full)
                        groups.Add(new DuplicateGroup(size, same, PickKeep(tree, same)));
                }
                progress?.Report(++done);
            }
            return groups.OrderByDescending(g => g.WastedBytes).ToList();
        }, ct);

    private static int PickKeep(ScanTree tree, List<int> ids) =>
        ids.OrderBy(tree.ModifiedUtc).ThenBy(id => tree.GetFullPath(id).Length).ThenBy(tree.GetFullPath, StringComparer.Ordinal).First();

    private static IEnumerable<int> Files(ScanTree tree, int nodeId)
    {
        var stack = new Stack<int>();
        stack.Push(nodeId);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!tree.IsDir(id)) { yield return id; continue; }
            if (id != nodeId && Skip.Contains(tree.Name(id))) continue;
            foreach (var c in tree.Children(id)) stack.Push(c);
        }
    }

    /// <summary>Null when the file is gone, unreadable, or no longer the size the scan saw.</summary>
    private static string? Hash(string path, long expectedSize, bool partial)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            if (fs.Length != expectedSize) return null;
            var h = new XxHash128();
            if (partial)
            {
                var buf = new byte[Edge];
                var n = fs.Read(buf, 0, (int)Math.Min(Edge, fs.Length));
                h.Append(buf.AsSpan(0, n));
                if (fs.Length > Edge)
                {
                    fs.Seek(Math.Max(Edge, fs.Length - Edge), SeekOrigin.Begin);
                    n = fs.Read(buf, 0, buf.Length);
                    h.Append(buf.AsSpan(0, n));
                }
            }
            else h.Append(fs);
            return Convert.ToHexString(h.GetCurrentHash());
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
