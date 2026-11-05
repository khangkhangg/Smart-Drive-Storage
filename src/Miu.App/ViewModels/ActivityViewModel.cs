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
    public string Summary => action.Kind == "recycle"
        ? $"Moved {action.Items.Count} {(action.Items.Count == 1 ? "item" : "items")} ({SizeFormatter.Format(action.Bytes)}) to the {shell.Services.RecycleBinName}"
        : $"Docker removed {string.Join(", ", action.Items.Take(1).Select(i => i.OriginalPath))}{(action.Items.Count > 1 ? $" and {action.Items.Count - 1} more" : "")} (can't be undone)";
    public string ItemsText => string.Join(", ", action.Items.Take(3).Select(i => Path.GetFileName(i.OriginalPath)))
                               + (action.Items.Count > 3 ? $" and {action.Items.Count - 3} more" : "");
    public string OriginText => action.Origin switch { "quick-clean" => "Quick Clean", "dev-project" => "Dev projects", "docker" => "Docker", "telegram" => "Telegram", "auto" => "Automation", _ => "Cleanup cart" };
    public bool Undone => action.Undone;
    public bool CanUndo => action.Kind == "recycle" && !action.Undone;

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
