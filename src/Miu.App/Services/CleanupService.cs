using Miu.Core.Cleanup;
using Miu.Data;
using Miu.Platform.Abstractions;

namespace Miu.App.Services;

public sealed record CleanupOutcome(long ActionId, long FreedBytes, int Count, IReadOnlyList<FailedItem> Failed);

/// <summary>Cleans a list of items through the guarded Recycle Bin, logs the action for undo, and updates the scan.</summary>
public sealed class CleanupService(AppServices services)
{
    public async Task<CleanupOutcome> CleanAsync(IReadOnlyList<CartItem> items, ScanSession? session, string origin,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var result = await services.Recycler.RecycleAsync(items.Select(i => i.Path).ToList(), progress, ct);
        long actionId = 0;
        if (result.Recycled.Count > 0)
        {
            actionId = services.Actions.Add("recycle",
                result.Recycled.Select(r => new ActionItem(r.OriginalPath, r.TrashLocation, r.Bytes)).ToList(), origin);
            var cleaned = result.Recycled.Select(r => r.OriginalPath).ToList();
            services.Cart.RemoveAll(cleaned);
            session?.RemoveAndRefresh(cleaned);
        }
        return new CleanupOutcome(actionId, result.FreedBytes, result.Recycled.Count, result.Failed);
    }

    /// <summary>Puts everything from one clean back where it was. Each action can be undone once.</summary>
    public async Task<RestoreResult> UndoAsync(long actionId, CancellationToken ct = default)
    {
        var action = services.Actions.Get(actionId);
        if (action is null || action.Undone) return new RestoreResult([], []);
        var result = await services.Recycler.RestoreAsync(
            action.Items.Select(i => new RecycledItem(i.OriginalPath, i.TrashLocation, i.Bytes)).ToList(), ct);
        if (result.Restored.Count > 0) services.Actions.MarkUndone(actionId);
        return result;
    }
}
