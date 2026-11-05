using System.Globalization;

namespace Miu.Core.Automation;

public enum WatchMode { Suggest, AutoCleanSafe, NotifyOnly }

public enum ScheduleKind { Daily, Weekly, Monthly, AtSignIn, LowSpace }

/// <summary>When a watched folder runs. Times are local.</summary>
public sealed record WatchSchedule(ScheduleKind Kind, TimeOnly Time, DayOfWeek Day = DayOfWeek.Sunday, int DayOfMonth = 1, int LowSpacePercent = 10)
{
    public static WatchSchedule Daily(TimeOnly time) => new(ScheduleKind.Daily, time);
    public static WatchSchedule Weekly(DayOfWeek day, TimeOnly time) => new(ScheduleKind.Weekly, time, day);
    public static WatchSchedule Monthly(int dayOfMonth, TimeOnly time) => new(ScheduleKind.Monthly, time, DayOfMonth: Math.Clamp(dayOfMonth, 1, 31));
    public static WatchSchedule AtSignIn() => new(ScheduleKind.AtSignIn, default);
    public static WatchSchedule LowSpace(int percentFree) => new(ScheduleKind.LowSpace, default, LowSpacePercent: Math.Clamp(percentFree, 1, 90));

    public string Describe()
    {
        var at = Time.ToString("HH:mm", CultureInfo.InvariantCulture);
        return Kind switch
        {
            ScheduleKind.Daily => $"every day at {at}",
            ScheduleKind.Weekly => $"every {Day} at {at}",
            ScheduleKind.Monthly => $"on day {DayOfMonth} of every month at {at}",
            ScheduleKind.AtSignIn => "every time you sign in",
            _ => $"when the drive has less than {LowSpacePercent}% free",
        };
    }
}

/// <summary>Only items at least this old and this big are considered.</summary>
public sealed record WatchFilters(int? OlderThanDays = null, long? MinSizeMB = null)
{
    public bool Matches(long size, DateTime modifiedUtc, DateTime nowUtc) =>
        (OlderThanDays is not { } d || (nowUtc - modifiedUtc).TotalDays >= d) &&
        (MinSizeMB is not { } mb || size >= mb * 1024 * 1024);
}

/// <summary>A folder Miu looks after on a schedule.</summary>
public sealed record Watch(
    long Id,
    string Name,
    string Path,
    WatchMode Mode,
    WatchSchedule Schedule,
    WatchFilters Filters,
    bool Enabled = true,
    DateTime? NextRunLocal = null,
    DateTime? LastRunUtc = null,
    string? LastResult = null);

public static class Scheduler
{
    /// <summary>
    /// The next clock time strictly after <paramref name="afterLocal"/>, or null for event-driven schedules
    /// (sign-in, low space). Runs missed while the computer was off are not replayed: one run, then the next slot.
    /// </summary>
    public static DateTime? NextRun(WatchSchedule s, DateTime afterLocal)
    {
        switch (s.Kind)
        {
            case ScheduleKind.Daily:
            {
                var c = afterLocal.Date + s.Time.ToTimeSpan();
                return c > afterLocal ? c : c.AddDays(1);
            }
            case ScheduleKind.Weekly:
            {
                var c = afterLocal.Date.AddDays(((int)s.Day - (int)afterLocal.DayOfWeek + 7) % 7) + s.Time.ToTimeSpan();
                return c > afterLocal ? c : c.AddDays(7);
            }
            case ScheduleKind.Monthly:
            {
                var month = new DateTime(afterLocal.Year, afterLocal.Month, 1, 0, 0, 0, afterLocal.Kind);
                for (var i = 0; i < 3; i++, month = month.AddMonths(1))
                {
                    var day = Math.Min(s.DayOfMonth, DateTime.DaysInMonth(month.Year, month.Month));
                    var c = month.AddDays(day - 1) + s.Time.ToTimeSpan();
                    if (c > afterLocal) return c;
                }
                return null;
            }
            default:
                return null;
        }
    }

    public static bool IsDue(DateTime? nextRunLocal, DateTime nowLocal) => nextRunLocal is { } n && n <= nowLocal;
}

public static class WatchTemplates
{
    /// <summary>One-tap starting points (spec §5.5).</summary>
    public static IReadOnlyList<Watch> All(string home, string tempDir) =>
    [
        new(0, "Downloads tidy", System.IO.Path.Combine(home, "Downloads"), WatchMode.Suggest,
            WatchSchedule.Weekly(DayOfWeek.Sunday, new TimeOnly(10, 0)), new WatchFilters(OlderThanDays: 30)),
        new(0, "Temp & caches", tempDir, WatchMode.AutoCleanSafe,
            WatchSchedule.Weekly(DayOfWeek.Saturday, new TimeOnly(11, 0)), new WatchFilters(OlderThanDays: 2)),
        new(0, "Dev projects", Directory.Exists(System.IO.Path.Combine(home, "code")) ? System.IO.Path.Combine(home, "code") : home,
            WatchMode.Suggest, WatchSchedule.Monthly(1, new TimeOnly(19, 0)), new WatchFilters()),
        new(0, "Low-space guard", home, WatchMode.NotifyOnly, WatchSchedule.LowSpace(10), new WatchFilters()),
    ];
}
