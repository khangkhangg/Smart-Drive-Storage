using Miu.Core.Sync;
using Miu.Data;
using Miu.Platform.Portable;

namespace Miu.App.Services;

/// <param name="Problem">Why the job can't run at all (bad folders), or null.</param>
/// <param name="Note">Things Miu left alone on purpose (links, unreadable folders, names that differ only by case).</param>
public sealed record SyncPreview(SyncJob Job, SyncPlan Plan, SyncListing Source, SyncListing Target, string? Problem,
    string? Note = null, bool CaseInsensitive = false, IReadOnlySet<string>? SameContent = null)
{
    public bool CanRun => Problem is null && Plan.Refusal is null && !Plan.IsEmpty;
    public bool CanRunAnyway => Problem is null && Plan.Refusal is not null && !Plan.RefusalIsFinal;
    public string Text => (Problem ?? Plan.Describe()) + (Note is null ? "" : " " + Note);
}

/// <param name="Status">"ok", "partial", "nothing", "refused", "failed" or "cancelled".</param>
public sealed record SyncOutcome(string Status, string Summary, SyncRunResult? Result = null, long ActionId = 0);

/// <summary>
/// Previews and runs sync jobs. Nothing is ever deleted: removed or overwritten files are set aside in
/// .miu-sync-trash on their own side, logged in Activity, and Undo puts them back. File work runs off the UI thread;
/// the database is used only from the caller's thread.
/// </summary>
public sealed class SyncService(AppServices services, Func<DateTime> nowLocal)
{
    private StringComparison Cmp => services.CaseInsensitivePaths ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Why these two folders can't be synced, or null when they can. Links, junctions and mapped drives are resolved first.</summary>
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
        var (rs, rt) = (Real(s), Real(t));
        if (string.Equals(s, t, Cmp) || string.Equals(rs, rt, Cmp)) return "The source and the target are the same folder.";
        if (IsInside(s, t) || IsInside(t, s) || IsInside(rs, rt) || IsInside(rt, rs)) return "One folder is inside the other. Pick two separate folders.";
        foreach (var (label, p, real) in new[] { ("source", s, rs), ("target", t, rt) })
            if (services.Guard.Explain(p, checkLink: true) is not null || services.Guard.Explain(real, checkLink: false) is not null)
                return $"The {label} is a system or protected folder, so Miu won't sync it.";
        return null;
    }

    /// <summary>The real location: links resolved, and a mapped drive letter turned into its \\server\share path.</summary>
    private string Real(string path)
    {
        var real = ManagedScanner.RealPath(path);
        if (OperatingSystem.IsWindows() && real.Length >= 2 && real[1] == ':')
        {
            try
            {
                if (services.NetworkDrives.Mapped().FirstOrDefault(m => string.Equals(m.Letter, real[..2], StringComparison.OrdinalIgnoreCase)) is { } m)
                    real = m.Unc.TrimEnd('\\') + real[2..];
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        }
        return Path.TrimEndingDirectorySeparator(real);
    }

    private bool IsInside(string child, string parent)
    {
        var withSep = parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar;
        return child.StartsWith(withSep, Cmp);
    }

    public async Task<SyncPreview> PreviewAsync(SyncJob job, CancellationToken ct = default) => await PreviewCoreAsync(job, job.CompareByHash, ct);

    private async Task<SyncPreview> PreviewCoreAsync(SyncJob job, bool hash, CancellationToken ct)
    {
        var empty = new SyncListing(null, []);
        if (Validate(job.Source, job.Target) is { } problem) return new SyncPreview(job, new SyncPlan([], null), empty, empty, problem);
        var (source, target, ci, canCreate) = await Task.Run(async () =>
        {
            var st = SyncLister.ListAsync(job.Source, ct);
            var tt = SyncLister.ListAsync(job.Target, ct);
            var s = await st;
            var t = await tt;
            var create = t.Entries is null && CanCreate(job.Target);
            // Match names the way the stricter side does: a FAT/exFAT stick or an SMB share ignores case even on Linux.
            var insensitive = services.CaseInsensitivePaths
                || (s.Entries is not null && SyncLister.IsCaseInsensitive(job.Source))
                || (t.Entries is not null && SyncLister.IsCaseInsensitive(job.Target));
            return (s, t, insensitive, create);
        }, ct);
        if (source.Entries is null)
            return new SyncPreview(job, new SyncPlan([], null), source, target, $"Miu can't open the source folder ({job.Source}). Is the drive connected?");
        if (target.Entries is null && canCreate) target = new SyncListing([], []); // a new target folder: it will be created
        if (target.Entries is null)
            return new SyncPreview(job, new SyncPlan([], null), source, target, $"Miu can't open the target folder ({job.Target}). Is the drive connected?");

        // Unknown paths are left out on BOTH sides: unreadable folders, links, and names that clash by case.
        var twins = ci ? CaseTwins(source.Entries).Concat(CaseTwins(target.Entries!)).ToList() : [];
        var unknown = source.UnknownPaths.Concat(target.UnknownPaths).Concat(twins).ToList();
        var s = Known(source.Entries, unknown);
        var t = Known(target.Entries!, unknown);
        var baseline = job.Mode == SyncMode.TwoWay && job.Id > 0 ? services.SyncBaseline.Get(job.Id) is { } b ? Known(b, unknown) : null : null;
        HashSet<string>? same = hash ? await Task.Run(() => SameContentAsync(job, s, t, ci, ct), ct) : null;
        var plan = SyncDiff.Plan(s, t, baseline, job.Mode, ci, DateOnly.FromDateTime(nowLocal()), same);
        return new SyncPreview(job, plan, source with { Entries = s }, target with { Entries = t }, null, NoteFor(source, target, twins.Count), ci, same);
    }

    private static string? NoteFor(SyncListing s, SyncListing t, int twins)
    {
        var parts = new List<string>();
        var links = (s.Links?.Count ?? 0) + (t.Links?.Count ?? 0);
        var unreadable = s.UnreadableDirs.Count + t.UnreadableDirs.Count;
        if (links > 0) parts.Add($"{links} linked {(links == 1 ? "item is" : "items are")} left alone (Miu doesn't follow links)");
        if (unreadable > 0) parts.Add($"{unreadable} {(unreadable == 1 ? "folder" : "folders")} couldn't be read and {(unreadable == 1 ? "is" : "are")} skipped");
        if (twins > 0) parts.Add($"{twins} files whose names differ only by upper/lower case are skipped, because one side can't tell them apart");
        return parts.Count == 0 ? null : string.Join("; ", parts) + ".";
    }

    /// <summary>
    /// Runs a job. A refused plan runs only when <paramref name="overrideRefusal"/> is exactly the refusal you saw in
    /// the preview, and never for an empty or vanished folder.
    /// </summary>
    public async Task<SyncOutcome> RunAsync(SyncJob job, string origin, string? overrideRefusal = null, IProgress<SyncProgress>? progress = null,
        CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        SyncRunner? runner = null;
        SyncOutcome outcome;
        try
        {
            var preview = await PreviewAsync(job, ct);
            if (preview.Problem is { } problem) outcome = new SyncOutcome("failed", problem);
            else if (preview.Plan.Refusal is { } refusal && (preview.Plan.RefusalIsFinal || overrideRefusal != refusal)) outcome = new SyncOutcome("refused", refusal);
            else if (preview.Plan.IsEmpty)
            {
                await AlignSameContentAsync(job, preview, ct);
                SaveBaseline(job, await PreviewCoreAsync(job, false, ct), preview, new HashSet<string>());
                outcome = new SyncOutcome("nothing", "Everything is already in sync.");
            }
            else
            {
                runner = new SyncRunner(job.Source, job.Target, preview.Source.Entries!, preview.Target.Entries!, preview.CaseInsensitive, nowLocal());
                var r = runner;
                var result = await Task.Run(() =>
                {
                    Directory.CreateDirectory(job.Target);
                    return r.RunAsync(preview.Plan with { Refusal = null }, progress, ct);
                }, ct);
                var actionId = RecordArchived(runner, origin);
                runner = null; // recorded
                await AlignSameContentAsync(job, preview, ct);
                var keep = result.Failed.Select(f => f.RelPath).Concat(result.ChangedPaths).ToHashSet(preview.CaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                SaveBaseline(job, await PreviewCoreAsync(job, false, ct), preview, keep);
                var status = result.Failed.Count > 0 ? "partial" : "ok";
                var summary = result.Describe(result.Archived > 0 ? $" (in {SyncLister.TrashFolder})" : null);
                if (result.Failed.Count > 0) summary += " First problem: " + result.Failed[0].RelPath + " — " + result.Failed[0].Reason;
                outcome = new SyncOutcome(status, summary, result, actionId);
            }
        }
        catch (OperationCanceledException)
        {
            // Whatever was already set aside must stay in Activity, so Undo can find it.
            if (runner is not null) RecordArchived(runner, origin);
            Log(job, started, new SyncOutcome("cancelled", "Stopped. Finished files are kept; the rest will be copied next time."));
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            var id = runner is null ? 0 : RecordArchived(runner, origin);
            outcome = new SyncOutcome("failed", "Couldn't sync: " + ex.Message, null, id);
        }
        Log(job, started, outcome);
        return outcome;
    }

    private long RecordArchived(SyncRunner runner, string origin) =>
        runner.ArchivedSoFar.Count == 0 ? 0
            : services.Actions.Add("sync", runner.ArchivedSoFar.Select(a => new ActionItem(a.OriginalPath, a.ArchivePath, a.Bytes)).ToList(), origin);

    private void Log(SyncJob job, DateTime started, SyncOutcome outcome)
    {
        if (job.Id <= 0 || services.SyncJobs.Get(job.Id) is null) return;
        services.JobRuns.AddSync(job.Id, started, DateTime.UtcNow, outcome.Status, outcome.Result?.BytesCopied ?? 0, outcome.Summary);
        services.SyncJobs.Patch(job.Id, j => j with { LastRunUtc = DateTime.UtcNow, LastResult = outcome.Summary });
    }

    /// <summary>
    /// Two-way only: remembers what both sides now agree on. Paths Miu didn't settle this run (failed, changed during
    /// the run, unreadable, links) keep their old entry, so a hiccup is never mistaken for a deletion or an edit.
    /// </summary>
    private void SaveBaseline(SyncJob job, SyncPreview after, SyncPreview before, IReadOnlySet<string> unsettled)
    {
        if (job.Mode != SyncMode.TwoWay || job.Id <= 0 || services.SyncJobs.Get(job.Id) is null) return;
        if (after.Problem is not null || after.Source.Entries is null || after.Target.Entries is null) return;
        var cmp = after.CaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var target = new Dictionary<string, SyncEntry>(cmp);
        foreach (var e in after.Target.Entries) target.TryAdd(e.RelPath, e);
        var agreed = new Dictionary<string, SyncEntry>(cmp);
        foreach (var e in after.Source.Entries)
            if (target.TryGetValue(e.RelPath, out var t) && t.Size == e.Size && (t.MtimeUtc - e.MtimeUtc).Duration() <= SyncDiff.MtimeTolerance)
                agreed.TryAdd(e.RelPath, e);
        var unknown = after.Source.UnknownPaths.Concat(after.Target.UnknownPaths).Concat(before.Source.UnknownPaths).Concat(before.Target.UnknownPaths).ToList();
        if (services.SyncBaseline.Get(job.Id) is { } old)
            foreach (var e in old)
                if (!agreed.ContainsKey(e.RelPath) && (unsettled.Contains(e.RelPath) || Under(e.RelPath, unknown))) agreed.TryAdd(e.RelPath, e);
        services.SyncBaseline.Save(job.Id, agreed.Values.ToList(), DateTime.UtcNow);
    }

    /// <summary>Gives identical files (by content) the same date, so they aren't hashed again on every run.</summary>
    private static Task AlignSameContentAsync(SyncJob job, SyncPreview preview, CancellationToken ct)
    {
        if (preview.SameContent is not { Count: > 0 } same || preview.Source.Entries is null) return Task.CompletedTask;
        var entries = preview.Source.Entries.Where(e => same.Contains(e.RelPath)).ToList();
        return Task.Run(() =>
        {
            foreach (var e in entries)
            {
                ct.ThrowIfCancellationRequested();
                try { File.SetLastWriteTimeUtc(Path.Combine(job.Target, e.RelPath), e.MtimeUtc); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }, ct);
    }

    /// <summary>Puts files a sync set aside back where they were, unless a newer file is there now.</summary>
    public async Task<(int Restored, IReadOnlyList<string> Skipped)> UndoAsync(long actionId)
    {
        var action = services.Actions.Get(actionId);
        if (action is null || action.Undone || action.Kind != "sync") return (0, []);
        var (restored, left, skipped) = await Task.Run(() =>
        {
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
            return (restored, left, skipped);
        });
        if (left.Count == 0) services.Actions.MarkUndone(actionId);
        else services.Actions.UpdateItems(actionId, left);
        return (restored, skipped);
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
                if (File.Exists(dir)) return false;
                if (!Directory.Exists(dir)) continue;
                return Directory.EnumerateFileSystemEntries(dir).Any();
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>Paths that differ only by upper/lower case (e.g. "Makefile" and "makefile").</summary>
    public static IReadOnlyList<string> CaseTwins(IEnumerable<SyncEntry> entries) =>
        entries.GroupBy(e => e.RelPath, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).SelectMany(g => g.Select(e => e.RelPath)).ToList();

    private static async Task<HashSet<string>> SameContentAsync(SyncJob job, IReadOnlyList<SyncEntry> s, IReadOnlyList<SyncEntry> t, bool ci, CancellationToken ct)
    {
        var cmp = ci ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var target = new Dictionary<string, SyncEntry>(cmp);
        foreach (var e in t) target.TryAdd(e.RelPath, e);
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

    private List<SyncEntry> Known(IEnumerable<SyncEntry> entries, IReadOnlyList<string> unknownPaths) =>
        unknownPaths.Count == 0 ? entries.ToList() : entries.Where(e => !Under(e.RelPath, unknownPaths)).ToList();

    private bool Under(string rel, IReadOnlyList<string> paths) =>
        paths.Any(d => d.Length == 0 || string.Equals(rel, d, Cmp) || rel.StartsWith(d + "/", Cmp));
}
