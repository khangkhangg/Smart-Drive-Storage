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
        return null;
    }

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
                            if (await UploadWithRetryAsync(plan, cloud, op, p => progress?.Report(new BackupProgress(done, total, b0 + p, Path.GetFileName(op.LocalPath))), ct) is { } why)
                                failed.Add((op.LocalPath, why));
                            else { uploaded++; bytes += op.Bytes; }
                        }
                        else
                        {
                            await KeepAsVersionAsync(plan, cloud, op.LocalPath, op.RemotePath, "deleted on this computer", ct);
                            services.BackupManifest.Remove(plan.Id, op.LocalPath);
                            setAside++;
                        }
                    }
                    catch (CloudException ex) { failed.Add((op.LocalPath, ex.Message)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add((op.LocalPath, ex.Message)); }
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
        catch (OperationCanceledException)
        {
            Log(plan, started, new BackupOutcome("cancelled", $"Stopped after {uploaded} {(uploaded == 1 ? "file" : "files")}. The next run continues from there."));
            throw;
        }
        catch (Exception ex) when (ex is CloudException or IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            outcome = new BackupOutcome("failed", "Couldn't back up: " + ex.Message, uploaded, bytes, Failed: failed);
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

    /// <summary>Uploads one file, retrying network hiccups. Returns null when done and verified, or why it failed.</summary>
    private async Task<string?> UploadWithRetryAsync(BackupPlan plan, ICloudStorage cloud, BackupOp op, Action<long> onBytes, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await UploadOnceAsync(plan, cloud, op, onBytes, firstAttempt: attempt == 0, ct); }
            catch (CloudException ex) when (ex.IsTransient && attempt < 2) { await Task.Delay(RetryDelay(attempt), ct); }
            catch (ChecksumMismatch) when (attempt < 2) { await Task.Delay(RetryDelay(attempt), ct); }
            catch (ChecksumMismatch ex) { return ex.Message; }
        }
    }

    private sealed class ChecksumMismatch(string message) : Exception(message);

    private async Task<string?> UploadOnceAsync(BackupPlan plan, ICloudStorage cloud, BackupOp op, Action<long> onBytes, bool firstAttempt, CancellationToken ct)
    {
        var before = new FileInfo(op.LocalPath);
        if (!before.Exists) return "it was removed before it could be uploaded";
        var (size, mtime) = (before.Length, before.LastWriteTimeUtc);

        // Never replace a cloud file without keeping it as an older version first. (On a retry, what's there is
        // this run's own failed attempt, not a version worth keeping.)
        if (firstAttempt && await cloud.GetAsync(op.RemotePath, ct) is not null)
            await KeepAsVersionAsync(plan, cloud, op.LocalPath, op.RemotePath, "replaced by a newer copy", ct);

        var kind = cloud.ExpectedHashKind(size);
        var hasher = CloudHashes.Create(kind);
        CloudItem item;
        await using (var file = new FileStream(op.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
        {
            var hashing = new HashingStream(file, hasher);
            Stream body = plan.BandwidthKBps > 0 ? new ThrottledStream(hashing, plan.BandwidthKBps * 1024L) : hashing;
            item = await cloud.UploadAsync(op.RemotePath, body, size, new Progress<long>(onBytes), ct);
            if (hashing.BytesRead != size) return "it changed size while it was being uploaded";
        }
        var after = new FileInfo(op.LocalPath);
        if (!after.Exists || after.Length != size || (after.LastWriteTimeUtc - mtime).Duration() > Tolerance)
            return "it changed while it was being uploaded; it will be uploaded next time";
        if (item.Size != size) throw new ChecksumMismatch($"the cloud stored {item.Size} bytes instead of {size}");
        var localHash = hasher?.Finish();
        var verified = item.HashKind is not null && item.HashKind == kind && CloudHashes.Equal(kind, item.Hash, localHash);
        if (item.HashKind is not null && !verified) throw new ChecksumMismatch("the cloud copy's checksum doesn't match");
        if (plan.Id > 0)
            services.BackupManifest.Save(plan.Id, new ManifestEntry(op.LocalPath, size, mtime, verified ? kind : null, verified ? localHash : null, op.RemotePath, DateTime.UtcNow));
        return null;
    }

    /// <summary>Moves a cloud file into .miu-versions and keeps only the newest N versions of it.</summary>
    private async Task KeepAsVersionAsync(BackupPlan plan, ICloudStorage cloud, string localPath, string remotePath, string reason, CancellationToken ct)
    {
        var versionPath = BackupPaths.Version(plan.RemoteFolder, remotePath, nowLocal());
        for (var i = 2; await cloud.GetAsync(versionPath, ct) is not null; i++)
            versionPath = BackupPaths.Version(plan.RemoteFolder, remotePath, nowLocal()) + $" ({i})";
        try { await cloud.MoveAsync(remotePath, versionPath, ct); }
        catch (CloudException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound) { return; } // nothing there any more
        if (plan.Id <= 0) return;
        services.BackupManifest.AddVersion(plan.Id, localPath, versionPath, DateTime.UtcNow, reason);
        foreach (var old in services.BackupManifest.Versions(plan.Id, localPath).Skip(Math.Max(1, plan.KeepVersions)))
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
        var manifest = services.BackupManifest.For(plan.Id).ToDictionary(m => m.LocalPath, services.CaseInsensitivePaths ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var ready = new List<(BackupOp Op, ManifestEntry Entry)>();
        foreach (var op in frees)
        {
            ct.ThrowIfCancellationRequested();
            if (!manifest.TryGetValue(op.LocalPath, out var m) || m.Freed) continue; // not backed up (its upload failed)
            if (m.Hash is null || m.HashKind is null) { failed.Add((op.LocalPath, "kept: this cloud doesn't confirm a checksum, so Miu can't prove the copy is complete")); continue; }
            var fi = new FileInfo(op.LocalPath);
            if (!fi.Exists || fi.Length != m.Size || (fi.LastWriteTimeUtc - m.MtimeUtc).Duration() > Tolerance) continue; // changed: back it up again first
            var remote = await cloud.GetAsync(m.RemotePath, ct);
            if (remote is null || remote.Size != m.Size || remote.HashKind != m.HashKind || !CloudHashes.Equal(m.HashKind, remote.Hash, m.Hash))
            {
                failed.Add((op.LocalPath, "kept: its cloud copy doesn't match"));
                continue;
            }
            ready.Add((op, m));
        }
        if (ready.Count == 0) return (0, 0, 0, failed);

        long freedBytes = 0, actionId = 0;
        var freed = 0;
        var cleanup = new CleanupService(services);
        foreach (var src in plan.Sources)
        {
            var here = ready.Where(r => r.Op.LocalPath.StartsWith(Path.TrimEndingDirectorySeparator(src.LocalPath) + Path.DirectorySeparatorChar, Cmp)).ToList();
            if (here.Count == 0) continue;
            // A fresh scan so Keep rules and protected locations are honoured, exactly as in a normal clean.
            var session = await ScanSession.RunAsync(services, src.LocalPath, null, ct);
            var items = here.Select(r => new CartItem(r.Op.LocalPath, Path.GetFileName(r.Op.LocalPath), r.Op.Bytes, Verdict.Safe, "Backed up and verified in the cloud")).ToList();
            var outcome = await cleanup.CleanAsync(items, session, "backup", ct: ct);
            failed.AddRange(outcome.Failed.Select(f => (f.Path, "kept: " + f.Reason)));
            var failedPaths = outcome.Failed.Select(f => f.Path).ToHashSet();
            foreach (var (op, entry) in here.Where(r => !failedPaths.Contains(r.Op.LocalPath) && !File.Exists(r.Op.LocalPath)))
            {
                services.BackupManifest.Save(plan.Id, entry with { Freed = true });
                freed++;
            }
            freedBytes += outcome.FreedBytes;
            if (outcome.ActionId > 0) actionId = outcome.ActionId;
        }
        return (freed, freedBytes, actionId, failed);
    }

    /// <summary>
    /// Downloads files back. A different local file is never overwritten: the copy is saved as
    /// "name (restored YYYY-MM-DD).ext" next to it. <paramref name="toFolder"/> null means "where they were".
    /// </summary>
    public async Task<RestoreOutcome> RestoreAsync(BackupPlan plan, IReadOnlyList<ManifestEntry> entries, string? toFolder, IProgress<BackupProgress>? progress = null,
        CancellationToken ct = default)
    {
        var cloud = accounts.Open(services.CloudAccounts.Get(plan.AccountId) ?? throw new CloudException("The cloud account for this backup is gone."));
        var failed = new List<(string, string)>();
        var renamed = new List<string>();
        int restored = 0, skipped = 0, done = 0;
        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new BackupProgress(done++, entries.Count, 0, Path.GetFileName(e.LocalPath)));
            var target = TargetFor(plan, e, toFolder);
            if (target is null) { failed.Add((e.LocalPath, "its path isn't safe to restore")); continue; }
            try
            {
                var fi = new FileInfo(target);
                if (fi.Exists && fi.Length == e.Size && (fi.LastWriteTimeUtc - e.MtimeUtc).Duration() <= Tolerance) { skipped++; continue; }
                if (fi.Exists || Directory.Exists(target)) { target = RestoredName(target); renamed.Add(target); }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var partial = target + SyncLister.PartialSuffix;
                await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                    await cloud.DownloadAsync(e.RemotePath, output, ct);
                var got = new FileInfo(partial).Length;
                var hashOk = e.Hash is null || CloudHashes.Equal(e.HashKind, e.Hash, await HashFileAsync(e.HashKind, partial, ct));
                if (got != e.Size || !hashOk)
                {
                    File.Delete(partial);
                    failed.Add((e.LocalPath, "the downloaded copy didn't match the backup"));
                    continue;
                }
                File.SetLastWriteTimeUtc(partial, e.MtimeUtc);
                File.Move(partial, target);
                restored++;
                if (e.Freed && string.Equals(target, e.LocalPath, Cmp)) services.BackupManifest.Save(plan.Id, e with { Freed = false });
            }
            catch (CloudException ex) { failed.Add((e.LocalPath, ex.Message)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add((e.LocalPath, ex.Message)); }
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
        foreach (var p in paths)
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(p));
            if (File.Exists(p)) files.Add((p, $"Miu Backup/Before cleaning/{stamp}/{name}"));
            else if (Directory.Exists(p))
            {
                var listing = await Task.Run(() => SyncLister.List(p, ct), ct);
                if (listing.Entries is null || listing.UnknownPaths.Any()) return $"Miu couldn't read everything in {name}, so it wasn't cleaned.";
                files.AddRange(listing.Entries.Select(e => (Path.Combine(p, e.RelPath.Replace('/', Path.DirectorySeparatorChar)), $"Miu Backup/Before cleaning/{stamp}/{name}/{e.RelPath}")));
            }
        }
        var temp = new BackupPlan(0, "Before cleaning", account.Id, [], "Miu Backup/Before cleaning", BackupMode.CopyNew);
        var done = 0;
        foreach (var (local, remote) in files)
        {
            progress?.Report(new BackupProgress(done++, files.Count, 0, Path.GetFileName(local)));
            var why = await UploadWithRetryAsync(temp, cloud, new BackupOp(BackupOpKind.Upload, local, remote, new FileInfo(local).Length, "before cleaning"), _ => { }, ct);
            if (why is not null) return $"{Path.GetFileName(local)} couldn't be backed up ({why}), so nothing was cleaned.";
        }
        return null;
    }
}
