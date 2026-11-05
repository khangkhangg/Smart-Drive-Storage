using Miu.Core.Rules;

namespace Miu.Core.Cleanup;

public sealed record CartItem(string Path, string Name, long Bytes, Verdict Verdict, string Reason);

/// <summary>
/// Items the user has chosen to clean. Keeps only top-level items: adding something inside an item already in the
/// cart does nothing, and adding a folder replaces anything inside it — so nothing is counted or deleted twice.
/// </summary>
public sealed class CleanupCart(PathStyle style)
{
    private readonly List<CartItem> _items = [];
    private readonly StringComparison _cmp = style == PathStyle.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly char _sep = PathNormalizer.Separator(style);

    public event EventHandler? Changed;

    public IReadOnlyList<CartItem> Items => _items;
    public long TotalBytes => _items.Sum(i => i.Bytes);
    public int Count => _items.Count;

    public bool Contains(string path) => _items.Any(i => Same(i.Path, path) || IsInside(path, i.Path));

    public void Add(CartItem item)
    {
        if (item.Verdict == Verdict.Protected)
            throw new InvalidOperationException("Protected items can't be cleaned.");
        if (Contains(item.Path)) return;
        _items.RemoveAll(i => IsInside(i.Path, item.Path));
        _items.Add(item);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void AddRange(IEnumerable<CartItem> items)
    {
        foreach (var i in items.Where(i => i.Verdict != Verdict.Protected)) Add(i);
    }

    public void Remove(string path)
    {
        if (_items.RemoveAll(i => Same(i.Path, path)) > 0) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveAll(IEnumerable<string> paths)
    {
        var set = paths.ToList();
        if (_items.RemoveAll(i => set.Any(p => Same(i.Path, p))) > 0) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool Same(string a, string b) => string.Equals(Trim(a), Trim(b), _cmp);

    private bool IsInside(string child, string parent)
    {
        var p = Trim(parent);
        return Trim(child).StartsWith(p + _sep, _cmp);
    }

    private string Trim(string p) => p.Replace(_sep == '\\' ? '/' : '\\', _sep).TrimEnd(_sep);
}
