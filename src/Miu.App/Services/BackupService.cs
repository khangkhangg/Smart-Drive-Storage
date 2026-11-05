using System.Globalization;
using Miu.Cloud;
using Miu.Core.Backup;
using Miu.Core.Cleanup;
using Miu.Core.Formatting;
using Miu.Core.Rules;
using Miu.Data;
using Miu.Platform.Portable;

namespace Miu.App.Services;

public sealed record BackupProgress(int Done, int Total, long Bytes, string Current);

public sealed record BackupPreview(BackupPlan Plan, BackupPlanResult Result, string? Problem, string? Note = null)
{
    public bool CanRun => Problem is null && Result.Refusal is null && !Result.IsEmpty;
    public bool CanRunAnyway => Problem is null && Result.Refusal is not null && !Result.RefusalIsFinal;

    public string Text
    {
        get
        {
            if (Problem is not null) return Problem;
            if (Result.Refusal is not null) return Result.Refusal;
            var parts = new List<string>();
            if (Result.Uploads > 0) parts.Add($"upload {Result.Uploads} {(Result.Uploads == 1 ? "file" : "files")} ({SizeFormatter.Format(Result.UploadBytes)})");
            if (Result.SetAsides > 0) parts.Add($"keep {Result.SetAsides} removed {(Result.SetAsides == 1 ? "file" : "files")} as older versions");
            if (Result.Frees > 0) parts.Add($"free {SizeFormatter.Format(Result.FreeBytes)} on this computer ({Result.Frees} {(Result.Frees == 1 ? "file" : "files")}, after checking each one in the cloud)");
            var text = parts.Count == 0 ? "Everything is already backed up." : char.ToUpperInvariant(parts[0][0]) + string.Join(", ", parts)[1..] + ".";
            if (Result.MissingSources.Count > 0) text += $" Skipped (not found): {string.Join(", ", Result.MissingSources)}.";
            return Note is null ? text : text + " " + Note;
        }
    }
}

/// <param name="Status">"ok", "partial", "nothing", "refused", "failed" or "cancelled".</param>
public sealed record BackupOutcome(string Status, string Summary, int Uploaded = 0, long UploadedBytes = 0, int Freed = 0, long FreedBytes = 0,
    IReadOnlyList<(string Path, string Reason)>? Failed = null, long CleanActionId = 0)
{
    public IReadOnlyList<(string Path, string Reason)> Failed { get; } = Failed ?? [];
}

/// <param name="VersionPath">Set for an older version: where it is in the cloud (Entry.RemotePath is the file's normal place).</param>
public sealed record RestoreItem(ManifestEntry Entry, string? VersionPath = null, string? Note = null)
{
    public bool IsVersion => VersionPath is not null;
}

public sealed record RestoreOutcome(int Restored, int Skipped, IReadOnlyList<(string Path, string Reason)> Failed, IReadOnlyList<string> RenamedTo);

