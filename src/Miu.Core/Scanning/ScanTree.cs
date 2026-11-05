namespace Miu.Core.Scanning;

/// <summary>
/// In-memory result of a scan. Nodes are stored in flat arrays with first-child / next-sibling links
/// so millions of entries stay compact. <see cref="Add"/> is thread-safe; call
/// <see cref="ComputeAggregates"/> once the scan is complete.
/// </summary>
public sealed class ScanTree
{
    private struct Node
    {
        public int Parent;
        public int FirstChild;
        public int NextSibling;
        public string Name;
        public bool IsDir;
        public long Size;
        public int FileCount;
        public long ModifiedTicks;
        public long NewestTicks;
    }

    private readonly object _gate = new();
    private Node[] _nodes = new Node[1024];
    private int _count;
    private int[][]? _sortedChildren;

    public ScanTree(string rootPath)
    {
        RootPath = rootPath;
        _nodes[0] = new Node { Parent = -1, FirstChild = -1, NextSibling = -1, Name = rootPath, IsDir = true };
        _count = 1;
    }

    public int Root => 0;
    public string RootPath { get; }
    public int Count { get { lock (_gate) return _count; } }

    public int Add(int parentId, string name, bool isDir, long size, DateTime modifiedUtc)
    {
        lock (_gate)
        {
            if ((uint)parentId >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(parentId));
            if (_count == _nodes.Length) Array.Resize(ref _nodes, _nodes.Length * 2);
            var id = _count++;
            _nodes[id] = new Node
            {
                Parent = parentId,
                FirstChild = -1,
                NextSibling = _nodes[parentId].FirstChild,
                Name = name,
                IsDir = isDir,
                Size = isDir ? 0 : size,
                FileCount = isDir ? 0 : 1,
                ModifiedTicks = modifiedUtc.Ticks,
                NewestTicks = isDir ? 0 : modifiedUtc.Ticks,
            };
            _nodes[parentId].FirstChild = id;
            _sortedChildren = null;
            return id;
        }
    }

    /// <summary>Sums sizes and file counts bottom-up and sorts children by size.</summary>
    public void ComputeAggregates()
    {
        lock (_gate)
        {
            for (var i = 0; i < _count; i++)
            {
                if (!_nodes[i].IsDir) continue;
                _nodes[i].Size = 0;
                _nodes[i].FileCount = 0;
                _nodes[i].NewestTicks = 0;
            }
            // A child always has a higher id than its parent, so a reverse walk is a post-order walk.
            for (var i = _count - 1; i >= 0; i--)
            {
                ref var n = ref _nodes[i];
                if (n.IsDir && n.NewestTicks == 0) n.NewestTicks = n.ModifiedTicks;
                if (n.Parent < 0) continue;
                ref var p = ref _nodes[n.Parent];
                p.Size += n.Size;
                p.FileCount += n.FileCount;
                if (n.NewestTicks > p.NewestTicks) p.NewestTicks = n.NewestTicks;
            }

            var sorted = new int[_count][];
            for (var i = 0; i < _count; i++)
            {
                if (!_nodes[i].IsDir) { sorted[i] = []; continue; }
                var list = new List<int>();
                for (var c = _nodes[i].FirstChild; c >= 0; c = _nodes[c].NextSibling) list.Add(c);
                var nodes = _nodes;
                list.Sort((x, y) => nodes[y].Size.CompareTo(nodes[x].Size));
                sorted[i] = list.ToArray();
            }
            _sortedChildren = sorted;
        }
    }

    public string Name(int id) => _nodes[id].Name;
    public bool IsDir(int id) => _nodes[id].IsDir;
    public long Size(int id) => _nodes[id].Size;
    public int FileCount(int id) => _nodes[id].FileCount;
    public DateTime ModifiedUtc(int id) => new(_nodes[id].ModifiedTicks, DateTimeKind.Utc);
    public DateTime NewestModifiedUtc(int id) => new(_nodes[id].NewestTicks, DateTimeKind.Utc);
    public int Parent(int id) => _nodes[id].Parent;

    public IReadOnlyList<int> Children(int id)
    {
        var sorted = _sortedChildren;
        if (sorted is not null) return sorted[id];
        lock (_gate)
        {
            var list = new List<int>();
            for (var c = _nodes[id].FirstChild; c >= 0; c = _nodes[c].NextSibling) list.Add(c);
            list.Reverse();
            return list;
        }
    }

    public int? FindChild(int parentId, string name)
    {
        foreach (var c in Children(parentId))
            if (string.Equals(_nodes[c].Name, name, StringComparison.Ordinal)) return c;
        return null;
    }

    public string GetFullPath(int id)
    {
        if (id == Root) return RootPath;
        var parts = new Stack<string>();
        for (var n = id; n != Root; n = _nodes[n].Parent) parts.Push(_nodes[n].Name);
        return Path.Combine([RootPath, .. parts]);
    }
}
