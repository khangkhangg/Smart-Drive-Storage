using Avalonia.Controls;
using Avalonia.Input;
using Miu.App.ViewModels;

namespace Miu.App.Views;

public partial class DevProjectsView : UserControl
{
    public DevProjectsView() => InitializeComponent();

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: ProjectRow row } && DataContext is DevProjectsViewModel vm)
            vm.OpenCommand.Execute(row);
    }
}
