using Avalonia.Headless.XUnit;
using Miu.App.Services;
using Miu.App.ViewModels;
using Miu.Core.Automation;

namespace Miu.App.Tests;

public sealed class SettingsMoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(AppContext.BaseDirectory, "miu-settings-" + Guid.NewGuid().ToString("N"));

    public SettingsMoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static MainWindowViewModel Shell(TelegramServiceTests.MemorySecrets secrets) =>
        new(AppServices.Create(":memory:", new CleanupServiceTests.FakeBin(), secrets: secrets), new AutomationTests.FakeNotifier(), _ => new TelegramServiceTests.FakeTelegram());

    [AvaloniaFact]
    public void Export_has_your_setup_and_never_a_secret()
    {
        var secrets = new TelegramServiceTests.MemorySecrets();
        var vm = Shell(secrets);
        secrets.Set("telegram.token", "123456:SECRET-TOKEN");
        var account = vm.Services.CloudAccounts.Add("s3", "B2 photos");
        secrets.Set("cloud." + account, "{\"SecretKey\":\"S3-SECRET-KEY\"}");
        vm.Services.Watches.Add(new Watch(0, "Downloads tidy", "/home/x/Downloads", WatchMode.Suggest, WatchSchedule.Daily(new TimeOnly(9, 0)), new WatchFilters()));
        vm.Services.Settings.Set("telegram.token", "SHOULD-NOT-BE-HERE-ANYWAY");

        vm.Settings.ExportFolder = _dir;
        vm.Settings.ExportCommand.Execute(null);
        vm.Settings.ExportStatus.Should().StartWith("Saved to");
        var text = File.ReadAllText(Directory.GetFiles(_dir, "Miu-export-*.json").Single());
        text.Should().Contain("Downloads tidy").And.Contain("B2 photos");
        text.Should().NotContain("SECRET-TOKEN").And.NotContain("S3-SECRET-KEY").And.NotContain("SHOULD-NOT-BE-HERE");
    }

    [AvaloniaFact]
    public void Theme_is_saved()
    {
        var vm = Shell(new TelegramServiceTests.MemorySecrets());
        vm.Settings.Theme = "Dark";
        vm.Services.Settings.Get(Miu.Data.SettingKeys.Theme).Should().Be("Dark");
        vm.Settings.Theme = "System";
    }

    [Fact]
    public void Startup_arguments_find_the_folder_to_analyze()
    {
        StartupArgs.AnalyzePath(["--analyze", _dir]).Should().Be(Path.GetFullPath(_dir));
        StartupArgs.AnalyzePath(["--background"]).Should().BeNull();
        StartupArgs.AnalyzePath([_dir]).Should().Be(Path.GetFullPath(_dir)); // "Open with Miu"
        StartupArgs.AnalyzePath(["--analyze", Path.Combine(_dir, "missing")]).Should().BeNull();
        StartupArgs.AnalyzePath(null).Should().BeNull();
    }

    [Fact]
    public void Analyze_signal_hands_a_folder_to_the_running_miu()
    {
        SingleInstance.SignalAnalyze(_dir, "/some/folder");
        SingleInstance.ReadAnalyzeSignal(_dir).Should().Be("/some/folder");
        SingleInstance.ReadAnalyzeSignal(_dir).Should().BeNull(); // read once
    }

    [Fact]
    public void Windows_explorer_menu_roundtrip()
    {
        if (!OperatingSystem.IsWindows()) return;
#pragma warning disable CA1416
        var wasRegistered = Miu.Platform.Windows.WindowsExplorerMenu.IsRegistered;
        Miu.Platform.Windows.WindowsExplorerMenu.Register(@"C:\Program Files\Miu Test\Miu.exe");
        Miu.Platform.Windows.WindowsExplorerMenu.IsRegistered.Should().BeTrue();
        Miu.Platform.Windows.WindowsExplorerMenu.Command(@"C:\Program Files\Miu Test\Miu.exe", "%1").Should().Be("\"C:\\Program Files\\Miu Test\\Miu.exe\" --analyze \"%1\"");
        if (!wasRegistered) Miu.Platform.Windows.WindowsExplorerMenu.Unregister();
#pragma warning restore CA1416
    }
}
