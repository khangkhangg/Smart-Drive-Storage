using Avalonia.Headless.XUnit;
using Miu.App.Services;
using Miu.App.ViewModels;
using Miu.Core.Automation;
using Miu.Data;
using Miu.Platform.Abstractions;

namespace Miu.App.Tests;

public sealed class AutomationSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "miu-asafe-" + Guid.NewGuid().ToString("N"));
    private readonly AutomationTests.FakeNotifier _notes = new();
    private DateTime _now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Local);
    private double _freePercent = 50;

    public AutomationSafetyTests()
    {
        var p = Path.Combine(_root, "crash", "app.dmp");
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[4000]);
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-30));
    }

    public void Dispose() => Directory.Delete(_root, true);

    private AutomationService Auto(AppServices s) => new(s, _notes, new AlwaysPlugged(), () => _now, _ => { }, _ => _freePercent);

    private static Watch W(string path, WatchMode mode, DateTime? next, WatchSchedule? schedule = null) =>
        new(0, "Test", path, mode, schedule ?? WatchSchedule.Daily(new TimeOnly(9, 0)), new WatchFilters(), NextRunLocal: next);

    [AvaloniaFact]
    public async Task Run_now_with_a_stale_row_does_not_re_enable_the_watch()
    {
        var s = AppServices.Create(":memory:", new CleanupServiceTests.FakeBin());
        var vm = new MainWindowViewModel(s, _notes);
        s.Watches.Add(W(_root, WatchMode.Suggest, _now.AddDays(1)));
        vm.Automations.Load();
        var row = vm.Automations.Rows.Single();
        row.Enabled = false;
        await row.RunNowCommand.ExecuteAsync(null);
        s.Watches.All().Single().Enabled.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task Toggling_after_a_run_keeps_the_new_next_run()
    {
        var s = AppServices.Create(":memory:", new CleanupServiceTests.FakeBin());
        var vm = new MainWindowViewModel(s, _notes);
        var id = s.Watches.Add(W(_root, WatchMode.NotifyOnly, _now.AddMinutes(-1)));
        vm.Automations.Load();
        var row = vm.Automations.Rows.Single();
        await Auto(s).TickAsync();
        var next = s.Watches.All().Single().NextRunLocal;
        row.Enabled = false;
        row.Enabled = true;
        s.Watches.All().Single(w => w.Id == id).NextRunLocal.Should().Be(next);
    }

    [AvaloniaFact]
    public void Editing_keeps_enabled_state_and_last_result()
    {
        var s = AppServices.Create(":memory:", new CleanupServiceTests.FakeBin());
        var vm = new MainWindowViewModel(s, _notes);
        var id = s.Watches.Add(W(_root, WatchMode.Suggest, null) with { Enabled = false, LastResult = "Found 1 item", Filters = new WatchFilters(MinSizeMB: 5) });
        vm.Automations.Load();
        vm.Automations.Rows.Single().EditCommand.Execute(null);
        vm.Automations.Editor.Name = "Renamed";
        vm.Automations.Editor.SaveCommand.Execute(null);
        var w = s.Watches.All().Single(x => x.Id == id);
        w.Name.Should().Be("Renamed");
        w.Enabled.Should().BeFalse();
        w.LastResult.Should().Be("Found 1 item");
        w.Filters.MinSizeMB.Should().Be(5);
    }

    [AvaloniaFact]
    public async Task Undo_of_an_automatic_clean_is_not_cleaned_again()
    {
        var trash = Path.Combine(_root, "..", Path.GetFileName(_root) + "-trash");
        var s = AppServices.Create(":memory:", new Miu.Platform.Portable.FreedesktopTrash(trash));
        s.Settings.SetBool(SettingKeys.AutoCleanAllowed, true);
        var a = Auto(s);
        s.Watches.Add(W(_root, WatchMode.AutoCleanSafe, _now.AddMinutes(-1)));
        await a.TickAsync();
        var dmp = Path.Combine(_root, "crash", "app.dmp");
        File.Exists(dmp).Should().BeFalse();
        await _notes.Shown.Single().Actions.Single(x => x.Label == "Undo").Run();
        File.Exists(dmp).Should().BeTrue();

        _now = _now.AddDays(1);
        s.Watches.Update(s.Watches.All().Single() with { NextRunLocal = _now.AddMinutes(-1) });
        await a.TickAsync();
        File.Exists(dmp).Should().BeTrue();
        Directory.Delete(trash, true);
    }

    [AvaloniaFact]
    public async Task Low_space_notifies_once_per_episode()
    {
        var s = AppServices.Create(":memory:", new CleanupServiceTests.FakeBin());
        var a = Auto(s);
        s.Watches.Add(W(_root, WatchMode.NotifyOnly, null, WatchSchedule.LowSpace(10)));
        _freePercent = 5;
        for (var i = 0; i < 3; i++) { await a.TickAsync(); _now = _now.AddMinutes(31); }
        _notes.Shown.Should().ContainSingle();
        _freePercent = 40;
        await a.TickAsync();
        _now = _now.AddMinutes(31);
        _freePercent = 5;
        await a.TickAsync();
        _notes.Shown.Should().HaveCount(2);
        new JobRunsRepository(s.Database).Recent(s.Watches.All().Single().Id, 10).Should().HaveCount(2); // quiet checks aren't logged
    }

    [AvaloniaFact]
    public async Task Sign_in_runs_respect_pause()
    {
        var s = AppServices.Create(":memory:", new CleanupServiceTests.FakeBin());
        var a = Auto(s);
        s.Watches.Add(W(_root, WatchMode.NotifyOnly, null, WatchSchedule.AtSignIn()));
        a.PauseFor(TimeSpan.FromHours(1));
        await a.RunAtSignInAsync();
        _notes.Shown.Should().BeEmpty();
    }

    [AvaloniaFact]
    public void Editor_refuses_automatic_cleaning_of_home_or_relative_paths()
    {
        var s = AppServices.Create(":memory:", new CleanupServiceTests.FakeBin());
        var vm = new MainWindowViewModel(s, _notes);
        var e = vm.Automations.Editor;
        e.Action = "Clean Safe items automatically";
        e.Path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        e.SaveCommand.Execute(null);
        s.Watches.All().Should().BeEmpty();
        vm.Automations.Message.Should().Contain("narrower");
        e.Path = "relative/folder";
        e.Action = "Suggest what to clean";
        e.SaveCommand.Execute(null);
        s.Watches.All().Should().BeEmpty();
        vm.Automations.Message.Should().Contain("full path");
    }

    [Fact]
    public void Temp_template_is_windows_only()
    {
        var names = WatchTemplates.All("/home/u", "/tmp").Select(t => t.Name);
        if (OperatingSystem.IsWindows()) names.Should().Contain("Temp & caches");
        else names.Should().NotContain("Temp & caches");
    }

    [Fact]
    public void Startup_command_handles_the_dotnet_host()
    {
        StartupCommand.For("/usr/share/dotnet/dotnet", "/opt/miu/Miu.dll").Should().Be(("/usr/share/dotnet/dotnet", "\"/opt/miu/Miu.dll\" --background"));
        StartupCommand.For(@"C:\Apps\Miu\Miu.exe", @"C:\Apps\Miu\Miu.dll").Should().Be((@"C:\Apps\Miu\Miu.exe", "--background"));
    }

    [Fact]
    public void Drive_for_path_picks_the_deepest_mount()
    {
        DriveLookup.RootFor("/home/u/Downloads", ["/", "/home", "/media/usb"]).Should().Be("/home");
        DriveLookup.RootFor("/homework/x", ["/", "/home"]).Should().Be("/");
        DriveLookup.RootFor(@"D:\Games\x", [@"C:\", @"D:\"]).Should().Be(@"D:\");
    }

    [Fact]
    public void Single_instance_lock_is_exclusive()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "miu-lock-" + Guid.NewGuid().ToString("N"));
        using var first = SingleInstance.TryAcquire(dir);
        first.Should().NotBeNull();
        SingleInstance.TryAcquire(dir).Should().BeNull();
        first!.Dispose();
        using var again = SingleInstance.TryAcquire(dir);
        again.Should().NotBeNull();
    }
}
