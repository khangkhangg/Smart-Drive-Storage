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

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = AppServices.Create(MiuDatabase.DefaultPath);
            desktop.MainWindow = new MainWindow { DataContext = new MainWindowViewModel(services) };
            desktop.Exit += (_, _) => services.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
