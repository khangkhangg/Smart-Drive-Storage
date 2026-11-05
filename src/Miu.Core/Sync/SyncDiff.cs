using System.Globalization;

namespace Miu.Core.Sync;

public enum SyncMode { Mirror, CopyOnly, TwoWay }

public enum Side { Source, Target }

/// <summary>A file in a synced folder, by its path relative to the folder root ('/'-separated).</summary>
public sealed record SyncEntry(string RelPath, long Size, DateTime MtimeUtc);

public enum SyncOpKind
{
    /// <summary>Copy the file from <see cref="SyncOp.From"/> to the other side (an existing copy there is archived first).</summary>
    Copy,
    /// <summary>Move the file on <see cref="SyncOp.From"/> into that side's .miu-sync-trash.</summary>
    Archive,
    /// <summary>
    /// Both sides changed. <see cref="SyncOp.From"/> is the newer (winning) side. The loser is renamed to
    /// <see cref="SyncOp.ConflictName"/>, then both versions are copied so each side ends up with both.
    /// </summary>
    Conflict,
}

public sealed record SyncOp(SyncOpKind Kind, Side From, string RelPath, long Bytes, string Reason, string? ConflictName = null);

/// <param name="RefusalIsFinal">True for refusals that "Run anyway" may never override (an empty or vanished folder).</param>
public sealed record SyncPlan(IReadOnlyList<SyncOp> Ops, string? Refusal, int KeptNewerOnTarget = 0, bool RefusalIsFinal = false)
{
    public int Copies => Ops.Count(o => o.Kind == SyncOpKind.Copy);
    public int Archives => Ops.Count(o => o.Kind == SyncOpKind.Archive);
    public int Conflicts => Ops.Count(o => o.Kind == SyncOpKind.Conflict);
    public long CopyBytes => Ops.Where(o => o.Kind != SyncOpKind.Archive).Sum(o => o.Bytes);
    public bool IsEmpty => Ops.Count == 0;

    public string Describe()
    {
        if (Refusal is not null) return Refusal;
        if (IsEmpty) return "Everything is already in sync.";
        var parts = new List<string>();
        if (Copies > 0) parts.Add($"copy {Copies} {(Copies == 1 ? "file" : "files")}");
        if (Archives > 0) parts.Add($"set aside {Archives} removed {(Archives == 1 ? "file" : "files")}");
        if (Conflicts > 0) parts.Add($"keep both versions of {Conflicts} {(Conflicts == 1 ? "file" : "files")} changed on both sides");
        return char.ToUpperInvariant(parts[0][0]) + string.Join(", ", parts)[1..] + ".";
    }
}

/// <summary>
/// Works out what a sync would do, without touching any file. Files are the same when size matches and the
/// modified time is within 2 seconds (FAT and many NAS file systems round to 2 s), or when the caller has
/// confirmed equal content by hash.
/// </summary>
public static class SyncDiff
{
    public static readonly TimeSpan MtimeTolerance = TimeSpan.FromSeconds(2);
    private const int BrakeMinFiles = 20;

