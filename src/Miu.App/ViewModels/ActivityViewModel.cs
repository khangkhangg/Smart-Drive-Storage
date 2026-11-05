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
    private string Count(string one, string many) => $"{action.Items.Count} {(action.Items.Count == 1 ? one : many)}";
    public string Summary => action.Kind switch
    {
        "recycle" => $"Moved {Count("item", "items")} ({SizeFormatter.Format(action.Bytes)}) to the {shell.Services.RecycleBinName}",
        "sync" => $"Sync set aside {Count("replaced or removed file", "replaced or removed files")} ({SizeFormatter.Format(action.Bytes)}) in .miu-sync-trash",
        "removed" => $"Removed {Count("item", "items")} ({SizeFormatter.Format(action.Bytes)}) without a {shell.Services.RecycleBinName} copy",
        "failed" when action.Origin == "disk-image" => "Disk image didn't finish",
        "failed" => $"Couldn't clean {Count("item", "items")}",
        "backup" when action.Origin == "disk-image" => $"Disk image of {action.Items.FirstOrDefault()?.OriginalPath} ({SizeFormatter.Format(action.Bytes)})",
        "rule-added" => "New rule · " + action.Items.FirstOrDefault()?.OriginalPath,
        "rule-changed" => "Rule changed · " + action.Items.FirstOrDefault()?.OriginalPath,
        _ => $"Docker removed {string.Join(", ", action.Items.Take(1).Select(i => i.OriginalPath))}{(action.Items.Count > 1 ? $" and {action.Items.Count - 1} more" : "")} (can't be undone)",
    };
    public bool IsRule => action.Kind.StartsWith("rule", StringComparison.Ordinal);
    public string ItemsText => action.Kind switch
    {
        _ when IsRule => "",
        "backup" or "failed" when action.Origin == "disk-image" => action.Items.FirstOrDefault()?.TrashLocation ?? "",
        "removed" => "Windows removed these but Miu found no copy in the bin: they may be deleted for good. " + Names(),
        "failed" => string.Join("; ", action.Items.Take(3).Select(i => $"{Path.GetFileName(i.OriginalPath)}: {i.TrashLocation}"))
                    + (action.Items.Count > 3 ? $" and {action.Items.Count - 3} more" : ""),
        _ => Names(),
    };
    private string Names() => string.Join(", ", action.Items.Take(3).Select(i => Path.GetFileName(i.OriginalPath)))
                               + (action.Items.Count > 3 ? $" and {action.Items.Count - 3} more" : "");
    public string OriginText => action.Kind == "sync" ? (action.Origin == "auto" ? "Scheduled sync" : "Sync") : action.Origin switch
    {
        "quick-clean" => "Quick Clean", "dev-project" => "Dev projects", "docker" => "Docker", "telegram" => "Telegram", "auto" => "Automation",
        "backup" => "Back up, then free space", "teach" => "Teach Miu", "suggestion" => "Accepted suggestion", "import" => "Rules import", "rules" => "Rules page", "disk-image" => "Disk image",
        _ => "Cleanup cart",
    };
    public bool Undone => action.Undone;
    public bool CanUndo => action.Kind is "recycle" or "sync" or "rule-added" && !action.Undone;

    [RelayCommand]
    private async Task Undo() => await shell.UndoAsync(action.Id);
}

/// <summary>Everything Miu has cleaned, newest first, each with Undo.</summary>
public partial class ActivityViewModel : ObservableObject
{
    private readonly MainWindowViewModel shell;

    public ActivityViewModel(MainWindowViewModel shell)
    {
        this.shell = shell;
        // New entries show up straight away, even while you're looking at this page.
        shell.Services.Actions.Added += (_, _) =>
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Load();
            else Avalonia.Threading.Dispatcher.UIThread.Post(Load);
        };
    }

    public ObservableCollection<ActivityRow> Rows { get; } = [];

    [ObservableProperty] private bool isEmpty = true;

    public void Load()
    {
        Rows.Clear();
        foreach (var a in shell.Services.Actions.Recent(100)) Rows.Add(new ActivityRow(shell, a));
        IsEmpty = Rows.Count == 0;
    }
}
