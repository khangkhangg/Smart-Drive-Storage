using Miu.Core.Automation;

namespace Miu.Core.Sync;

/// <summary>What the job is for: a folder sync/mirror, or a file-level backup of a whole drive.</summary>
public enum SyncKind { Folder, DriveBackup }

/// <summary>A sync between two folders. <see cref="Schedule"/> null means "only when I press Run".</summary>
public sealed record SyncJob(
    long Id,
    string Name,
    string Source,
    string Target,
    SyncMode Mode,
    WatchSchedule? Schedule = null,
    bool CompareByHash = false,
    bool Enabled = true,
    DateTime? NextRunLocal = null,
    DateTime? LastRunUtc = null,
    string? LastResult = null,
    string[]? Excludes = null,
    bool RunOnChange = false,
    SyncKind Kind = SyncKind.Folder)
{
    public string ModeText => Mode switch
    {
        SyncMode.Mirror => "Mirror",
        SyncMode.CopyOnly => "Copy new and changed",
        _ => "Two-way",
    };

    public string ScheduleText => RunOnChange
        ? "when files change" + (Schedule is { } s ? ", and " + s.Describe() : "")
        : Schedule?.Describe() ?? "when you press Run";

    public string KindText => Kind == SyncKind.DriveBackup ? "Drive backup" : ModeText;
}
