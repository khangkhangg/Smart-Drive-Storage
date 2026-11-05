using Miu.Core.Rules;
using Miu.Core.Sync;
using Miu.Data;
using Miu.Platform.Portable;

namespace Miu.App.Services;

/// <param name="Problem">Why the job can't run at all (bad folders), or null.</param>
public sealed record SyncPreview(SyncJob Job, SyncPlan Plan, SyncListing Source, SyncListing Target, string? Problem)
{
    public bool CanRun => Problem is null && Plan.Refusal is null && !Plan.IsEmpty;
    public string Text => Problem ?? Plan.Describe();
}

/// <param name="Status">"ok", "partial", "nothing", "refused" or "failed".</param>
public sealed record SyncOutcome(string Status, string Summary, SyncRunResult? Result = null, long ActionId = 0);

/// <summary>
/// Previews and runs sync jobs. Nothing is ever deleted: removed or overwritten files are set aside in
/// .miu-sync-trash on their own side, logged in Activity, and Undo puts them back.
/// </summary>
public sealed class SyncService(AppServices services, Func<DateTime> nowLocal)
{
    private StringComparison Cmp => services.CaseInsensitivePaths ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Why these two folders can't be synced, or null when they can.</summary>
    public string? Validate(string source, string target)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)) return "Choose both folders.";
        string s, t;
        try
        {
            s = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Trim()));
            t = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return "That isn't a valid folder path."; }
        if (string.Equals(s, t, Cmp)) return "The source and the target are the same folder.";
        if (IsInside(s, t) || IsInside(t, s)) return "One folder is inside the other. Pick two separate folders.";
        foreach (var (label, p) in new[] { ("source", s), ("target", t) })
            if (services.Guard.Classify(p) != ProtectionKind.None)
                return $"The {label} is a system or protected folder, so Miu won't sync it.";
        return null;
    }

    private bool IsInside(string child, string parent)
    {
        var withSep = parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar;
        return child.StartsWith(withSep, Cmp);
    }

    public async Task<SyncPreview> PreviewAsync(SyncJob job, CancellationToken ct = default)
    {
        var empty = new SyncListing(null, []);
        if (Validate(job.Source, job.Target) is { } problem) return new SyncPreview(job, new SyncPlan([], null), empty, empty, problem);
        var sourceTask = SyncLister.ListAsync(job.Source, ct);
        var targetTask = SyncLister.ListAsync(job.Target, ct);
        var source = await sourceTask;
        var target = await targetTask;
        if (source.Entries is null)
            return new SyncPreview(job, new SyncPlan([], null), source, target, $"Miu can't open the source folder ({job.Source}). Is the drive connected?");
        if (target.Entries is null && CanCreate(job.Target))
            target = new SyncListing([], []); // a new target folder: it will be created
        if (target.Entries is null)
            return new SyncPreview(job, new SyncPlan([], null), source, target, $"Miu can't open the target folder ({job.Target}). Is the drive connected?");

        // Files in folders Miu couldn't read on either side are unknown, not gone: leave them out entirely.
        var unknown = source.UnreadableDirs.Concat(target.UnreadableDirs).ToList();
        var s = Known(source.Entries, unknown);
        var t = Known(target.Entries!, unknown);
        var baseline = job.Mode == SyncMode.TwoWay && job.Id > 0 ? services.SyncBaseline.Get(job.Id) is { } b ? Known(b, unknown) : null : null;
        var same = job.CompareByHash ? await SameContentAsync(job, s, t, ct) : null;
        var plan = SyncDiff.Plan(s, t, baseline, job.Mode, services.CaseInsensitivePaths, DateOnly.FromDateTime(nowLocal()), same);
        return new SyncPreview(job, plan, source with { Entries = s }, target with { Entries = t }, null);
    }

    /// <summary>Runs a job. A refused plan (it looked like a disconnected drive) runs only when <paramref name="runAnyway"/>.</summary>
    public async Task<SyncOutcome> RunAsync(SyncJob job, string origin, bool runAnyway = false, IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        var preview = await PreviewAsync(job, ct);
        SyncOutcome outcome;
        if (preview.Problem is { } problem) outcome = new SyncOutcome("failed", problem);
        else if (preview.Plan.Refusal is { } refusal && !runAnyway) outcome = new SyncOutcome("refused", refusal);
        else if (preview.Plan.IsEmpty)
        {
            SaveBaseline(job, preview);
            outcome = new SyncOutcome("nothing", "Everything is already in sync.");
        }
        else
        {
            if (!Directory.Exists(job.Target)) Directory.CreateDirectory(job.Target);
            var runner = new SyncRunner(job.Source, job.Target, preview.Source.Entries!, preview.Target.Entries!, services.CaseInsensitivePaths, nowLocal());
            var result = await runner.RunAsync(preview.Plan with { Refusal = null }, progress, ct);
            var actionId = result.ArchivedFiles.Count == 0 ? 0
                : services.Actions.Add("sync", result.ArchivedFiles.Select(a => new ActionItem(a.OriginalPath, a.ArchivePath, a.Bytes)).ToList(), origin);
            if (job.Mode == SyncMode.TwoWay) SaveBaseline(job, await PreviewAsync(job, ct));
            var status = result.Failed.Count > 0 ? "partial" : "ok";
            var summary = result.Describe(result.Archived > 0 ? $" (in {SyncLister.TrashFolder})" : null);
            if (result.Failed.Count > 0) summary += " First problem: " + result.Failed[0].RelPath + " — " + result.Failed[0].Reason;
            outcome = new SyncOutcome(status, summary, result, actionId);
        }
        if (job.Id > 0)
        {
            services.JobRuns.AddSync(job.Id, started, DateTime.UtcNow, outcome.Status, outcome.Result?.BytesCopied ?? 0, outcome.Summary);
            services.SyncJobs.Patch(job.Id, j => j with { LastRunUtc = DateTime.UtcNow, LastResult = outcome.Summary });
        }
        return outcome;
    }

    /// <summary>
    /// Two-way only: remembers the files both sides now agree on. Files in unreadable folders keep their old entry,
    /// so a folder that is briefly unreadable isn't later mistaken for a deletion.
    /// </summary>
    private void SaveBaseline(SyncJob job, SyncPreview after)
    {
        if (job.Mode != SyncMode.TwoWay || job.Id <= 0 || after.Problem is not null || after.Source.Entries is null || after.Target.Entries is null) return;
        var cmp = services.CaseInsensitivePaths ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var target = after.Target.Entries.ToDictionary(e => e.RelPath, cmp);
        var agreed = after.Source.Entries
            .Where(e => target.TryGetValue(e.RelPath, out var t) && t.Size == e.Size && (t.MtimeUtc - e.MtimeUtc).Duration() <= SyncDiff.MtimeTolerance)
            .ToList();
        var unknown = after.Source.UnreadableDirs.Concat(after.Target.UnreadableDirs).ToList();
        if (unknown.Count > 0 && services.SyncBaseline.Get(job.Id) is { } old)
            agreed.AddRange(old.Where(e => Under(e.RelPath, unknown)));
        services.SyncBaseline.Save(job.Id, agreed, DateTime.UtcNow);
    }

    /// <summary>Puts files a sync set aside back where they were, unless a newer file is there now.</summary>
    public (int Restored, IReadOnlyList<string> Skipped) Undo(long actionId)
    {
        var action = services.Actions.Get(actionId);
        if (action is null || action.Undone || action.Kind != "sync") return (0, []);
        var left = new List<ActionItem>();
        var skipped = new List<string>();
        var restored = 0;
        foreach (var item in action.Items)
        {
            try
            {
                if (File.Exists(item.OriginalPath) || Directory.Exists(item.OriginalPath)) { skipped.Add($"{Path.GetFileName(item.OriginalPath)} — a newer file is there now"); left.Add(item); continue; }
                if (!File.Exists(item.TrashLocation)) { skipped.Add($"{Path.GetFileName(item.OriginalPath)} — no longer in {SyncLister.TrashFolder}"); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(item.OriginalPath)!);
                File.Move(item.TrashLocation, item.OriginalPath);
                restored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add($"{Path.GetFileName(item.OriginalPath)} — {ex.Message}");
                left.Add(item);
            }
        }
        if (left.Count == 0) services.Actions.MarkUndone(actionId);
        else services.Actions.UpdateItems(actionId, left);
        return (restored, skipped);
    }

    private async Task<ISet<string>> SameContentAsync(SyncJob job, IReadOnlyList<SyncEntry> s, IReadOnlyList<SyncEntry> t, CancellationToken ct)
    {
        var cmp = services.CaseInsensitivePaths ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var target = t.ToDictionary(e => e.RelPath, cmp);
        var same = new HashSet<string>(cmp);
        foreach (var e in s)
        {
            if (!target.TryGetValue(e.RelPath, out var o) || o.Size != e.Size || (o.MtimeUtc - e.MtimeUtc).Duration() <= SyncDiff.MtimeTolerance) continue;
            var a = await SyncLister.HashAsync(Path.Combine(job.Source, e.RelPath), ct);
            var b = await SyncLister.HashAsync(Path.Combine(job.Target, o.RelPath), ct);
            if (a is not null && a == b) same.Add(e.RelPath);
        }
        return same;
    }

    /// <summary>
    /// A missing target may be created when something above it exists and has content. A missing drive or share root,
    /// or an empty folder above it (often a mount point whose drive isn't connected), is refused instead.
    /// </summary>
    public static bool CanCreate(string target)
    {
        try
        {
            var full = Path.GetFullPath(target);
            if (Directory.Exists(full)) return false;
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return false;
            for (var dir = Path.GetDirectoryName(full); dir is not null; dir = Path.GetDirectoryName(dir))
            {
                if (!Directory.Exists(dir)) continue;
                return Directory.EnumerateFileSystemEntries(dir).Any();
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private List<SyncEntry> Known(IEnumerable<SyncEntry> entries, IReadOnlyList<string> unknownDirs) =>
        unknownDirs.Count == 0 ? entries.ToList() : entries.Where(e => !Under(e.RelPath, unknownDirs)).ToList();

    private bool Under(string rel, IReadOnlyList<string> dirs) =>
        dirs.Any(d => d.Length == 0 || string.Equals(rel, d, Cmp) || rel.StartsWith(d + "/", Cmp));
}
