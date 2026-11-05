using Miu.Platform.Abstractions;

namespace Miu.Platform.Portable;

/// <summary>XDG autostart: a .desktop file in ~/.config/autostart that desktops launch at sign-in.</summary>
public sealed class LinuxAutostart(string? autostartDir = null) : IStartupRegistration
{
    private readonly string _file = Path.Combine(autostartDir ?? DefaultDir(), "miu-smart-storage.desktop");

    public static string DefaultDir()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(config)) config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(config, "autostart");
    }

    public bool IsEnabled => File.Exists(_file);

    public void Enable(string executablePath, string arguments)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        // Desktop Entry spec: quote the program path; escape quotes, backslashes and $ inside it.
        var quoted = "\"" + executablePath.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`") + "\"";
        File.WriteAllText(_file, $"""
            [Desktop Entry]
            Type=Application
            Name=Miu Smart Storage
            Comment=Keeps your drives tidy in the background
            Exec={quoted} {arguments}
            Terminal=false
            X-GNOME-Autostart-enabled=true

            """);
    }

    public void Disable()
    {
        if (File.Exists(_file)) File.Delete(_file);
    }
}

/// <summary>Reads /sys/class/power_supply: on battery when there is mains power info and none of it is online.</summary>
public sealed class LinuxPowerState(string? sysDir = null) : IPowerState
{
    private readonly string _dir = sysDir ?? "/sys/class/power_supply";

    public bool IsOnBattery
    {
        get
        {
            try
            {
                if (!Directory.Exists(_dir)) return false;
                var mains = Directory.GetDirectories(_dir)
                    .Where(d => File.Exists(Path.Combine(d, "type")) && File.ReadAllText(Path.Combine(d, "type")).Trim() == "Mains")
                    .ToList();
                if (mains.Count == 0) return false;
                return !mains.Any(d => File.Exists(Path.Combine(d, "online")) && File.ReadAllText(Path.Combine(d, "online")).Trim() == "1");
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }
}
