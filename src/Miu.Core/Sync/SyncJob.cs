using Miu.Core.Automation;

namespace Miu.Core.Sync;

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
    string? LastResult = null)
{
    public string ModeText => Mode switch
    {
        SyncMode.Mirror => "Mirror",
        SyncMode.CopyOnly => "Copy new and changed",
        _ => "Two-way",
    };

    public string ScheduleText => Schedule?.Describe() ?? "when you press Run";
}
