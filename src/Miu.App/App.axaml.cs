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
    private bool _exiting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Closing the window may only hide it (background mode), so the app quits explicitly.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var background = desktop.Args?.Contains("--background") == true;
            if (background)
                _ = StartAsync(desktop, splash: null, showWindow: false);
            else
            {
                var splash = new SplashWindow();
                desktop.MainWindow = splash;
                splash.Opened += async (_, _) => await StartAsync(desktop, splash, showWindow: true);
                splash.Show();
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Loads the database and rules off the UI thread (splash visible), then opens Miu and starts automations.</summary>
    private async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop, SplashWindow? splash, bool showWindow)
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
        _main = new MainWindow { DataContext = _vm };
        _main.Closing += (_, e) =>
        {
            if (_exiting || !services.Settings.GetBool(SettingKeys.RunInBackground, false)) { Quit(desktop); return; }
            e.Cancel = true; // keep running in the tray
            _main.Hide();
        };
        desktop.MainWindow = _main;
        desktop.Exit += (_, _) => services.Dispose();
        CreateTrayIcon(desktop);
        if (showWindow) _main.Show();
        splash?.Close();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += async (_, _) => await SafeTick();
        _timer.Start();
        if (!showWindow) await _vm.Automation.RunAtSignInAsync();
        await SafeTick();
    }

    private async Task SafeTick()
    {
        try { if (_vm is not null) await _vm.Automation.TickAsync(); }
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
        desktop.Shutdown();
    }
}
