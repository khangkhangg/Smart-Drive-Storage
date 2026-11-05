using Miu.Core.Rules;
using Miu.Platform.Abstractions;

namespace Miu.Platform.Portable;

/// <summary>
/// The only way Miu deletes anything. Every path is re-checked against the protected-path guard at deletion time
/// (including where a link or a linked parent folder really points), and missing items are reported instead of
/// failing the whole batch. Only what passes reaches the Recycle Bin.
/// </summary>
public sealed class SafeRecycler(IRecycleBin inner, ProtectedPathGuard guard) : IRecycleBin
{
    public bool IsSupported => inner.IsSupported;

    public async Task<RecycleResult> RecycleAsync(IReadOnlyList<string> paths, IProgress<int>? progress, CancellationToken ct)
    {
        var allowed = new List<string>();
        var refused = new List<FailedItem>();
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) { refused.Add(new FailedItem(raw, "Empty path.")); continue; }
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
            if (Refusal(path) is { } why) { refused.Add(new FailedItem(path, why)); continue; }
            if (!File.Exists(path) && !Directory.Exists(path) && new FileInfo(path).LinkTarget is null)
            {
                refused.Add(new FailedItem(path, "It is no longer there (moved or deleted after the scan)."));
                continue;
            }
            allowed.Add(path);
        }

        var result = allowed.Count == 0 ? new RecycleResult([], []) : await inner.RecycleAsync(allowed, progress, ct);
        return new RecycleResult(result.Recycled, [.. refused, .. result.Failed]);
    }

    public Task<RestoreResult> RestoreAsync(IReadOnlyList<RecycledItem> items, CancellationToken ct) => inner.RestoreAsync(items, ct);

    private string? Refusal(string path)
    {
        if (guard.Explain(path, checkLink: true) is { } why) return "This is a protected location. " + why;
        // A linked parent folder could point into a protected tree: judge the real location too.
        var parent = Path.GetDirectoryName(path);
        if (parent is not null)
        {
            var real = Path.Combine(ManagedScanner.RealPath(parent), Path.GetFileName(path));
            if (!string.Equals(real, path, StringComparison.Ordinal) && guard.Explain(real, checkLink: false) is { } why2)
                return "This is a protected location. " + why2;
        }
        return null;
    }
}
