using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Miu.App.Services;

namespace Miu.App.Views;

/// <summary>A small notification in the bottom-right corner; closes itself after a while unless you hover it.</summary>
public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(15) };

    public ToastWindow() : this(new MiuNotification("Miu", "", [])) { }

    public ToastWindow(MiuNotification n)
    {
        InitializeComponent();
        TitleText.Text = n.Title;
        BodyText.Text = n.Body;
        ActionsList.ItemsSource = n.Actions.Select(a =>
        {
            var b = new Button { Content = a.Label, Classes = { "secondary" } };
            b.Click += async (_, _) => { Close(); await a.Run(); };
            return b;
        }).ToList();
        _timer.Tick += (_, _) => { if (!IsPointerOver) Close(); };
        Opened += (_, _) => { PlaceInCorner(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
    }

    private void PlaceInCorner()
    {
        if (Screens.Primary is not { } screen) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        Position = new PixelPoint(area.Right - (int)((Bounds.Width + 12) * scale), area.Bottom - (int)((Bounds.Height + 12) * scale));
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
