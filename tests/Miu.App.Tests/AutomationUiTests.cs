using Avalonia.Headless.XUnit;
using Miu.App.Services;
using Miu.App.ViewModels;
using Miu.Core.Automation;
using Miu.Data;
using Miu.Platform.Abstractions;

namespace Miu.App.Tests;

public sealed class AutomationUiTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "miu-aui-" + Guid.NewGuid().ToString("N"));
    private readonly AutomationTests.FakeNotifier _notes = new();
    private readonly FakeStartup _startup = new();

    public AutomationUiTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Downloads"));
        var p = Path.Combine(_root, "Downloads", "old.dmp");
        File.WriteAllBytes(p, new byte[5000]);
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-40));
    }

    public void Dispose() => Directory.Delete(_root, true);

    private MainWindowViewModel Shell()
    {
        var s = AppServices.Create(":memory:", new CleanupServiceTests.FakeBin(), _startup);
        return new MainWindowViewModel(s, _notes);
    }

    [AvaloniaFact]
    public void Template_creates_a_scheduled_watch()
    {
        var vm = Shell();
        vm.Navigate(vm.Automations);
        var t = vm.Automations.Templates.Single(x => x.Name == "Downloads tidy");
        vm.Automations.UseTemplateCommand.Execute(t);
        var row = vm.Automations.Rows.Single();
        row.Sentence.Should().Contain("every Sunday at 10:00").And.Contain("suggest").And.Contain("older than 30 days");
        row.NextRunText.Should().StartWith("Next run");
        vm.Services.Watches.All().Single().NextRunLocal.Should().NotBeNull();
    }

    [AvaloniaFact]
    public void Sentence_editor_saves_a_watch()
    {
        var vm = Shell();
        var e = vm.Automations.Editor;
        e.Name = "My downloads";
        e.Path = Path.Combine(_root, "Downloads");
        e.Frequency = "Every day";
        e.Hour = 8;
        e.Action = "Suggest what to clean";
        e.OlderThanDays = 14;
        e.Preview.Should().Be($"Every day at 08:00, in {Path.Combine(_root, "Downloads")}, suggest what to clean older than 14 days.");
        e.SaveCommand.Execute(null);
        var w = vm.Services.Watches.All().Single();
        w.Schedule.Kind.Should().Be(ScheduleKind.Daily);
        w.Filters.OlderThanDays.Should().Be(14);
        w.Mode.Should().Be(WatchMode.Suggest);
    }

    [AvaloniaFact]
    public void Auto_clean_choice_explains_the_setting()
    {
        var vm = Shell();
        vm.Automations.Editor.Action = "Clean Safe items automatically";
        vm.Automations.Editor.AutoCleanNote.Should().Contain("Settings");
        vm.Settings.AllowAutoClean = true;
        vm.Automations.Editor.AutoCleanNote.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task Run_now_and_toggle_and_delete()
    {
        var vm = Shell();
        vm.Automations.Editor.Path = Path.Combine(_root, "Downloads");
        vm.Automations.Editor.SaveCommand.Execute(null);
        var row = vm.Automations.Rows.Single();
        await row.RunNowCommand.ExecuteAsync(null);
        _notes.Shown.Should().ContainSingle();
        vm.Automations.Rows.Single().LastResultText.Should().Contain("1 item");
        vm.Automations.Rows.Single().Enabled = false;
        vm.Services.Watches.All().Single().Enabled.Should().BeFalse();
        vm.Automations.Rows.Single().DeleteCommand.Execute(null);
        vm.Services.Watches.All().Should().BeEmpty();
    }

    [AvaloniaFact]
    public void Settings_persist_and_register_startup()
    {
        var vm = Shell();
        vm.Navigate(vm.Settings);
        vm.Settings.RunInBackground = true;
        _startup.Enabled.Should().BeTrue();
        _startup.Args.Should().Be("--background");
        vm.Settings.RunInBackground = false;
        _startup.Enabled.Should().BeFalse();
        vm.Settings.AllowAutoClean = true;
        vm.Services.Settings.GetBool(SettingKeys.AutoCleanAllowed, false).Should().BeTrue();
        vm.Settings.QuietHoursEnabled = true;
        vm.Settings.QuietStart = 22;
        vm.Settings.QuietEnd = 7;
        vm.Services.Settings.Get(SettingKeys.QuietStartHour).Should().Be("22");
        vm.Settings.PauseOnBattery = false;
        vm.Services.Settings.GetBool(SettingKeys.PauseOnBattery, true).Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task Home_shows_what_grew_since_an_older_scan()
    {
        var vm = Shell();
        var downloads = Path.Combine(_root, "Downloads");
        vm.Services.Snapshots.Save(_root, DateTime.UtcNow.AddDays(-7), new Dictionary<string, long> { [downloads] = 1000 });
        await vm.Analyze.ScanPathAsync(_root);
        var g = vm.Home.Grew.Single();
        g.Path.Should().Be(downloads);
        g.DeltaText.Should().Be("+3.9 KB");
        vm.Home.HasGrew.Should().BeTrue();
    }

    [AvaloniaFact]
    public void Drive_card_shows_forecast_when_trend_is_known()
    {
        var vm = Shell();
        var card = vm.Home.Drives.FirstOrDefault();
        if (card is null) return; // no fixed drive in this environment
        var total = 100L << 30;
        for (var d = 0; d < 4; d++)
            vm.Services.SpaceSamples.Add(card.Root, DateTime.UtcNow.AddDays(-6 + d * 2), (90L << 30) + d * (1L << 30), total);
        vm.Home.RefreshForecasts();
        vm.Home.Drives.First().ForecastText.Should().StartWith("Full in ~");
    }

    private sealed class FakeStartup : IStartupRegistration
    {
        public bool Enabled { get; private set; }
        public string? Args { get; private set; }
        public bool IsEnabled => Enabled;
        public void Enable(string executablePath, string arguments) { Enabled = true; Args = arguments; }
        public void Disable() => Enabled = false;
    }
}
