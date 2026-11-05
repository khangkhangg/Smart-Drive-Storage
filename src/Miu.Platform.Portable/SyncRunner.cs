using System.Globalization;
using Miu.Core.Sync;

namespace Miu.Platform.Portable;

public sealed record SyncProgress(int Done, int Total, long Bytes, string Current);

/// <summary>A file Miu set aside instead of deleting it, so it can be put back.</summary>
public sealed record ArchivedFile(string OriginalPath, string ArchivePath, long Bytes);

public sealed record SyncRunResult(
    int Copied,
    int Archived,
    int Conflicts,
    long BytesCopied,
    IReadOnlyList<ArchivedFile> ArchivedFiles,
    IReadOnlyList<(string RelPath, string Reason)> Failed,
    int ChangedDuringSync,
    IReadOnlyList<string>? ChangedPaths = null)
{
    public IReadOnlyList<string> ChangedPaths { get; } = ChangedPaths ?? [];

    public string Describe(string? trashHint = null)
    {
        var parts = new List<string> { $"Copied {Copied} {(Copied == 1 ? "file" : "files")}" };
        if (Archived > 0) parts.Add($"set aside {Archived}{trashHint}");
        if (Conflicts > 0) parts.Add($"kept both versions of {Conflicts}");
        if (ChangedDuringSync > 0) parts.Add($"left {ChangedDuringSync} that changed during the sync for next time");
        if (Failed.Count > 0) parts.Add($"{Failed.Count} failed");
        return string.Join(", ", parts) + ".";
    }
}

