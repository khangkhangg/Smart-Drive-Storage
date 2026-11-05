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
            actionId = services.Actions.Add("recycle",
                result.Recycled.Select(r => new ActionItem(r.OriginalPath, r.TrashLocation, r.Bytes)).ToList(), origin);
        // Items already gone from disk shouldn't linger in the cart or the scan either.
        var gone = result.Recycled.Select(r => r.OriginalPath)
            .Concat(result.Failed.Select(f => f.Path).Where(p => !File.Exists(p) && !Directory.Exists(p)))
            .ToList();
        if (gone.Count > 0)
        {
            services.Cart.RemoveWithin(gone);
            session?.RemoveAndRefresh(gone);
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
        // Keep whatever couldn't be restored, so Undo can be tried again after the conflict is fixed.
        var left = action.Items.Where(i => !result.Restored.Contains(i.OriginalPath)).ToList();
        if (left.Count == 0) services.Actions.MarkUndone(actionId);
        else if (left.Count < action.Items.Count) services.Actions.UpdateItems(actionId, left);
        return result;
    }
}
