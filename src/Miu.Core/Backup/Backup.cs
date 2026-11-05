using System.Globalization;
using Miu.Core.Automation;

namespace Miu.Core.Backup;

public enum BackupMode { CopyNew, Mirror, BackupThenFree }

/// <param name="RemoteName">The folder name used for it in the cloud (stays the same if you rename the local folder).</param>
public sealed record BackupSource(string LocalPath, string RemoteName);

/// <summary>A cloud backup. <see cref="Schedule"/> null means "only when I press Run".</summary>
public sealed record BackupPlan(
    long Id,
    string Name,
    long AccountId,
    IReadOnlyList<BackupSource> Sources,
    string RemoteFolder,
    BackupMode Mode,
    WatchSchedule? Schedule = null,
    int KeepVersions = 5,
    int FreeAfterDays = 30,
    int BandwidthKBps = 0,
    bool Enabled = true,
    DateTime? NextRunLocal = null,
    DateTime? LastRunUtc = null,
    string? LastResult = null)
{
    public string ModeText => Mode switch
    {
        BackupMode.CopyNew => "Copy new and changed",
        BackupMode.Mirror => "Mirror",
        _ => $"Back up, then free files untouched for {FreeAfterDays} days",
    };

    public string ScheduleText => Schedule?.Describe() ?? "when you press Run";
}

/// <summary>What Miu knows it has in the cloud for a local file.</summary>
public sealed record ManifestEntry(string LocalPath, long Size, DateTime MtimeUtc, string? HashKind, string? Hash, string RemotePath, DateTime UploadedUtc, bool Freed = false);

public sealed record LocalFile(string LocalPath, long Size, DateTime MtimeUtc);

public enum BackupOpKind
{
    Upload,
    /// <summary>Mirror: the local file is gone, so its cloud copy moves to .miu-versions.</summary>
    SetAside,
    /// <summary>Back up, then free: move the local file to the Recycle Bin once its cloud copy is verified.</summary>
    Free,
}

public sealed record BackupOp(BackupOpKind Kind, string LocalPath, string RemotePath, long Bytes, string Reason);

public sealed record BackupPlanResult(IReadOnlyList<BackupOp> Ops, string? Refusal, bool RefusalIsFinal = false, IReadOnlyList<string>? MissingSources = null)
{
    public IReadOnlyList<string> MissingSources { get; } = MissingSources ?? [];
    public int Uploads => Ops.Count(o => o.Kind == BackupOpKind.Upload);
    public long UploadBytes => Ops.Where(o => o.Kind == BackupOpKind.Upload).Sum(o => o.Bytes);
    public int SetAsides => Ops.Count(o => o.Kind == BackupOpKind.SetAside);
    public int Frees => Ops.Count(o => o.Kind == BackupOpKind.Free);
    public long FreeBytes => Ops.Where(o => o.Kind == BackupOpKind.Free).Sum(o => o.Bytes);
    public bool IsEmpty => Ops.Count == 0;
}

public static class BackupPaths
{
    public const string VersionsFolder = ".miu-versions";

    public static string Remote(string remoteFolder, string remoteName, string rel) =>
        string.Join('/', new[] { remoteFolder.Trim('/'), remoteName.Trim('/'), rel.Replace('\\', '/').Trim('/') }.Where(p => p.Length > 0));

    /// <summary>"Backup/Photos/a.jpg" → "Backup/.miu-versions/2026-11-05 140309/Photos/a.jpg".</summary>
    public static string Version(string remoteFolder, string remotePath, DateTime stampLocal)
    {
        var root = remoteFolder.Trim('/');
        var inside = root.Length > 0 && remotePath.StartsWith(root + "/", StringComparison.Ordinal) ? remotePath[(root.Length + 1)..] : remotePath;
        return string.Join('/', new[] { root, VersionsFolder, stampLocal.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture), inside }.Where(p => p.Length > 0));
    }

    /// <summary>A relative path that can't climb out of a folder: no "..", no root, no drive.</summary>
    public static bool IsSafeRelative(string rel)
    {
        if (string.IsNullOrWhiteSpace(rel) || rel.StartsWith('/') || rel.StartsWith('\\') || rel.Contains(':')) return false;
        return rel.Split('/', '\\').All(p => p.Length > 0 && p != "." && p != "..");
    }
}