/// <summary>
/// Carries out a <see cref="SyncPlan"/>. Nothing is ever deleted: a file that would be removed or overwritten is moved
/// into <c>.miu-sync-trash/&lt;time&gt;/</c> on its own side. Copies are written to a <c>.miu-partial</c> file named
/// after the source version and renamed into place only when complete, so a cancelled run never leaves a
/// half-written file and a resumed copy can't mix two versions.
/// </summary>
public sealed class SyncRunner(string sourceRoot, string targetRoot, IReadOnlyCollection<SyncEntry> sourceList, IReadOnlyCollection<SyncEntry> targetList,
    bool caseInsensitive, DateTime nowLocal)
{
    private const int Buffer = 1 << 20;
    private readonly Dictionary<string, SyncEntry> _s = ToMap(sourceList, caseInsensitive);
    private readonly Dictionary<string, SyncEntry> _t = ToMap(targetList, caseInsensitive);
    private readonly string _stamp = nowLocal.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);
    private readonly List<ArchivedFile> _archived = [];
    private readonly List<(string, string)> _failed = [];
    private readonly List<string> _changedPaths = [];
    private int _copied, _conflicts;
    private long _bytes;

    private sealed class ChangedException(string message) : IOException(message);

    /// <summary>What was set aside so far, also when the run is cancelled or fails part-way.</summary>
    public IReadOnlyList<ArchivedFile> ArchivedSoFar => _archived;

    public string TrashPath(Side side) => Path.Combine(Root(side), SyncLister.TrashFolder, _stamp);

    public async Task<SyncRunResult> RunAsync(SyncPlan plan, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        if (plan.Refusal is not null) throw new InvalidOperationException(plan.Refusal);
        var done = 0;
        foreach (var op in plan.Ops)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new SyncProgress(done, plan.Ops.Count, _bytes, op.RelPath));
            try
            {
                switch (op.Kind)
                {
                    case SyncOpKind.Copy:
                        await CopyAsync(op.From, op.RelPath, op.RelPath, Expected(op.From, op.RelPath), ct);
                        _copied++;
                        break;
                    case SyncOpKind.Archive:
                        Verify(op.From, op.RelPath, Expected(op.From, op.RelPath));
                        Archive(op.From, op.RelPath);
                        break;
                    case SyncOpKind.Conflict:
                        await ConflictAsync(op, ct);
                        _conflicts++;
                        break;
                }
            }
            catch (ChangedException) { _changedPaths.Add(op.RelPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _failed.Add((op.RelPath, ex.Message)); }
            done++;
        }
        progress?.Report(new SyncProgress(done, plan.Ops.Count, _bytes, ""));
        return new SyncRunResult(_copied, _archived.Count, _conflicts, _bytes, _archived, _failed, _changedPaths.Count, _changedPaths);
    }

    private async Task ConflictAsync(SyncOp op, CancellationToken ct)
    {
        var winner = op.From;
        var loser = Other(winner);
        var w = Expected(winner, op.RelPath);
        var l = Expected(loser, op.RelPath);
        Verify(winner, op.RelPath, w);
        Verify(loser, op.RelPath, l);
        var conflict = op.ConflictName!;
        var loserConflictPath = Full(loser, conflict);
        if (File.Exists(loserConflictPath)) throw new IOException("A file with the conflict name already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(loserConflictPath)!);
        File.Move(Full(loser, op.RelPath), loserConflictPath);
        await CopyAsync(winner, op.RelPath, op.RelPath, w, ct, destinationMayBeMissing: true);
        await CopyAsync(loser, conflict, conflict, l with { RelPath = conflict }, ct);
    }

    private async Task CopyAsync(Side from, string fromRel, string toRel, SyncEntry expected, CancellationToken ct, bool destinationMayBeMissing = false)
    {
        var src = Full(from, fromRel);
        var dst = Full(Other(from), toRel);
        Verify(from, fromRel, expected);
        VerifyDestination(Other(from), toRel, destinationMayBeMissing);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        var fingerprint = $"{expected.Size:x}-{expected.MtimeUtc.Ticks:x}";
        var partial = $"{dst}.{fingerprint}{SyncLister.PartialSuffix}";
        // Partials of other source versions are Miu's own temp files: remove them.
        var ownStale = new System.Text.RegularExpressions.Regex(
            "^" + System.Text.RegularExpressions.Regex.Escape(Path.GetFileName(dst)) + @"\.[0-9a-f]+-[0-9a-f]+\.miu-partial$");
        foreach (var stale in Directory.EnumerateFiles(Path.GetDirectoryName(dst)!, Path.GetFileName(dst) + ".*" + SyncLister.PartialSuffix))
            if (!string.Equals(stale, partial, StringComparison.Ordinal) && ownStale.IsMatch(Path.GetFileName(stale)))
                File.Delete(stale); // only Miu's own partial copies of this file

        await using (var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, Buffer, useAsync: true))
        await using (var output = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, Buffer, useAsync: true))
        {
            var offset = output.Length <= input.Length ? output.Length : 0;
            output.SetLength(offset);
            output.Position = offset;
            input.Position = offset;
            var buf = new byte[Buffer];
            int n;
            while ((n = await input.ReadAsync(buf, ct)) > 0)
            {
                await output.WriteAsync(buf.AsMemory(0, n), ct);
                _bytes += n;
            }
            await output.FlushAsync(ct);
        }
        try
        {
            Verify(from, fromRel, expected);
            VerifyDestination(Other(from), toRel, destinationMayBeMissing);
        }
        catch (ChangedException) { File.Delete(partial); throw; }
        File.SetLastWriteTimeUtc(partial, expected.MtimeUtc);
        if (File.Exists(dst)) Archive(Other(from), toRel);
        File.Move(partial, dst);
    }

    /// <summary>
    /// The file a copy will replace must still be the one in the plan (or still be absent): otherwise someone edited
    /// it during the run, and replacing it would hide that edit.
    /// </summary>
    private void VerifyDestination(Side side, string rel, bool mayBeMissing)
    {
        var path = Full(side, rel);
        if ((side == Side.Source ? _s : _t).TryGetValue(rel, out var planned))
        {
            if (mayBeMissing && !File.Exists(path)) return;
            Verify(side, rel, planned);
        }
        else if (File.Exists(path) || Directory.Exists(path)) throw new ChangedException($"{rel} appeared after the preview.");
    }

    private void Archive(Side side, string rel)
    {
        var original = Full(side, rel);
        var dest = Path.Combine(TrashPath(side), rel.Replace('/', Path.DirectorySeparatorChar));
        var name = Path.GetFileName(original);
        var dot = name.LastIndexOf('.');
        var (stem, ext) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");
        for (var i = 2; File.Exists(dest); i++) dest = Path.Combine(Path.GetDirectoryName(dest)!, $"{stem} ({i}){ext}");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var size = new FileInfo(original).Length;
        File.Move(original, dest);
        _archived.Add(new ArchivedFile(original, dest, size));
    }

    /// <summary>Throws when the file is no longer the version the plan was made from.</summary>
    private void Verify(Side side, string rel, SyncEntry expected)
    {
        var f = new FileInfo(Full(side, rel));
        if (!f.Exists || f.Length != expected.Size || (f.LastWriteTimeUtc - expected.MtimeUtc).Duration() > SyncDiff.MtimeTolerance)
            throw new ChangedException($"{rel} changed after the preview.");
    }

    private SyncEntry Expected(Side side, string rel) =>
        (side == Side.Source ? _s : _t).TryGetValue(rel, out var e) ? e : throw new ChangedException($"{rel} is not in the listing.");

    private string Root(Side side) => side == Side.Source ? sourceRoot : targetRoot;
    private static Side Other(Side side) => side == Side.Source ? Side.Target : Side.Source;

    private string Full(Side side, string rel)
    {
        var root = Path.GetFullPath(Root(side));
        var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSep, caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException($"{rel} is outside the sync folder.");
        // Never go through a link (or junction) below the root: it could lead anywhere.
        for (var dir = Path.GetDirectoryName(full); dir is not null && dir.Length > root.Length; dir = Path.GetDirectoryName(dir))
        {
            var info = new DirectoryInfo(dir);
            if (info.Exists && ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null))
                throw new IOException($"{rel} is inside a linked folder, so Miu left it alone.");
        }
        if (File.Exists(full) && new FileInfo(full).LinkTarget is not null) throw new IOException($"{rel} is a link, so Miu left it alone.");
        return full;
    }

    private static Dictionary<string, SyncEntry> ToMap(IEnumerable<SyncEntry> entries, bool ci)
    {
        var d = new Dictionary<string, SyncEntry>(ci ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var e in entries) d.TryAdd(e.RelPath, e);
        return d;
    }
}
