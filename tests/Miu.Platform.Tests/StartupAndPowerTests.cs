using Miu.Platform.Portable;
using Miu.Platform.Windows;

namespace Miu.Platform.Tests;

public sealed class StartupAndPowerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "miu-autostart-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Linux_autostart_writes_and_removes_desktop_entry_with_quoted_path()
    {
        var a = new LinuxAutostart(_dir);
        a.IsEnabled.Should().BeFalse();
        a.Enable("/opt/My Apps/Miu", "--background");
        a.IsEnabled.Should().BeTrue();
        var text = File.ReadAllText(Path.Combine(_dir, "miu-smart-storage.desktop"));
        text.Should().Contain("[Desktop Entry]").And.Contain("Exec=\"/opt/My Apps/Miu\" --background").And.Contain("Name=Miu Smart Storage");
        a.Disable();
        a.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void Windows_run_key_command_is_quoted()
    {
        WindowsRunKey.CommandLine(@"C:\Program Files\Miu\Miu.exe", "--background")
            .Should().Be("\"C:\\Program Files\\Miu\\Miu.exe\" --background");
    }

    [Fact]
    public void Windows_run_key_roundtrip()
    {
        if (!OperatingSystem.IsWindows()) return;
        var key = new WindowsRunKey("MiuSmartStorageTest");
        key.Enable(@"C:\Program Files\Miu\Miu.exe", "--background");
        key.IsEnabled.Should().BeTrue();
        key.Disable();
        key.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void Linux_power_reads_ac_online()
    {
        var sys = Path.Combine(_dir, "power_supply");
        Directory.CreateDirectory(Path.Combine(sys, "AC"));
        File.WriteAllText(Path.Combine(sys, "AC", "type"), "Mains\n");
        File.WriteAllText(Path.Combine(sys, "AC", "online"), "0\n");
        new LinuxPowerState(sys).IsOnBattery.Should().BeTrue();
        File.WriteAllText(Path.Combine(sys, "AC", "online"), "1\n");
        new LinuxPowerState(sys).IsOnBattery.Should().BeFalse();
        new LinuxPowerState(Path.Combine(_dir, "none")).IsOnBattery.Should().BeFalse(); // desktops have no battery
    }
}
