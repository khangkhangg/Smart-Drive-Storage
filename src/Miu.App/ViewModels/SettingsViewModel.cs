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
        theme = _s.Get(SettingKeys.Theme) is { } t && Themes.Contains(t) ? t : "System";
        autoUpdateCheck = _s.GetBool(SettingKeys.AutoUpdateCheck, true);
        explorerMenu = OperatingSystem.IsWindows() && Miu.Platform.Windows.WindowsExplorerMenu.IsRegistered;
        exportFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) is { Length: > 0 } docs ? docs : shell.Home.HomeFolder;
        _loading = false;
        Telegram = new TelegramSettingsViewModel(shell);
    }

    public TelegramSettingsViewModel Telegram { get; }

    public IReadOnlyList<int> Hours { get; } = Enumerable.Range(0, 24).ToList();
    public string Version => $"Miu Smart Storage v{Services.AppInfo.Version}";

    [ObservableProperty] private bool runInBackground;
    [ObservableProperty] private bool allowAutoClean;
    [ObservableProperty] private bool pauseOnBattery;
    [ObservableProperty] private bool quietHoursEnabled;
    [ObservableProperty] private int quietStart;
    [ObservableProperty] private int quietEnd;
    [ObservableProperty] private string pauseText = "";

    // ---------- Appearance, updates, data, Explorer ----------
    public IReadOnlyList<string> Themes { get; } = ["System", "Light", "Dark"];
    [ObservableProperty] private string theme = "System";
    [ObservableProperty] private bool autoUpdateCheck = true;
    [ObservableProperty] private string updateStatus = "";
    [ObservableProperty] private bool isCheckingUpdates;
    [ObservableProperty] private string exportFolder = "";
    [ObservableProperty] private string exportStatus = "";
    [ObservableProperty] private bool explorerMenu;
    [ObservableProperty] private string explorerStatus = "";
    public bool IsWindows => OperatingSystem.IsWindows();
    public string DataFolder => System.IO.Path.GetDirectoryName(MiuDatabase.DefaultPath)!;

    partial void OnThemeChanged(string value)
    {
        if (_loading) return;
        _s.Set(SettingKeys.Theme, value);
        ApplyTheme(value);
    }

    /// <summary>Applies "System", "Light" or "Dark" to the whole app.</summary>
    public static void ApplyTheme(string? value)
    {
        if (Avalonia.Application.Current is not { } app) return;
        app.RequestedThemeVariant = value switch
        {
            "Light" => Avalonia.Styling.ThemeVariant.Light,
            "Dark" => Avalonia.Styling.ThemeVariant.Dark,
            _ => Avalonia.Styling.ThemeVariant.Default,
        };
    }

    partial void OnAutoUpdateCheckChanged(bool value)
    {
        if (!_loading) _s.SetBool(SettingKeys.AutoUpdateCheck, value);
    }

    [RelayCommand]
    private async Task CheckUpdates()
    {
        IsCheckingUpdates = true;
        UpdateStatus = "Checking…";
        try { UpdateStatus = await _shell.CheckForUpdatesAsync(force: true); }
        finally { IsCheckingUpdates = false; }
    }

    [RelayCommand]
    private void Export()
    {
        try { ExportStatus = "Saved to " + Services.ExportService.Export(_shell.Services, ExportFolder.Trim(), DateTime.Now) + ". It has no passwords, tokens or keys."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { ExportStatus = "Couldn't save: " + ex.Message; }
    }

    [RelayCommand]
    private void OpenDataFolder() => Services.ShellIntegration.Open(DataFolder);

    partial void OnExplorerMenuChanged(bool value)
    {
        if (_loading || !OperatingSystem.IsWindows()) return;
        try
        {
            if (value) Miu.Platform.Windows.WindowsExplorerMenu.Register(Environment.ProcessPath ?? "Miu.exe");
            else Miu.Platform.Windows.WindowsExplorerMenu.Unregister();
            ExplorerStatus = value ? "Right-click any folder or drive in Explorer and choose “Analyze with Miu”." : "Removed from Explorer.";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            ExplorerStatus = "Couldn't change the Explorer menu: " + ex.Message;
        }
    }

    partial void OnRunInBackgroundChanged(bool value)
    {
        if (_loading) return;
        try
        {
            if (value)
            {
                // Inside an AppImage the process lives in a temporary mount: start the AppImage file itself instead.
                var process = Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage ? appImage : Environment.ProcessPath ?? "Miu";
                var (exe, args) = Services.StartupCommand.For(process, System.Reflection.Assembly.GetEntryAssembly()?.Location);
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
