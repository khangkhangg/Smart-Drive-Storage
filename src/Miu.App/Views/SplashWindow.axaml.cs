using Avalonia.Controls;
using Miu.App.Services;

namespace Miu.App.Views;

/// <summary>Shown while the database and rules load: logo, author and websites.</summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        VersionText.Text = $"v{AppInfo.Version}";
    }

    public void SetStatus(string text) => StatusText.Text = text;
}
