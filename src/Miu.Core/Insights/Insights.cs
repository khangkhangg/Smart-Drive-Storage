using Miu.Core.Scanning;

namespace Miu.Core.Insights;

/// <summary>How much space each kind of file takes under a folder.</summary>
public static class TypeBreakdown
{
    public static IReadOnlyList<(FileCategory Category, long Bytes, int Files)> Compute(ScanTree tree, int nodeId)
    {
        var totals = new Dictionary<FileCategory, (long Bytes, int Files)>();
        foreach (var id in LargestFiles.Files(tree, nodeId))
        {
            var c = Categorizer.Categorize(tree.Name(id), false, tree.GetFullPath(id));
            var (b, f) = totals.GetValueOrDefault(c);
            totals[c] = (b + tree.Size(id), f + 1);
        }
        return totals.Select(kv => (kv.Key, kv.Value.Bytes, kv.Value.Files)).OrderByDescending(x => x.Bytes).ToList();
    }
}

public static class LargestFiles
{
    public static IReadOnlyList<int> Find(ScanTree tree, int nodeId, int count) =>
        Files(tree, nodeId).OrderByDescending(tree.Size).Take(count).ToList();

    /// <summary>Every file below a folder (links are recorded but not followed, so they count as their own size).</summary>
    internal static IEnumerable<int> Files(ScanTree tree, int nodeId)
    {
        var stack = new Stack<int>();
        stack.Push(nodeId);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!tree.IsDir(id)) { yield return id; continue; }
            foreach (var c in tree.Children(id)) stack.Push(c);
        }
    }
}
