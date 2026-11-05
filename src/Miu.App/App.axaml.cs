using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Miu.App.Services;
using Miu.App.ViewModels;
using Miu.App.Views;
using Miu.Data;

namespace Miu.App;

public partial class App : Application
{
    private static readonly TimeSpan MinimumSplashTime = TimeSpan.FromMilliseconds(1400);
    private MainWindowViewModel? _vm;
    private MainWindow? _main;
    private DispatcherTimer? _timer;
    private readonly CancellationTokenSource _stopping = new();
    private bool _exiting;
    private IDisposable? _instanceLock;
    private FileSystemWatcher? _signalWatcher;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Closing the window may only hide it (background mode), so the app quits explicitly.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            CrashReporter.Install(DataDir, ex => _vm?.ShowProblem(ex));
            var background = desktop.Args?.Contains("--background") == true;
            // One Miu at a time: a second start asks the running one to show its window, then quits.
            _instanceLock = SingleInstance.TryAcquire(DataDir);
            if (_instanceLock is null)
            {
                if (!background) SingleInstance.SignalShow(DataDir);
                desktop.Shutdown();
                base.OnFrameworkInitializationCompleted();
                return;
            }
            WatchForShowSignal();
            if (background)
                _ = StartAsync(desktop, splash: null, showWindow: false);
            else
            {
                var splash = new SplashWindow();
                desktop.MainWindow = splash;
                splash.Opened += async (_, _) => await StartAsync(desktop, splash, showWindow: true);
                splash.Closed += (_, _) => { if (_main is null) desktop.Shutdown(1); }; // closed after a failed start
                splash.Show();
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Loads the database and rules off the UI thread (splash visible), then opens Miu and starts automations.</summary>
    private async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop, SplashWindow? splash, bool showWindow)
    {
        try { await StartCoreAsync(desktop, splash, showWindow); }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine("Miu failed to start: " + ex);
            if (splash is not null && _main is null) splash.SetStatus("Could not start: " + ex.Message);
            else Quit(desktop);
        }
    }

    private async Task StartCoreAsync(IClassicDesktopStyleApplicationLifetime desktop, SplashWindow? splash, bool showWindow)
    {
        var minimum = Task.Delay(splash is null ? TimeSpan.Zero : MinimumSplashTime);
        AppServices services;
        try
        {
            services = await Task.Run(() => AppServices.Create(MiuDatabase.DefaultPath));
        }
        catch (Exception ex)
        {
            splash?.SetStatus("Could not start: " + ex.Message);
            if (splash is null) desktop.Shutdown(1);
            return;
        }
        await minimum;
        _vm = new MainWindowViewModel(services);
        if (services.RecoveredFrom is { } from)
            _vm.Notice = new NoticeViewModel(_vm, "Miu's database was damaged, so it was restored from its most recent backup.",
                $"Backup used: {from}. The damaged file was kept next to it.", 0);
        else if (CrashReporter.Pending(DataDir) is { } report)
        {
            CrashReporter.MarkSeen(report);
            _vm.Notice = new NoticeViewModel(_vm, "Miu closed unexpectedly last time. Sorry about that.",
                "A report was saved on this computer (nothing was sent). You can share it to help fix the problem.", 0, "Open report", () => ShellIntegration.Open(report));
        }
        try { services.BackupDatabaseIfDue(); } catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException) { }
        _main = new MainWindow { DataContext = _vm };
        _vm.ShowWindow = ShowMain;
        _main.Closing += (_, e) =>
        {
            var systemShutdown = e.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown;
            if (_exiting || systemShutdown || !services.Settings.GetBool(SettingKeys.RunInBackground, false)) { Quit(desktop); return; }
            e.Cancel = true; // keep running in the background
            HideMain();
        };
        desktop.MainWindow = _main;
        desktop.Exit += (_, _) => services.Dispose();
        CreateTrayIcon(desktop);
        if (showWindow) _main.Show();
        else if (!TrayIsReliable) { _main.Show(); _main.WindowState = WindowState.Minimized; }
        splash?.Close();

        _ = RunTelegramAsync(_vm.Telegram);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += async (_, _) => await SafeTick();
        _timer.Start();
        await ReconnectSharesAsync(); // before sign-in syncs, which may use them
        _ = _vm.CheckForUpdatesAsync(force: false);
        if (!showWindow) await _vm.Automation.RunAtSignInAsync();
        await SafeTick();
    }

    /// <summary>Connects saved network shares marked "reconnect" that Windows didn't connect itself.</summary>
    private async Task ReconnectSharesAsync()
    {
        if (_vm is null) return;
        try
        {
            if (await _vm.CloudNetwork.Network.ReconnectSavedAsync(_stopping.Token) > 0) _vm.CloudNetwork.RefreshDrives();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { System.Diagnostics.Trace.WriteLine("Reconnecting shares failed: " + ex.Message); }
    }

    /// <summary>Listens for Telegram commands and button taps (handled on the UI thread, like everything else).</summary>
    private async Task RunTelegramAsync(TelegramService telegram)
    {
        try { await telegram.RunAsync(_stopping.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Telegram loop stopped: " + ex); }
    }

    private async Task SafeTick()
    {
        try
        {
            if (_vm is null) return;
            await _vm.Automation.TickAsync();
            await _vm.CheckForUpdatesAsync(force: false); // at most once a day
            try { _vm.Services.BackupDatabaseIfDue(); } catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException) { }
        }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Automation tick failed: " + ex); }
    }

    private void CreateTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var open = new NativeMenuItem("Open Miu");
        open.Click += (_, _) => ShowMain();
        var quick = new NativeMenuItem("Quick Clean…");
        quick.Click += (_, _) =>
        {
            ShowMain();
            if (_vm is null) return;
            _vm.Navigate(_vm.Home);
            if (_vm.Home.QuickCleanCommand.CanExecute(null)) _vm.Home.QuickCleanCommand.Execute(null);
        };
        var pauseHour = new NativeMenuItem("Pause automations for 1 hour");
        pauseHour.Click += (_, _) => _vm?.Automation.PauseFor(TimeSpan.FromHours(1));
        var pauseDay = new NativeMenuItem("Pause until tomorrow");
        pauseDay.Click += (_, _) => _vm?.Automation.PauseFor(DateTime.Today.AddDays(1).AddHours(8) - DateTime.Now);
        var resume = new NativeMenuItem("Resume automations");
        resume.Click += (_, _) => _vm?.Automation.Resume();
        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) => Quit(desktop);

        var menu = new NativeMenu();
        foreach (var item in new NativeMenuItemBase[] { open, quick, new NativeMenuItemSeparator(), pauseHour, pauseDay, resume, new NativeMenuItemSeparator(), exit })
            menu.Add(item);

        using var stream = AssetLoader.Open(new Uri("avares://Miu/Assets/miu-256.png"));
        var tray = new TrayIcon { Icon = new WindowIcon(stream), ToolTipText = "Miu Smart Storage", Menu = menu };
        tray.Clicked += (_, _) => ShowMain();
        TrayIcon.SetIcons(this, [tray]);
    }

    /// <summary>
    /// Some Linux desktops (GNOME without the AppIndicator extension) show no tray icon. There Miu minimizes
    /// instead of hiding, so you can always get back to it.
    /// </summary>
    private static bool TrayIsReliable => !OperatingSystem.IsLinux();

    private void HideMain()
    {
        if (_main is null) return;
        if (TrayIsReliable) _main.Hide();
        else _main.WindowState = WindowState.Minimized;
    }

    private static string DataDir => Path.GetDirectoryName(MiuDatabase.DefaultPath)!;

    private void WatchForShowSignal()
    {
        try
        {
            _signalWatcher = new FileSystemWatcher(DataDir, SingleInstance.ShowSignal) { EnableRaisingEvents = true };
            _signalWatcher.Changed += (_, _) => Dispatcher.UIThread.Post(ShowMain);
            _signalWatcher.Created += (_, _) => Dispatcher.UIThread.Post(ShowMain);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException) { }
    }

    private void ShowMain()
    {
        if (_main is null) return;
        _main.Show();
        _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    private void Quit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _exiting = true;
        _timer?.Stop();
        _stopping.Cancel();
        _signalWatcher?.Dispose();
        _instanceLock?.Dispose();
        desktop.Shutdown();
    }
}
