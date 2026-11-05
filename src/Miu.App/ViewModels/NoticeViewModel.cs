using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Miu.App.ViewModels;

/// <summary>The banner shown after a clean or undo, with an Undo button while it is still possible.</summary>
public partial class NoticeViewModel(MainWindowViewModel shell, string message, string details, long actionId,
    string? actionLabel = null, Action? action = null) : ObservableObject
{
    public string Message { get; } = message;
    public string ActionLabel { get; } = actionLabel ?? "";
    public bool HasAction => action is not null;

    [RelayCommand]
    private void RunAction() => action?.Invoke();
    public string Details { get; } = details;
    public bool HasDetails => Details.Length > 0;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(UndoCommand))] private bool canUndo = actionId > 0;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task Undo()
    {
        CanUndo = false;
        await shell.UndoAsync(actionId);
    }

    [RelayCommand]
    private void Dismiss() => shell.Notice = null;
}
