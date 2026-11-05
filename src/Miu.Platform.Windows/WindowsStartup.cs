using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Miu.Platform.Abstractions;

namespace Miu.Platform.Windows;

/// <summary>Start at sign-in through HKCU\Software\Microsoft\Windows\CurrentVersion\Run (no admin needed).</summary>
public sealed class WindowsRunKey(string valueName = "MiuSmartStorage") : IStartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string CommandLine(string exe, string args) => $"\"{exe}\" {args}".TrimEnd();

    public bool IsEnabled
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(valueName) is string;
        }
    }

    public void Enable(string executablePath, string arguments)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(valueName, CommandLine(executablePath, arguments));
    }

    public void Disable()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsPowerState : IPowerState
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    /// <summary>On battery when the AC line is offline and a battery is present (flag 128 = no battery).</summary>
    public bool IsOnBattery => GetSystemPowerStatus(out var s) && s.ACLineStatus == 0 && s.BatteryFlag != 128;
}
