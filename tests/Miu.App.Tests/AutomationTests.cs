using Avalonia.Headless.XUnit;
using Miu.App.Services;
using Miu.Core.Automation;
using Miu.Data;
using Miu.Platform.Abstractions;

namespace Miu.App.Tests;

public sealed class AutomationTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "miu-auto-" + Guid.NewGuid().ToString("N"));
    private readonly CleanupServiceTests.FakeBin _bin = new();
    private readonly FakeNotifier _notes = new();
    private readonly FakePower _power = new();
    private DateTime _now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Local);

    public AutomationTests()
    {
        void F(string rel, int size, int age)
        {
            var p = Path.Combine(_root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, new byte[size]);
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-age));
        }
        F("crash/app.dmp", 4000, 30);    // Safe (old crash dump)
        F("crash/fresh.dmp", 4000, 1);   // not yet Safe (too new)
        F("notes/todo.txt", 100, 30);    // Review
    }

    public void Dispose() => Directory.Delete(_root, true);

    private (AppServices S, AutomationService A) Setup()
    {
        var s = AppServices.Create(":memory:", _bin);
        var a = new AutomationService(s, _notes, _power, () => _now, _ => { });
        return (s, a);
    }

    private static Watch W(string path, WatchMode mode, DateTime? next) =>
        new(0, "Test", path, mode, WatchSchedule.Daily(new TimeOnly(9, 0)), new WatchFilters(), NextRunLocal: next);

    [AvaloniaFact]
    public async Task Suggest_notifies_with_safe_items_only()
    {
        var (s, a) = Setup();
        s.Watches.Add(W(_root, WatchMode.Suggest, _now.AddMinutes(-1)));
        await a.TickAsync();
        var n = _notes.Shown.Single();
        n.Body.Should().Contain("1 item").And.Contain("3.9 KB");
        n.Actions.Select(x => x.Label).Should().Contain("Review");
        _bin.Received.Should().BeEmpty();
        var w = s.Watches.All().Single();
        w.NextRunLocal.Should().Be(new DateTime(2026, 11, 6, 9, 0, 0, DateTimeKind.Local));
        w.LastResult.Should().Contain("1 item");
    }

    [AvaloniaFact]
    public async Task Auto_clean_without_opt_in_only_suggests()
    {
        var (s, a) = Setup();
        s.Watches.Add(W(_root, WatchMode.AutoCleanSafe, _now.AddMinutes(-1)));
        await a.TickAsync();
        _bin.Received.Should().BeEmpty();
        _notes.Shown.Single().Body.Should().Contain("Automatic cleaning is off");
    }

    [AvaloniaFact]
    public async Task Auto_clean_with_opt_in_cleans_safe_items_and_logs()
    {
        var (s, a) = Setup();
        s.Settings.SetBool(SettingKeys.AutoCleanAllowed, true);
        s.Watches.Add(W(_root, WatchMode.AutoCleanSafe, _now.AddMinutes(-1)));
        await a.TickAsync();
        _bin.Received.Should().Equal(Path.Combine(_root, "crash", "app.dmp"));
        var action = s.Actions.Recent(1).Single();
        action.Origin.Should().Be("auto");
        _notes.Shown.Single().Actions.Select(x => x.Label).Should().Contain("Undo");
        s.Decisions.All().Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task Missing_folder_is_logged_as_failed()
    {
        var (s, a) = Setup();
        s.Watches.Add(W(Path.Combine(_root, "nope"), WatchMode.Suggest, _now.AddMinutes(-1)));
        await a.TickAsync();
        var w = s.Watches.All().Single();
        w.LastResult.Should().Contain("not found");
        new JobRunsRepository(s.Database).Recent(w.Id, 1).Single().Status.Should().Be("failed");
    }

    [AvaloniaFact]
    public async Task Battery_postpones_without_losing_the_run()
    {
        var (s, a) = Setup();
        _power.OnBattery = true;
        var due = _now.AddMinutes(-1);
        s.Watches.Add(W(_root, WatchMode.Suggest, due));
        await a.TickAsync();
        _notes.Shown.Should().BeEmpty();
        s.Watches.All().Single().NextRunLocal.Should().Be(_now.AddMinutes(30));
    }

    [AvaloniaFact]
    public async Task Pause_and_quiet_hours_postpone()
    {
        var (s, a) = Setup();
        s.Watches.Add(W(_root, WatchMode.Suggest, _now.AddMinutes(-1)));
        a.PauseFor(TimeSpan.FromHours(1));
        await a.TickAsync();
        _notes.Shown.Should().BeEmpty();
        a.Resume();
        s.Settings.Set(SettingKeys.QuietStartHour, "11");
        s.Settings.Set(SettingKeys.QuietEndHour, "13");
        s.Watches.Update(s.Watches.All().Single() with { NextRunLocal = _now.AddMinutes(-1) });
        await a.TickAsync();
        _notes.Shown.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task Missed_runs_collapse_into_one()
    {
        var (s, a) = Setup();
        s.Watches.Add(W(_root, WatchMode.NotifyOnly, _now.AddDays(-5)));
        await a.TickAsync();
        await a.TickAsync();
        _notes.Shown.Should().HaveCount(1);
        s.Watches.All().Single().NextRunLocal.Should().BeAfter(_now);
    }

    [AvaloniaFact]
    public async Task New_clock_watch_gets_a_next_run()
    {
        var (s, a) = Setup();
        s.Watches.Add(W(_root, WatchMode.Suggest, null));
        await a.TickAsync();
        _notes.Shown.Should().BeEmpty();
        s.Watches.All().Single().NextRunLocal.Should().Be(new DateTime(2026, 11, 6, 9, 0, 0, DateTimeKind.Local));
    }

    [AvaloniaFact]
    public async Task Weekly_digest_after_seven_days()
    {
        var (_, a) = Setup();
        await a.TickAsync();
        _notes.Shown.Should().BeEmpty();
        _now = _now.AddDays(7).AddMinutes(1);
        await a.TickAsync();
        _notes.Shown.Single().Title.Should().Be("Your week with Miu");
    }

    [AvaloniaFact]
    public async Task Low_space_watch_only_notifies_when_space_is_low()
    {
        var (s, a) = Setup();
        s.Watches.Add(new Watch(0, "Low", _root, WatchMode.NotifyOnly, WatchSchedule.LowSpace(1), new WatchFilters()));
        await a.TickAsync();
        _notes.Shown.Should().BeEmpty(); // the test machine has more than 1% free
    }

    internal sealed class FakeNotifier : INotifier
    {
        public List<MiuNotification> Shown { get; } = [];
        public void Show(MiuNotification n) => Shown.Add(n);
    }

    private sealed class FakePower : IPowerState
    {
        public bool OnBattery { get; set; }
        public bool IsOnBattery => OnBattery;
    }
}
