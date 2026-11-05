using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Miu.App.ViewModels;

namespace Miu.App.Views;

public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    private async void OnScanFolder(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is not MainWindowViewModel shell) return;
        var path = await FolderPicker.PickAsync(this);
        if (path is null) return;
        shell.Navigate(shell.Analyze);
        await shell.Analyze.ScanPathAsync(path);
    }
}

internal static class FolderPicker
{
    public static async Task<string?> PickAsync(Control anchor)
    {
        var storage = TopLevel.GetTopLevel(anchor)?.StorageProvider;
        if (storage is null || !storage.CanPickFolder) return null;
        var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a folder to scan", AllowMultiple = false });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }
}
