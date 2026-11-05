namespace Miu.Platform.Abstractions;

/// <summary>An item now in the Recycle Bin / Trash, with what is needed to put it back.</summary>
public sealed record RecycledItem(string OriginalPath, string TrashLocation, long Bytes);

public sealed record FailedItem(string Path, string Reason);

public sealed record RecycleResult(IReadOnlyList<RecycledItem> Recycled, IReadOnlyList<FailedItem> Failed)
{
    public long FreedBytes => Recycled.Sum(r => r.Bytes);
}

public sealed record RestoreResult(IReadOnlyList<string> Restored, IReadOnlyList<FailedItem> Failed);

/// <summary>The operating system's Recycle Bin (Windows) or Trash (Linux, macOS). Never deletes permanently.</summary>
public interface IRecycleBin
{
    bool IsSupported { get; }
    Task<RecycleResult> RecycleAsync(IReadOnlyList<string> paths, IProgress<int>? progress, CancellationToken ct);
    Task<RestoreResult> RestoreAsync(IReadOnlyList<RecycledItem> items, CancellationToken ct);
}

/// <summary>Used where no recycle implementation exists yet (macOS until Phase 9).</summary>
public sealed class UnsupportedRecycleBin : IRecycleBin
{
    public bool IsSupported => false;

    public Task<RecycleResult> RecycleAsync(IReadOnlyList<string> paths, IProgress<int>? progress, CancellationToken ct) =>
        Task.FromResult(new RecycleResult([], paths.Select(p => new FailedItem(p, "Cleaning isn't available on this system yet.")).ToList()));

    public Task<RestoreResult> RestoreAsync(IReadOnlyList<RecycledItem> items, CancellationToken ct) =>
        Task.FromResult(new RestoreResult([], items.Select(i => new FailedItem(i.OriginalPath, "Not supported.")).ToList()));
}
