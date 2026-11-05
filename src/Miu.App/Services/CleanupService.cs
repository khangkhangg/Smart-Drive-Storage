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
        // Rules can change after an item went into the cart: never clean something you now keep.
        var blocked = new List<FailedItem>();
        if (session is not null)
        {
            var allowed = new List<CartItem>();
            foreach (var i in items)
                if (session.KeepConflict(i.Path) is { } why) blocked.Add(new FailedItem(i.Path, why));
                else allowed.Add(i);
            items = allowed;
        }
        // Never throw away work git doesn't have yet: uncommitted or new files inside the item, or (for a whole
        // repository) commits that were never pushed.
        var git = services.NewGit();
        var allowedByGit = new List<CartItem>();
        foreach (var i in items)
        {
            if (await GitConflictAsync(git, i.Path, ct) is { } why) blocked.Add(new FailedItem(i.Path, why));
            else allowedByGit.Add(i);
        }
        items = allowedByGit;
        var dirs = items.Where(i => session?.FindNode(i.Path) is { } id ? session.Tree.IsDir(id) : Directory.Exists(i.Path))
            .Select(i => i.Path).ToHashSet();
        var recycleResult = await services.Recycler.RecycleAsync(items.Select(i => i.Path).ToList(), progress, ct);
        var result = new RecycleResult(recycleResult.Recycled, [.. blocked, .. recycleResult.Failed]);
        long actionId = 0;
        if (result.Recycled.Count > 0)
        {
            actionId = services.Actions.Add("recycle",
                result.Recycled.Select(r => new ActionItem(r.OriginalPath, r.TrashLocation, r.Bytes)).ToList(), origin);
            var recycled = result.Recycled.Select(r => r.OriginalPath).ToHashSet();
            new LearningService(services).RecordCleaned(items.Where(i => recycled.Contains(i.Path) && !i.FromDuplicates)
                .Select(i => (i.Path, dirs.Contains(i.Path), i.Verdict, i.Reason)));
        }
        // Items already gone from disk shouldn't linger in the cart or the scan either.
        var gone = result.Recycled.Select(r => r.OriginalPath)
            .Concat(recycleResult.Failed.Select(f => f.Path).Where(p => !File.Exists(p) && !Directory.Exists(p)))
            .ToList();
        if (gone.Count > 0)
        {
            services.Cart.RemoveWithin(gone);
            session?.RemoveAndRefresh(gone);
        }
        return new CleanupOutcome(actionId, result.FreedBytes, result.Recycled.Count, result.Failed);
    }

    /// <summary>
    /// Why this item can't be cleaned without losing work git doesn't have, or null. Checks the repository the item is
    /// in, and (for folders) repositories inside it.
    /// </summary>
    private static async Task<string?> GitConflictAsync(Miu.Dev.Git.GitService git, string path, CancellationToken ct)
    {
        if (Miu.Dev.Git.GitStatus.FindRoot(path) is { } root && await RepoConflictAsync(git, root, path, ct) is { } why) return why;
        // Rebuildable folders (node_modules, build output, caches) don't hold repositories worth looking for.
        if (!Directory.Exists(path) || Rebuildable.Contains(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)))) return null;
        foreach (var inner in NestedRepos(path))
            if (await RepoConflictAsync(git, inner, inner, ct) is { } why2) return $"{Path.GetFileName(inner)} inside it: {why2}";
        return null;
    }

    private static async Task<string?> RepoConflictAsync(Miu.Dev.Git.GitService git, string root, string path, CancellationToken ct)
    {
        var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (rel == ".") rel = "";
        var check = await git.CheckAsync(root, ct);
        if (check.Problem is { } problem) return problem + " Clean it yourself if you're sure.";
        if (check.State is not { } state)
            return rel.Length == 0 ? "It's a git repository and git isn't installed, so Miu can't tell whether its work is saved anywhere else." : null;
        var work = state.WorkInside(rel);
        if (work.Count > 0) return $"It holds work you haven't committed in git ({work.Count} {(work.Count == 1 ? "file" : "files")}, e.g. {work[0]}).";
        if (rel.Length == 0 && state.HasUnpushed) return $"This repository has {state.Ahead} commits you haven't pushed.";
        if (rel.Length == 0 && state.HasNoUpstream) return "This repository's branch isn't pushed anywhere, so its history may exist only here.";
        return null;
    }

    private static readonly HashSet<string> Rebuildable = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".venv", "venv", "__pycache__", "build", "dist", "target", "bin", "obj", ".gradle", ".next", ".nuxt", ".cache", ".turbo", ".parcel-cache", "out", "coverage",
    };

    /// <summary>Git repositories inside a folder (a few levels deep; stops early in huge trees).</summary>
    private static IEnumerable<string> NestedRepos(string folder)
    {
        var queue = new Queue<(string Dir, int Depth)>([(folder, 0)]);
        var seen = 0;
        while (queue.Count > 0 && seen < 5000)
        {
            var (dir, depth) = queue.Dequeue();
            string[] children;
            try { children = Directory.GetDirectories(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var c in children)
            {
                seen++;
                var name = Path.GetFileName(c);
                if (name == ".git") { if (dir != folder) yield return dir; continue; }
                if (depth < 4 && name is not ("node_modules" or ".venv" or "venv" or "target" or "build" or "dist")) queue.Enqueue((c, depth + 1));
            }
            if (dir != folder && File.Exists(Path.Combine(dir, ".git"))) yield return dir; // worktrees and submodules
        }
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
        if (result.Restored.Count > 0)
        {
            var learning = new LearningService(services);
            if (action.Origin == "auto")
                // You undid something Miu cleaned on its own: keep it, so the next run doesn't clean it again.
                foreach (var p in result.Restored)
                    learning.Teach(p, Directory.Exists(p), Miu.Core.Learning.DecisionAction.Keep, "Restored after automatic cleaning", null);
            else
                learning.RecordRestored(result.Restored.Select(p => (p, Directory.Exists(p))));
        }
        if (left.Count == 0) services.Actions.MarkUndone(actionId);
        else if (left.Count < action.Items.Count) services.Actions.UpdateItems(actionId, left);
        return result;
    }
}