/// <summary>
/// Runs cloud backups. Every upload is checked (size, and the provider's checksum against one computed while reading
/// the file); the manifest is written after each file. A replaced or removed cloud file becomes an older version.
/// "Back up, then free" recycles a local file only after its cloud copy is verified again, it hasn't changed, and no
/// Keep rule covers it — through the Recycle Bin, with Undo.
/// </summary>
public sealed class BackupService(AppServices services, CloudAccountsService accounts, Func<DateTime> nowLocal)
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);
    public Func<int, TimeSpan> RetryDelay { get; set; } = attempt => TimeSpan.FromSeconds(2 << attempt);

    private StringComparison Cmp => services.CaseInsensitivePaths ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public string? Validate(BackupPlan plan)
    {
        if (services.CloudAccounts.Get(plan.AccountId) is null) return "Choose a cloud account.";
        if (plan.Sources.Count == 0) return "Add at least one folder to back up.";
        var remote = CloudPath.Normalize(plan.RemoteFolder);
        if (remote.Length == 0 || !BackupPaths.IsSafeRelative(remote) || remote.Split('/').Contains(BackupPaths.VersionsFolder))
            return "Type a cloud folder name, like Miu Backup.";
        var full = new List<string>();
        foreach (var s in plan.Sources)
        {
            string p;
            try { p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(s.LocalPath)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return $"{s.LocalPath} isn't a valid folder path."; }
            if (full.Any(o => string.Equals(o, p, Cmp) || p.StartsWith(o + Path.DirectorySeparatorChar, Cmp) || o.StartsWith(p + Path.DirectorySeparatorChar, Cmp)))
                return "One folder is inside another one. Pick separate folders.";
            full.Add(p);
        }
        if (plan.Sources.Select(s => s.RemoteName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Sources.Count)
            return "Two folders have the same name. Rename one in the cloud folder list.";
        if (plan.Sources.Any(s => !BackupPaths.IsSafeRelative(s.RemoteName))) return "A folder name can't contain / or \\.";
        // Two plans writing to the same cloud folders would overwrite (and version away) each other's files.
        var mine = plan.Sources.Select(s => CloudRoot(plan.RemoteFolder, s.RemoteName)).ToList();
        if (mine.Any(r => Overlaps(r, BeforeCleaningRoot))) return "That cloud folder is used for “Back up first” copies. Pick another name.";
        foreach (var other in services.BackupPlans.All().Where(p => p.Id != plan.Id && p.AccountId == plan.AccountId))
            foreach (var r in other.Sources.Select(s => CloudRoot(other.RemoteFolder, s.RemoteName)))
                if (mine.Any(m => Overlaps(m, r)))
                    return $"The plan “{other.Name}” already backs up to {r} on this account. Pick another cloud folder so they can't overwrite each other.";
        return null;
    }

    private const string BeforeCleaningFolder = "Miu Backup/Before cleaning";
    private static readonly string BeforeCleaningRoot = BeforeCleaningFolder.ToLowerInvariant();

    /// <summary>Compared without case: Dropbox and OneDrive ignore it.</summary>
    private static string CloudRoot(string remoteFolder, string remoteName) => BackupPaths.Remote(remoteFolder, remoteName, "").ToLowerInvariant();

    private static bool Overlaps(string a, string b) => a == b || a.StartsWith(b + "/", StringComparison.Ordinal) || b.StartsWith(a + "/", StringComparison.Ordinal);

    /// <summary>Builds the sources list from local folders, giving each a unique cloud name.</summary>
    public static IReadOnlyList<BackupSource> SourcesFor(IEnumerable<string> folders)
    {
        var list = new List<BackupSource>();
        foreach (var f in folders.Select(f => f.Trim()).Where(f => f.Length > 0))
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(f));
            if (string.IsNullOrEmpty(name)) name = new string(f.Where(char.IsLetterOrDigit).ToArray()) is { Length: > 0 } n ? n : "Drive";
            var unique = name;
            for (var i = 2; list.Any(s => s.RemoteName.Equals(unique, StringComparison.OrdinalIgnoreCase)); i++) unique = $"{name} ({i})";
            list.Add(new BackupSource(f, unique));
        }
        return list;
    }

    public async Task<BackupPreview> PreviewAsync(BackupPlan plan, CancellationToken ct = default)
    {
        if (Validate(plan) is { } problem) return new BackupPreview(plan, new BackupPlanResult([], null), problem);
        var (local, unknown, links) = await Task.Run(() =>
        {
            var local = new Dictionary<BackupSource, IReadOnlyList<LocalFile>?>();
            var unknown = new List<string>();
            var links = 0;
            foreach (var src in plan.Sources)
            {
                ct.ThrowIfCancellationRequested();
                var listing = SyncLister.List(src.LocalPath, ct);
                if (listing.Entries is null) { local[src] = null; continue; }
                local[src] = listing.Entries.Select(e => new LocalFile(Path.Combine(src.LocalPath, e.RelPath.Replace('/', Path.DirectorySeparatorChar)), e.Size, e.MtimeUtc)).ToList();
                unknown.AddRange(listing.UnknownPaths.Select(u => Path.Combine(src.LocalPath, u.Replace('/', Path.DirectorySeparatorChar))));
                links += listing.Links?.Count ?? 0;
            }
            return (local, unknown, links);
        }, ct);
        var result = BackupDiff.Plan(plan, local, unknown, services.BackupManifest.For(plan.Id), DateTime.UtcNow, services.CaseInsensitivePaths);
        var note = links > 0 ? $"{links} linked {(links == 1 ? "item is" : "items are")} skipped." : null;
        return new BackupPreview(plan, result, null, note);
    }

    public async Task<BackupOutcome> RunAsync(BackupPlan plan, string origin, string? overrideRefusal = null, IProgress<BackupProgress>? progress = null,
        CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        BackupOutcome outcome;
        var failed = new List<(string, string)>();
        int uploaded = 0, setAside = 0;
        long bytes = 0;
        try
        {
            var preview = await PreviewAsync(plan, ct);
            if (preview.Problem is { } problem) outcome = new BackupOutcome("failed", problem);
            else if (preview.Result.Refusal is { } refusal && (preview.Result.RefusalIsFinal || overrideRefusal != refusal)) outcome = new BackupOutcome("refused", refusal);
            else if (preview.Result.IsEmpty) outcome = new BackupOutcome("nothing", "Everything is already backed up.");
            else
            {
                var account = services.CloudAccounts.Get(plan.AccountId)!;
                var cloud = accounts.Open(account);
                var ops = preview.Result.Ops;
                var total = ops.Count;
                var done = 0;
                foreach (var op in ops.Where(o => o.Kind != BackupOpKind.Free))
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new BackupProgress(done, total, bytes, Path.GetFileName(op.LocalPath)));
                    try
                    {
                        if (op.Kind == BackupOpKind.Upload)
                        {
                            var b0 = bytes;
                            if ((await UploadFileAsync(plan, cloud, op, p => progress?.Report(new BackupProgress(done, total, b0 + p, Path.GetFileName(op.LocalPath))), ct)).Why is { } why)
                                failed.Add((op.LocalPath, why));
                            else { uploaded++; bytes += op.Bytes; }
                        }
                        else
                        {
                            await RetryAsync(() => KeepAsVersionAsync(plan, cloud, op.LocalPath, op.RemotePath, "deleted on this computer", ct), ct);
                            services.BackupManifest.Remove(plan.Id, op.LocalPath);
                            setAside++;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        failed.Add((op.LocalPath, Message(ex))); // one bad file or reply never stops the whole backup
                    }
                    done++;
                }

                var (freed, freedBytes, actionId, freeFailed) = await FreeAsync(plan, cloud, ops.Where(o => o.Kind == BackupOpKind.Free).ToList(), ct);
                failed.AddRange(freeFailed);
                progress?.Report(new BackupProgress(total, total, bytes, ""));
                var parts = new List<string> { $"Uploaded {uploaded} {(uploaded == 1 ? "file" : "files")} ({SizeFormatter.Format(bytes)})" };
                if (setAside > 0) parts.Add($"kept {setAside} removed {(setAside == 1 ? "file" : "files")} as older versions");
                if (freed > 0) parts.Add($"freed {SizeFormatter.Format(freedBytes)} ({freed} {(freed == 1 ? "file" : "files")} in the {services.RecycleBinName})");
                if (failed.Count > 0) parts.Add($"{failed.Count} didn't work (first: {Path.GetFileName(failed[0].Item1)} — {failed[0].Item2})");
                outcome = new BackupOutcome(failed.Count > 0 ? "partial" : "ok", string.Join(", ", parts) + ".", uploaded, bytes, freed, freedBytes, failed, actionId);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log(plan, started, new BackupOutcome("cancelled", $"Stopped after {uploaded} {(uploaded == 1 ? "file" : "files")}. The next run continues from there."));
            throw;
        }
        catch (Exception ex)
        {
            outcome = new BackupOutcome("failed", "Couldn't back up: " + Message(ex), uploaded, bytes, Failed: failed);
        }
        Log(plan, started, outcome);
        return outcome;
    }

    private void Log(BackupPlan plan, DateTime started, BackupOutcome outcome)
    {
        if (plan.Id <= 0 || services.BackupPlans.Get(plan.Id) is null) return;
        services.JobRuns.AddBackup(plan.Id, started, DateTime.UtcNow, outcome.Status, outcome.UploadedBytes, outcome.Summary);
        services.BackupPlans.Patch(plan.Id, p => p with { LastRunUtc = DateTime.UtcNow, LastResult = outcome.Summary });
    }

    private static string Message(Exception ex) => ex switch
    {
        CloudException c => c.Message,
        OperationCanceledException => "the cloud took too long to answer",
        System.Text.Json.JsonException or System.Xml.XmlException => "the cloud sent an answer Miu didn't understand (a network login page?)",
        _ => ex.Message,
    };

    /// <summary>Retries a cloud call after network hiccups (3 tries).</summary>
    private async Task<T> RetryAsync<T>(Func<Task<T>> call, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await call(); }
            catch (CloudException ex) when (ex.IsTransient && attempt < 2) { await Task.Delay(RetryDelay(attempt), ct); }
        }
    }

    private Task RetryAsync(Func<Task> call, CancellationToken ct) => RetryAsync(async () => { await call(); return true; }, ct);

    /// <summary>
    /// Uploads one file. Whatever is in the cloud at that path is first kept as an older version (pinned, never pruned,
    /// when it is the only copy of a file Miu freed); if that can't be done, nothing is uploaded. Returns why it failed,
    /// or null, and whether the provider's checksum confirmed the upload.
    /// </summary>
    private async Task<(string? Why, bool Verified)> UploadFileAsync(BackupPlan plan, ICloudStorage cloud, BackupOp op, Action<long> onBytes, CancellationToken ct)
    {
        try
        {
            if (await RetryAsync(() => cloud.GetAsync(op.RemotePath, ct), ct) is not null)
                await RetryAsync(() => KeepAsVersionAsync(plan, cloud, op.LocalPath, op.RemotePath, "replaced by a newer copy", ct), ct);
        }
        catch (CloudException ex) { return ("Miu couldn't keep the cloud's older copy, so it wasn't replaced: " + ex.Message, false); }

        for (var attempt = 0; ; attempt++)
        {
            // From here on, what's at the path is the old copy's replacement or this run's own failed attempt.
            try { return await UploadOnceAsync(plan, cloud, op, onBytes, ct); }
            catch (CloudException ex) when (ex.IsTransient && attempt < 2) { await Task.Delay(RetryDelay(attempt), ct); }
            catch (ChecksumMismatch) when (attempt < 2) { await Task.Delay(RetryDelay(attempt), ct); }
            catch (ChecksumMismatch ex) { return (ex.Message, false); }
        }
    }

    private sealed class ChecksumMismatch(string message) : Exception(message);

    private async Task<(string? Why, bool Verified)> UploadOnceAsync(BackupPlan plan, ICloudStorage cloud, BackupOp op, Action<long> onBytes, CancellationToken ct)
    {
        var before = new FileInfo(op.LocalPath);
        if (!before.Exists) return ("it was removed before it could be uploaded", false);
        var (size, mtime) = (before.Length, before.LastWriteTimeUtc);
        var kind = cloud.ExpectedHashKind(size);
        var progress = new Progress<long>(onBytes);
        // Reading, hashing and sending happen off the UI thread.
        var (item, read, localHash) = await Task.Run(async () =>
        {
            var hasher = CloudHashes.Create(kind);
            await using var file = new FileStream(op.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
            var hashing = new HashingStream(file, hasher);
            Stream body = plan.BandwidthKBps > 0 ? new ThrottledStream(hashing, plan.BandwidthKBps * 1024L) : hashing;
            var uploaded = await cloud.UploadAsync(op.RemotePath, body, size, progress, ct).ConfigureAwait(false);
            return (uploaded, hashing.BytesRead, hasher?.Finish());
        }, ct);
        if (read != size) return ("it changed size while it was being uploaded", false);
        var after = new FileInfo(op.LocalPath);
        if (!after.Exists || after.Length != size || (after.LastWriteTimeUtc - mtime).Duration() > Tolerance)
            return ("it changed while it was being uploaded; it will be uploaded next time", false);
        if (item.Size != size) throw new ChecksumMismatch($"the cloud stored {item.Size} bytes instead of {size}");
        var verified = item.HashKind is not null && item.HashKind == kind && CloudHashes.Equal(kind, item.Hash, localHash);
        if (item.HashKind is not null && !verified) throw new ChecksumMismatch("the cloud copy's checksum doesn't match");
        if (plan.Id > 0)
            services.BackupManifest.Save(plan.Id, new ManifestEntry(op.LocalPath, size, mtime, verified ? kind : null, verified ? localHash : null, op.RemotePath, DateTime.UtcNow),
                services.CaseInsensitivePaths);
        return (null, verified);
    }

    /// <summary>
    /// Moves a cloud file into .miu-versions and keeps only the newest N versions of it. The only copy of a file Miu
    /// freed is pinned: it is never pruned and stays restorable.
    /// </summary>
    private async Task KeepAsVersionAsync(BackupPlan plan, ICloudStorage cloud, string localPath, string remotePath, string reason, CancellationToken ct)
    {
        var current = await cloud.GetAsync(remotePath, ct);
        if (current is null) return; // nothing there any more
        var versionPath = BackupPaths.Version(plan.RemoteFolder, remotePath, nowLocal());
        for (var i = 2; await cloud.GetAsync(versionPath, ct) is not null; i++)
            versionPath = BackupPaths.Version(plan.RemoteFolder, remotePath, nowLocal()) + $" ({i})";
        try { await cloud.MoveAsync(remotePath, versionPath, ct); }
        catch (CloudException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound) { return; }
        if (plan.Id <= 0) return;
        var known = services.BackupManifest.For(plan.Id).FirstOrDefault(m => string.Equals(m.LocalPath, localPath, Cmp) && m.RemotePath == remotePath);
        var pinned = known?.Freed == true;
        services.BackupManifest.AddVersion(plan.Id, new BackupVersion(0, localPath, versionPath, DateTime.UtcNow, pinned ? "freed from this computer, then replaced" : reason,
            known?.Size ?? current.Size, known?.MtimeUtc ?? current.ModifiedUtc, known?.HashKind ?? current.HashKind, known?.Hash ?? current.Hash, pinned));
        foreach (var old in services.BackupManifest.Versions(plan.Id, localPath).Where(v => !v.Pinned).Skip(Math.Max(1, plan.KeepVersions)))
        {
            try
            {
                await cloud.RemoveAsync(old.RemotePath, ct);
                services.BackupManifest.RemoveVersion(old.Id);
            }
            catch (CloudException) { } // tried again next time
        }
    }

    /// <summary>
    /// Frees local files whose cloud copy is verified right now (size + checksum), which haven't changed since, and
    /// which no Keep rule covers. Providers without checksums never free anything.
    /// </summary>
    private async Task<(int Freed, long Bytes, long ActionId, List<(string, string)> Failed)> FreeAsync(BackupPlan plan, ICloudStorage cloud, List<BackupOp> frees, CancellationToken ct)
    {
        var failed = new List<(string, string)>();
        if (frees.Count == 0) return (0, 0, 0, failed);
        long freedBytes = 0, actionId = 0;
        var freed = 0;
        var cleanup = new CleanupService(services);
        foreach (var src in plan.Sources)
        {
            var here = frees.Where(o => o.LocalPath.StartsWith(Path.TrimEndingDirectorySeparator(src.LocalPath) + Path.DirectorySeparatorChar, Cmp)).ToList();
            if (here.Count == 0) continue;
            // Scan first (Keep rules, protected places), then check each file, then recycle straight away.
            var session = await ScanSession.RunAsync(services, src.LocalPath, null, ct);
            var manifest = new Dictionary<string, ManifestEntry>(services.CaseInsensitivePaths ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var m in services.BackupManifest.For(plan.Id)) manifest.TryAdd(m.LocalPath, m);
            var ready = new List<(BackupOp Op, ManifestEntry Entry)>();
            foreach (var op in here)
            {
                ct.ThrowIfCancellationRequested();
                if (!manifest.TryGetValue(op.LocalPath, out var m) || m.Freed) continue; // not backed up (its upload failed)
                if (m.Hash is null || m.HashKind is null) { failed.Add((op.LocalPath, "kept: this cloud doesn't confirm a checksum, so Miu can't prove the copy is complete")); continue; }
                CloudItem? remote;
                try { remote = await RetryAsync(() => cloud.GetAsync(m.RemotePath, ct), ct); }
                catch (CloudException ex) { failed.Add((op.LocalPath, "kept: " + ex.Message)); continue; }
                if (remote is null || remote.Size != m.Size || remote.HashKind != m.HashKind || !CloudHashes.Equal(m.HashKind, remote.Hash, m.Hash))
                {
                    failed.Add((op.LocalPath, "kept: its cloud copy doesn't match"));
                    continue;
                }
                // The local file must still be exactly what was uploaded: same size and date, and the same content.
                if (!Unchanged(op.LocalPath, m)) continue;
                var localHash = await Task.Run(() => HashFileAsync(m.HashKind, op.LocalPath, ct), ct);
                if (!CloudHashes.Equal(m.HashKind, localHash, m.Hash)) { failed.Add((op.LocalPath, "kept: it changed since it was backed up")); continue; }
                ready.Add((op, m));
            }
            ready = ready.Where(r => Unchanged(r.Op.LocalPath, r.Entry)).ToList(); // last look, right before recycling
            if (ready.Count == 0) continue;
            var items = ready.Select(r => new CartItem(r.Op.LocalPath, Path.GetFileName(r.Op.LocalPath), r.Op.Bytes, Verdict.Safe, "Backed up and verified in the cloud")).ToList();
            var outcome = await cleanup.CleanAsync(items, session, "backup", ct: ct);
            failed.AddRange(outcome.Failed.Select(f => (f.Path, "kept: " + f.Reason)));
            var failedPaths = outcome.Failed.Select(f => f.Path).ToHashSet();
            foreach (var (op, entry) in ready.Where(r => !failedPaths.Contains(r.Op.LocalPath) && !File.Exists(r.Op.LocalPath)))
            {
                services.BackupManifest.Save(plan.Id, entry with { Freed = true }, services.CaseInsensitivePaths);
                freed++;
            }
            freedBytes += outcome.FreedBytes;
            if (outcome.ActionId > 0) actionId = outcome.ActionId;
        }
        return (freed, freedBytes, actionId, failed);
    }

    private static bool Unchanged(string path, ManifestEntry m)
    {
        var fi = new FileInfo(path);
        return fi.Exists && fi.Length == m.Size && (fi.LastWriteTimeUtc - m.MtimeUtc).Duration() <= Tolerance;
    }

    /// <summary>Everything in a plan's backup you can restore: current copies, then older versions (newest first).</summary>
    public IReadOnlyList<RestoreItem> RestoreChoices(BackupPlan plan)
    {
        var list = services.BackupManifest.For(plan.Id).Select(m => new RestoreItem(m)).ToList();
        foreach (var v in services.BackupManifest.Versions(plan.Id))
        {
            var normal = NormalPathOf(v.RemotePath);
            if (normal is null) continue;
            var entry = new ManifestEntry(v.LocalPath, v.Size, v.MtimeUtc ?? v.CreatedUtc, v.HashKind, v.Hash, normal, v.CreatedUtc, v.Pinned);
            list.Add(new RestoreItem(entry, v.RemotePath, v.Pinned ? "the copy Miu freed from this computer" : "older version"));
        }
        return list;
    }

    /// <summary>"Backup/.miu-versions/2026-11-05 140309/Photos/a.jpg" → "Backup/Photos/a.jpg".</summary>
    private static string? NormalPathOf(string versionPath)
    {
        var marker = "/" + BackupPaths.VersionsFolder + "/";
        var i = versionPath.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return null;
        var rest = versionPath[(i + marker.Length)..];
        var slash = rest.IndexOf('/');
        if (slash < 0) return null;
        var inside = rest[(slash + 1)..];
        if (inside.EndsWith(')') && inside.LastIndexOf(" (", StringComparison.Ordinal) is var p and > 0 && int.TryParse(inside[(p + 2)..^1], out _)) inside = inside[..p];
        return versionPath[..i] + "/" + inside;
    }

    /// <summary>
    /// Downloads files back. A different local file is never overwritten: the copy is saved as
    /// "name (restored YYYY-MM-DD).ext" next to it. <paramref name="toFolder"/> null means "where they were".
    /// </summary>
    public Task<RestoreOutcome> RestoreAsync(BackupPlan plan, IReadOnlyList<ManifestEntry> entries, string? toFolder, IProgress<BackupProgress>? progress = null,
        CancellationToken ct = default) => RestoreAsync(plan, entries.Select(e => new RestoreItem(e)).ToList(), toFolder, progress, ct);

    public async Task<RestoreOutcome> RestoreAsync(BackupPlan plan, IReadOnlyList<RestoreItem> items, string? toFolder, IProgress<BackupProgress>? progress = null,
        CancellationToken ct = default)
    {
        var cloud = accounts.Open(services.CloudAccounts.Get(plan.AccountId) ?? throw new CloudException("The cloud account for this backup is gone."));
        var failed = new List<(string, string)>();
        var renamed = new List<string>();
        int restored = 0, skipped = 0, done = 0;
        foreach (var it in items)
        {
            var e = it.Entry;
            ct.ThrowIfCancellationRequested();
            progress?.Report(new BackupProgress(done++, items.Count, 0, Path.GetFileName(e.LocalPath)));
            var target = TargetFor(plan, e, toFolder);
            if (target is null) { failed.Add((e.LocalPath, "its path isn't safe to restore")); continue; }
            try
            {
                var fi = new FileInfo(target);
                if (fi.Exists && fi.Length == e.Size && (fi.LastWriteTimeUtc - e.MtimeUtc).Duration() <= Tolerance) { skipped++; continue; }
                if (fi.Exists || Directory.Exists(target)) { target = RestoredName(target); renamed.Add(target); }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                // A fresh, Miu-named temp file: never an existing file of yours.
                var partial = $"{target}.{Guid.NewGuid().ToString("N")[..8]}-0{SyncLister.PartialSuffix}";
                var source = it.VersionPath ?? e.RemotePath;
                try
                {
                    var ok = await Task.Run(async () =>
                    {
                        await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                            await cloud.DownloadAsync(source, output, ct).ConfigureAwait(false);
                        return new FileInfo(partial).Length == e.Size && (e.Hash is null || CloudHashes.Equal(e.HashKind, e.Hash, await HashFileAsync(e.HashKind, partial, ct).ConfigureAwait(false)));
                    }, ct);
                    if (!ok)
                    {
                        failed.Add((e.LocalPath, "the downloaded copy didn't match the backup"));
                        continue;
                    }
                    File.SetLastWriteTimeUtc(partial, e.MtimeUtc);
                    File.Move(partial, target);
                }
                finally { if (File.Exists(partial)) File.Delete(partial); }
                restored++;
                if (!it.IsVersion && e.Freed && string.Equals(target, e.LocalPath, Cmp)) services.BackupManifest.Save(plan.Id, e with { Freed = false }, services.CaseInsensitivePaths);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { failed.Add((e.LocalPath, Message(ex))); }
        }
        return new RestoreOutcome(restored, skipped, failed, renamed);
    }

    /// <summary>Where a file is restored to. Built only from parts that can't climb out of the folder.</summary>
    private string? TargetFor(BackupPlan plan, ManifestEntry e, string? toFolder)
    {
        if (toFolder is null) return Path.IsPathFullyQualified(e.LocalPath) ? e.LocalPath : null;
        var prefix = CloudPath.Normalize(plan.RemoteFolder) + "/";
        var remote = CloudPath.Normalize(e.RemotePath);
        if (!remote.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var rel = remote[prefix.Length..];
        if (!BackupPaths.IsSafeRelative(rel)) return null;
        var root = Path.GetFullPath(toFolder);
        var target = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        return target.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, Cmp) ? target : null;
    }

    private static string RestoredName(string target)
    {
        var dir = Path.GetDirectoryName(target)!;
        var name = Path.GetFileName(target);
        var dot = name.LastIndexOf('.');
        var (stem, ext) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");
        var tag = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var candidate = Path.Combine(dir, $"{stem} (restored {tag}){ext}");
        for (var i = 2; File.Exists(candidate) || Directory.Exists(candidate); i++) candidate = Path.Combine(dir, $"{stem} (restored {tag} {i}){ext}");
        return candidate;
    }

    private static async Task<string?> HashFileAsync(string? kind, string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        return await CloudHashes.ComputeAsync(kind, fs, ct);
    }

    /// <summary>
    /// "Back up first" in Review &amp; Clean: uploads the files (folders: everything inside) to
    /// "Miu Backup/Before cleaning/&lt;date&gt;/…" and checks each one. Returns why it failed, or null when all are safe.
    /// </summary>
    public async Task<string?> BackUpBeforeCleaningAsync(CloudAccount account, IReadOnlyList<string> paths, IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        var cloud = accounts.Open(account);
        var stamp = nowLocal().ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture);
        var files = new List<(string Local, string Remote)>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(p));
            var name = leaf;
            for (var i = 2; !used.Add(name); i++) name = $"{leaf} ({i})"; // two items with the same name never overwrite each other

            if (File.Exists(p)) files.Add((p, $"{BeforeCleaningFolder}/{stamp}/{name}"));
            else if (Directory.Exists(p))
            {
                var listing = await Task.Run(() => SyncLister.List(p, ct), ct);
                if (listing.Entries is null || listing.UnknownPaths.Any()) return $"Miu couldn't read everything in {name}, so it wasn't cleaned.";
                files.AddRange(listing.Entries.Select(e => (Path.Combine(p, e.RelPath.Replace('/', Path.DirectorySeparatorChar)), $"{BeforeCleaningFolder}/{stamp}/{name}/{e.RelPath}")));
            }
        }
        var temp = new BackupPlan(0, "Before cleaning", account.Id, [], BeforeCleaningFolder, BackupMode.CopyNew);
        var done = 0;
        foreach (var (local, remote) in files)
        {
            progress?.Report(new BackupProgress(done++, files.Count, 0, Path.GetFileName(local)));
            var (why, verified) = await UploadFileAsync(temp, cloud, new BackupOp(BackupOpKind.Upload, local, remote, new FileInfo(local).Length, "before cleaning"), _ => { }, ct);
            if (why is not null) return $"{Path.GetFileName(local)} couldn't be backed up ({why}), so nothing was cleaned.";
            if (!verified) return $"This cloud didn't confirm {Path.GetFileName(local)} with a checksum, so nothing was cleaned.";
        }
        return null;
    }
}
