using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Miu.App.Services;
using Miu.App.ViewModels;
using Miu.App.Views;
using Miu.Data;

namespace Miu.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    private static readonly TimeSpan MinimumSplashTime = TimeSpan.FromMilliseconds(1400);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var splash = new SplashWindow();
            desktop.MainWindow = splash;
            splash.Opened += async (_, _) => await StartAsync(desktop, splash);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Loads the database and rules off the UI thread while the splash is visible, then opens the main window.</summary>
    private static async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop, SplashWindow splash)
    {
        var minimum = Task.Delay(MinimumSplashTime);
        AppServices services;
        try
        {
            services = await Task.Run(() => AppServices.Create(MiuDatabase.DefaultPath));
        }
        catch (Exception ex)
        {
            splash.SetStatus("Could not start: " + ex.Message);
            return;
        }
        await minimum;
        var main = new MainWindow { DataContext = new MainWindowViewModel(services) };
        desktop.MainWindow = main;
        desktop.Exit += (_, _) => services.Dispose();
        main.Show();
        splash.Close();
    }
}