    /// <param name="target">null when the target could not be read: the plan is refused.</param>
    /// <param name="baseline">The state both sides agreed on after the last two-way run (null on the first run).</param>
    public static SyncPlan Plan(IReadOnlyCollection<SyncEntry>? source, IReadOnlyCollection<SyncEntry>? target, IReadOnlyCollection<SyncEntry>? baseline,
        SyncMode mode, bool caseInsensitive, DateOnly today, ISet<string>? sameContent = null)
    {
        if (source is null) return new SyncPlan([], "Miu couldn't read the source folder, so nothing was changed.", RefusalIsFinal: true);
        if (target is null) return new SyncPlan([], "Miu couldn't read the target folder, so nothing was changed.", RefusalIsFinal: true);
        var cmp = caseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var s = ToMap(source, cmp);
        var t = ToMap(target, cmp);
        var b = baseline is null ? null : ToMap(baseline, cmp);
        sameContent = sameContent is null ? null : new HashSet<string>(sameContent, cmp);

        bool Same(SyncEntry x, SyncEntry y) =>
            x.Size == y.Size && ((x.MtimeUtc - y.MtimeUtc).Duration() <= MtimeTolerance || sameContent?.Contains(x.RelPath) == true);

        var ops = new List<SyncOp>();
        var keptNewer = 0;
        var taken = new HashSet<string>(s.Keys.Concat(t.Keys), cmp);

        switch (mode)
        {
            case SyncMode.Mirror:
            case SyncMode.CopyOnly:
                foreach (var (rel, se) in s)
                {
                    if (!t.TryGetValue(rel, out var te)) ops.Add(new SyncOp(SyncOpKind.Copy, Side.Source, se.RelPath, se.Size, "new"));
                    else if (!Same(se, te))
                    {
                        if (mode == SyncMode.Mirror) ops.Add(new SyncOp(SyncOpKind.Copy, Side.Source, se.RelPath, se.Size, "changed"));
                        else if (se.MtimeUtc > te.MtimeUtc + MtimeTolerance) ops.Add(new SyncOp(SyncOpKind.Copy, Side.Source, se.RelPath, se.Size, "newer in the source"));
                        else keptNewer++;
                    }
                }
                if (mode == SyncMode.Mirror)
                    foreach (var (rel, te) in t)
                        if (!s.ContainsKey(rel)) ops.Add(new SyncOp(SyncOpKind.Archive, Side.Target, te.RelPath, te.Size, "not in the source"));
                break;

            case SyncMode.TwoWay:
                foreach (var rel in s.Keys.Concat(t.Keys).Distinct(cmp).ToList())
                {
                    s.TryGetValue(rel, out var se);
                    t.TryGetValue(rel, out var te);
                    SyncEntry? be = null;
                    b?.TryGetValue(rel, out be);
                    var changedS = se is not null && (be is null || !Same(se, be));
                    var changedT = te is not null && (be is null || !Same(te, be));
                    if (se is not null && te is not null)
                    {
                        if (Same(se, te)) continue;
                        if (be is not null && changedS && !changedT) ops.Add(new SyncOp(SyncOpKind.Copy, Side.Source, se.RelPath, se.Size, "changed"));
                        else if (be is not null && changedT && !changedS) ops.Add(new SyncOp(SyncOpKind.Copy, Side.Target, te.RelPath, te.Size, "changed"));
                        else
                        {
                            var winner = te.MtimeUtc > se.MtimeUtc ? Side.Target : Side.Source;
                            var w = winner == Side.Source ? se : te;
                            var name = ConflictNames.For(w.RelPath, today, taken.Contains);
                            taken.Add(name);
                            ops.Add(new SyncOp(SyncOpKind.Conflict, winner, w.RelPath, w.Size, "changed on both sides", name));
                        }
                    }
                    else if (se is not null)
                    {
                        if (be is null) ops.Add(new SyncOp(SyncOpKind.Copy, Side.Source, se.RelPath, se.Size, "new"));
                        else if (changedS) ops.Add(new SyncOp(SyncOpKind.Copy, Side.Source, se.RelPath, se.Size, "edited in the source after it was deleted in the target"));
                        else ops.Add(new SyncOp(SyncOpKind.Archive, Side.Source, se.RelPath, se.Size, "deleted in the target"));
                    }
                    else if (te is not null)
                    {
                        if (be is null) ops.Add(new SyncOp(SyncOpKind.Copy, Side.Target, te.RelPath, te.Size, "new"));
                        else if (changedT) ops.Add(new SyncOp(SyncOpKind.Copy, Side.Target, te.RelPath, te.Size, "edited in the target after it was deleted in the source"));
                        else ops.Add(new SyncOp(SyncOpKind.Archive, Side.Target, te.RelPath, te.Size, "deleted in the source"));
                    }
                }
                break;
        }

        var (refusal, final) = Brake(mode, s.Count, t.Count, b?.Count ?? 0, ops);
        return new SyncPlan(ops, refusal, keptNewer, final);
    }

    /// <summary>
    /// Refuses runs that look like a missing or disconnected folder rather than real deletions: they would empty the
    /// other side.
    /// </summary>
    private static (string? Refusal, bool Final) Brake(SyncMode mode, int sourceCount, int targetCount, int baselineCount, List<SyncOp> ops)
    {
        if (mode == SyncMode.Mirror && sourceCount == 0 && targetCount > 0)
            return ($"The source folder is empty, so mirroring would set aside all {targetCount} files in the target. Is the drive connected?", true);
        if (mode == SyncMode.TwoWay && baselineCount > 0 && (sourceCount == 0 || targetCount == 0))
            return ($"The {(sourceCount == 0 ? "source" : "target")} folder is empty since the last sync. Is the drive connected? Nothing was changed.", true);
        foreach (var side in new[] { Side.Source, Side.Target })
        {
            var n = ops.Count(o => o.Kind == SyncOpKind.Archive && o.From == side);
            var total = side == Side.Source ? sourceCount : targetCount;
            if (n > BrakeMinFiles && n * 2 > total)
                return ($"This would set aside {n} of {total} files in the {(side == Side.Source ? "source" : "target")}. That looks like a mistake (a disconnected drive?), so nothing was changed.", false);
        }
        return (null, false);
    }

    private static Dictionary<string, SyncEntry> ToMap(IEnumerable<SyncEntry> entries, StringComparer cmp)
    {
        var d = new Dictionary<string, SyncEntry>(cmp);
        foreach (var e in entries) d.TryAdd(e.RelPath, e);
        return d;
    }
}

public static class ConflictNames
{
    /// <summary>"dir/report.docx" → "dir/report (conflict 2026-11-05).docx", then " 2", " 3"… if that name is taken.</summary>
    public static string For(string relPath, DateOnly date, Func<string, bool> exists) => For(relPath, date, exists, "conflict");

    /// <param name="word">"conflict", or "restored" for files put back from a backup next to a different local file.</param>
    public static string For(string relPath, DateOnly date, Func<string, bool> exists, string word)
    {
        var slash = relPath.LastIndexOf('/');
        var dir = slash >= 0 ? relPath[..(slash + 1)] : "";
        var file = relPath[(slash + 1)..];
        var dot = file.LastIndexOf('.');
        var (stem, ext) = dot > 0 ? (file[..dot], file[dot..]) : (file, "");
        var tag = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        for (var i = 1; ; i++)
        {
            var name = $"{dir}{stem} ({word} {tag}{(i == 1 ? "" : " " + i.ToString(CultureInfo.InvariantCulture))}){ext}";
            if (!exists(name)) return name;
        }
    }
}
