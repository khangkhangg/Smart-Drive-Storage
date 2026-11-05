using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Miu.App.ViewModels;

namespace Miu.App.Views;

public partial class AnalyzeView : UserControl
{
    public AnalyzeView() => InitializeComponent();

    private AnalyzeViewModel? Vm => DataContext as AnalyzeViewModel;

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var path = await FolderPicker.PickAsync(this);
        if (path is not null && Vm is { } vm) await vm.ScanPathAsync(path);
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is { } vm && vm.Selected is { } item) vm.OpenCommand.Execute(item);
    }

    private async void OnCopyPath(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Selected is { } item && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(item.FullPath);
    }
}
