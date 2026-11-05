using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miu.Data;

namespace Miu.App.ViewModels;

/// <summary>Background mode, automatic cleaning, quiet hours and battery.</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly MainWindowViewModel _shell;
    private readonly SettingsRepository _s;
    private bool _loading;

    public SettingsViewModel(MainWindowViewModel shell)
    {
        _shell = shell;
        _s = shell.Services.Settings;
        _loading = true;
        runInBackground = _s.GetBool(SettingKeys.RunInBackground, false);
        allowAutoClean = _s.GetBool(SettingKeys.AutoCleanAllowed, false);
        pauseOnBattery = _s.GetBool(SettingKeys.PauseOnBattery, true);
        quietHoursEnabled = int.TryParse(_s.Get(SettingKeys.QuietStartHour), out var qs) && int.TryParse(_s.Get(SettingKeys.QuietEndHour), out _);
        quietStart = quietHoursEnabled ? qs : 22;
        quietEnd = int.TryParse(_s.Get(SettingKeys.QuietEndHour), out var qe) ? qe : 7;
        _loading = false;
    }

    public IReadOnlyList<int> Hours { get; } = Enumerable.Range(0, 24).ToList();
    public string Version => $"Miu Smart Storage v{Services.AppInfo.Version}";

    [ObservableProperty] private bool runInBackground;
    [ObservableProperty] private bool allowAutoClean;
    [ObservableProperty] private bool pauseOnBattery;
    [ObservableProperty] private bool quietHoursEnabled;
    [ObservableProperty] private int quietStart;
    [ObservableProperty] private int quietEnd;
    [ObservableProperty] private string pauseText = "";

    partial void OnRunInBackgroundChanged(bool value)
    {
        if (_loading) return;
        try
        {
            if (value)
            {
                var (exe, args) = Services.StartupCommand.For(Environment.ProcessPath ?? "Miu", System.Reflection.Assembly.GetEntryAssembly()?.Location);
                _shell.Services.Startup.Enable(exe, args);
            }
            else _shell.Services.Startup.Disable();
            _s.SetBool(SettingKeys.RunInBackground, value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            PauseText = "Couldn't change start-at-sign-in: " + ex.Message;
            _loading = true;
            RunInBackground = !value;
            _loading = false;
        }
    }

    partial void OnAllowAutoCleanChanged(bool value)
    {
        if (_loading) return;
        _s.SetBool(SettingKeys.AutoCleanAllowed, value);
        _shell.Automations.Editor.RefreshNote();
    }

    partial void OnPauseOnBatteryChanged(bool value)
    {
        if (!_loading) _s.SetBool(SettingKeys.PauseOnBattery, value);
    }

    partial void OnQuietHoursEnabledChanged(bool value) => SaveQuiet();
    partial void OnQuietStartChanged(int value) => SaveQuiet();
    partial void OnQuietEndChanged(int value) => SaveQuiet();

    private void SaveQuiet()
    {
        if (_loading) return;
        _s.Set(SettingKeys.QuietStartHour, QuietHoursEnabled ? QuietStart.ToString() : "");
        _s.Set(SettingKeys.QuietEndHour, QuietHoursEnabled ? QuietEnd.ToString() : "");
    }

    [RelayCommand]
    private void PauseOneHour()
    {
        _shell.Automation.PauseFor(TimeSpan.FromHours(1));
        PauseText = "Automations are paused for an hour.";
    }

    [RelayCommand]
    private void ResumeAutomations()
    {
        _shell.Automation.Resume();
        PauseText = "Automations are running.";
    }
}