/// <summary>Works out what a backup run would do, without touching anything.</summary>
public static class BackupDiff
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);
    private const int BrakeMinFiles = 20;

    /// <param name="local">Files per source; null when the source folder is missing or unreadable.</param>
    /// <param name="unknownLocal">Local folders Miu couldn't read: their files are unknown, not deleted.</param>
    public static BackupPlanResult Plan(BackupPlan plan, IReadOnlyDictionary<BackupSource, IReadOnlyList<LocalFile>?> local, IReadOnlyList<string> unknownLocal,
        IReadOnlyList<ManifestEntry> manifest, DateTime nowUtc, bool caseInsensitive)
    {
        var cmp = caseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var cmpKind = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var byPath = new Dictionary<string, ManifestEntry>(cmp);
        foreach (var m in manifest) byPath.TryAdd(m.LocalPath, m);
        bool Under(string path, string dir)
        {
            var d = dir.TrimEnd('/', '\\');
            return path.Equals(d, cmpKind) || path.StartsWith(d + Path.DirectorySeparatorChar, cmpKind) || path.StartsWith(d + "/", cmpKind);
        }

        var ops = new List<BackupOp>();
        var missing = new List<string>();
        string? refusal = null;
        var final = false;
        foreach (var src in plan.Sources)
        {
            var files = local.TryGetValue(src, out var f) ? f : null;
            var tracked = manifest.Where(m => !m.Freed && Under(m.LocalPath, src.LocalPath) && !unknownLocal.Any(u => Under(m.LocalPath, u))).ToList();
            if (files is null)
            {
                missing.Add(src.LocalPath);
                if (plan.Mode == BackupMode.Mirror && refusal is null)
                {
                    refusal = $"The folder {src.LocalPath} ({src.RemoteName}) is missing, so mirroring it would set aside its whole backup. Is the drive connected? Nothing was changed.";
                    final = true;
                }
                continue;
            }

            var present = new HashSet<string>(cmp);
            foreach (var file in files)
            {
                if (unknownLocal.Any(u => Under(file.LocalPath, u))) continue;
                present.Add(file.LocalPath);
                var rel = Path.GetRelativePath(src.LocalPath, file.LocalPath);
                var remote = BackupPaths.Remote(plan.RemoteFolder, src.RemoteName, rel);
                byPath.TryGetValue(file.LocalPath, out var m);
                var same = m is not null && m.Size == file.Size && (m.MtimeUtc - file.MtimeUtc).Duration() <= Tolerance;
                if (!same) ops.Add(new BackupOp(BackupOpKind.Upload, file.LocalPath, remote, file.Size, m is null ? "new" : "changed"));
                if (plan.Mode == BackupMode.BackupThenFree && (nowUtc - file.MtimeUtc).TotalDays >= plan.FreeAfterDays)
                    ops.Add(new BackupOp(BackupOpKind.Free, file.LocalPath, remote, file.Size, $"untouched for {(int)(nowUtc - file.MtimeUtc).TotalDays} days"));
            }

            if (plan.Mode != BackupMode.Mirror) continue;
            var gone = tracked.Where(m => !present.Contains(m.LocalPath)).ToList();
            if (files.Count == 0 && tracked.Count > 0 && refusal is null)
            {
                refusal = $"The folder {src.LocalPath} is empty, so mirroring would set aside all {tracked.Count} backed-up files. Is the drive connected? Nothing was changed.";
                final = true;
            }
            else if (gone.Count > BrakeMinFiles && gone.Count * 2 > tracked.Count && refusal is null)
                refusal = $"This would set aside {gone.Count} of {tracked.Count} backed-up files from {src.RemoteName}. That looks like a mistake (a disconnected drive?), so nothing was changed.";
            ops.AddRange(gone.Select(m => new BackupOp(BackupOpKind.SetAside, m.LocalPath, m.RemotePath, m.Size, "deleted on this computer")));
        }
        return new BackupPlanResult(ops, refusal, final, missing);
    }
}
