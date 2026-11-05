using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miu.Core.Cleanup;
using Miu.Core.Formatting;

namespace Miu.App.ViewModels;

/// <summary>The cleanup cart bar docked at the bottom of the window.</summary>
public partial class CartViewModel : ObservableObject
{
    private readonly MainWindowViewModel _shell;
    private readonly CleanupCart _cart;

    public CartViewModel(MainWindowViewModel shell, CleanupCart cart)
    {
        _shell = shell;
        _cart = cart;
        cart.Changed += (_, _) => Refresh();
    }

    public int Count => _cart.Count;
    public bool HasItems => _cart.Count > 0;
    public string CountText => $"{Count} {(Count == 1 ? "item" : "items")}";
    public string TotalText => SizeFormatter.Format(_cart.TotalBytes);
    public string SummaryText => string.Join(", ", _cart.Items.Take(4).Select(i => i.Name)) + (Count > 4 ? $" and {Count - 4} more" : "");

    [RelayCommand]
    private void Clear() => _cart.Clear();

    [RelayCommand]
    private void Review() => _shell.OpenReview(_cart.Items.ToList(), "Review & Clean", "cart");

    private void Refresh()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(SummaryText));
    }
}
