using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miu.Core.Formatting;
using Miu.Data;

namespace Miu.App.ViewModels;

public partial class ActivityRow(MainWindowViewModel shell, ActionRecord action) : ObservableObject
{
    public long Id => action.Id;
    public string WhenText => HumanTime.Ago(action.TsUtc, DateTime.UtcNow);
    public string Summary => $"Moved {action.Items.Count} {(action.Items.Count == 1 ? "item" : "items")} ({SizeFormatter.Format(action.Bytes)}) to the {shell.Services.RecycleBinName}";
    public string ItemsText => string.Join(", ", action.Items.Take(3).Select(i => Path.GetFileName(i.OriginalPath)))
                               + (action.Items.Count > 3 ? $" and {action.Items.Count - 3} more" : "");
    public string OriginText => action.Origin switch { "quick-clean" => "Quick Clean", "dev-project" => "Dev projects", _ => "Cleanup cart" };
    public bool Undone => action.Undone;
    public bool CanUndo => !action.Undone;

    [RelayCommand]
    private async Task Undo() => await shell.UndoAsync(action.Id);
}

/// <summary>Everything Miu has cleaned, newest first, each with Undo.</summary>
public partial class ActivityViewModel(MainWindowViewModel shell) : ObservableObject
{
    public ObservableCollection<ActivityRow> Rows { get; } = [];

    [ObservableProperty] private bool isEmpty = true;

    public void Load()
    {
        Rows.Clear();
        foreach (var a in shell.Services.Actions.Recent(100)) Rows.Add(new ActivityRow(shell, a));
        IsEmpty = Rows.Count == 0;
    }
}
