using Miu.Core.Automation;

namespace Miu.Core.Tests.Automation;

public class SchedulerTests
{
    private static DateTime L(int y, int m, int d, int h = 0, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Local);

    [Fact]
    public void Daily_later_today_or_tomorrow()
    {
        var s = WatchSchedule.Daily(new TimeOnly(9, 0));
        Scheduler.NextRun(s, L(2026, 11, 5, 8, 0)).Should().Be(L(2026, 11, 5, 9, 0));
        Scheduler.NextRun(s, L(2026, 11, 5, 9, 0)).Should().Be(L(2026, 11, 6, 9, 0)); // strictly after
    }

    [Fact]
    public void Weekly_on_day()
    {
        var s = WatchSchedule.Weekly(DayOfWeek.Sunday, new TimeOnly(10, 0));
        // 2026-11-05 is a Thursday
        Scheduler.NextRun(s, L(2026, 11, 5, 12, 0)).Should().Be(L(2026, 11, 8, 10, 0));
        Scheduler.NextRun(s, L(2026, 11, 8, 10, 30)).Should().Be(L(2026, 11, 15, 10, 0));
    }

    [Fact]
    public void Monthly_clamps_to_last_day()
    {
        var s = WatchSchedule.Monthly(31, new TimeOnly(8, 0));
        Scheduler.NextRun(s, L(2026, 11, 5)).Should().Be(L(2026, 11, 30, 8, 0));
        Scheduler.NextRun(s, L(2026, 11, 30, 9, 0)).Should().Be(L(2026, 12, 31, 8, 0));
        Scheduler.NextRun(s, L(2027, 1, 31, 9, 0)).Should().Be(L(2027, 2, 28, 8, 0));
    }

    [Fact]
    public void Event_schedules_have_no_clock_time()
    {
        Scheduler.NextRun(WatchSchedule.AtSignIn(), L(2026, 11, 5)).Should().BeNull();
        Scheduler.NextRun(WatchSchedule.LowSpace(10), L(2026, 11, 5)).Should().BeNull();
    }

    [Fact]
    public void Missed_runs_collapse_into_one()
    {
        var s = WatchSchedule.Daily(new TimeOnly(9, 0));
        var lastPlanned = L(2026, 11, 1, 9, 0); // the PC was off for days
        var now = L(2026, 11, 5, 14, 0);
        Scheduler.IsDue(lastPlanned, now).Should().BeTrue();
        Scheduler.NextRun(s, now).Should().Be(L(2026, 11, 6, 9, 0)); // one future slot, no backlog
    }

    [Fact]
    public void Describe_reads_like_a_sentence()
    {
        WatchSchedule.Weekly(DayOfWeek.Sunday, new TimeOnly(10, 0)).Describe().Should().Be("every Sunday at 10:00");
        WatchSchedule.Monthly(1, new TimeOnly(8, 0)).Describe().Should().Be("on day 1 of every month at 08:00");
        WatchSchedule.LowSpace(10).Describe().Should().Be("when the drive has less than 10% free");
    }

    [Fact]
    public void Filters_match_age_and_size()
    {
        var now = new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc);
        var f = new WatchFilters(OlderThanDays: 30, MinSizeMB: 1);
        f.Matches(2 << 20, now.AddDays(-40), now).Should().BeTrue();
        f.Matches(2 << 20, now.AddDays(-5), now).Should().BeFalse();
        f.Matches(100, now.AddDays(-40), now).Should().BeFalse();
        new WatchFilters().Matches(1, now, now).Should().BeTrue();
    }

    [Fact]
    public void Templates_cover_the_spec()
    {
        var t = WatchTemplates.All("/home/u", "/tmp");
        t.Select(x => x.Name).Should().Contain(["Downloads tidy", "Dev projects", "Low-space guard"]);
        if (OperatingSystem.IsWindows()) t.Single(x => x.Name == "Temp & caches").Mode.Should().Be(WatchMode.AutoCleanSafe);
        t.Single(x => x.Name == "Downloads tidy").Path.Should().Be(Path.Combine("/home/u", "Downloads"));
    }
}
