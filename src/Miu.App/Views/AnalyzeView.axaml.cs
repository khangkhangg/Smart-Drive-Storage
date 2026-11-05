using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Miu.App.ViewModels;

namespace Miu.App.Views;

public partial class AnalyzeView : UserControl
{
    public AnalyzeView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (Vm is { } vm) vm.ShowPreview = content =>
            {
                var window = new PreviewWindow(content);
                if (TopLevel.GetTopLevel(this) is Window owner) window.Show(owner);
                else window.Show();
            };
        };
    }

    private void OnListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox list && Vm is { } vm && list.IsVisible)
            vm.SetSelection(list.SelectedItems?.OfType<NodeItemViewModel>() ?? []);
    }

    /// <summary>Space adds the selection to the cart; Enter opens.</summary>
    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.Key == Key.Space) { vm.AddSelectedToCartCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Enter && vm.Selected is { } item) { vm.OpenItemCommand.Execute(item); e.Handled = true; }
    }

    private async void OnCopyCommand(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string text } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

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
        if (Vm is { } vm && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            // Several selected: one path per line.
            var paths = vm.SelectedItems.Count > 1 ? vm.SelectedItems.Select(i => i.FullPath) : vm.Selected is { } s ? [s.FullPath] : [];
            var text = string.Join(Environment.NewLine, paths);
            if (text.Length > 0) await clipboard.SetTextAsync(text);
        }
    }
}
