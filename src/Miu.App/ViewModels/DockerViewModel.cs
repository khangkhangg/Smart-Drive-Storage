using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miu.Core.Formatting;
using Miu.Data;
using Miu.Dev.Docker;

namespace Miu.App.ViewModels;

public sealed record DockerRow(string Type, string CountText, string SizeText, string ReclaimableText);

/// <summary>
/// Docker's disk usage and three cleanups. Docker deletes permanently (there is no Recycle Bin), so every cleanup
/// asks first, says exactly what goes, and is written to Activity. Volumes are never touched.
/// </summary>
public partial class DockerViewModel(DockerService docker, ActionsRepository actions) : ObservableObject
{
    private Func<Task<ProcessResult>>? _pending;
    private IReadOnlyList<string> _pendingItems = [];
    private string _pendingDone = "";

    public ObservableCollection<DockerRow> Rows { get; } = [];

    [ObservableProperty] private bool isAvailable;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PruneBuildCacheCommand), nameof(PruneImagesCommand), nameof(PruneContainersCommand), nameof(ConfirmPendingCommand))]
    private bool isBusy;
    [ObservableProperty] private string statusText = "Checking Docker…";
    [ObservableProperty] private string reclaimableText = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPending))] private string pendingText = "";

    public bool HasPending => PendingText.Length > 0;

    private bool CanPrune() => IsAvailable && !IsBusy;

    [RelayCommand]
    public async Task Refresh()
    {
        IsBusy = true;
        try
        {
            var usage = await docker.GetUsageAsync();
            Rows.Clear();
            IsAvailable = usage is not null;
            if (usage is null)
            {
                StatusText = "Docker isn't installed or isn't running. Start Docker Desktop to see what it can free.";
                ReclaimableText = "";
                return;
            }
            foreach (var u in usage)
                Rows.Add(new DockerRow(u.Type, $"{u.TotalCount} ({u.Active} in use)", SizeFormatter.Format(u.SizeBytes), SizeFormatter.Format(u.ReclaimableBytes)));
            ReclaimableText = SizeFormatter.Format(usage.Where(u => u.Type != "Local Volumes").Sum(u => u.ReclaimableBytes));
            StatusText = "Docker removes things permanently (no Recycle Bin). Volumes can hold databases, so Miu never removes them.";
        }
        finally
        {
            IsBusy = false;
            PruneBuildCacheCommand.NotifyCanExecuteChanged();
            PruneImagesCommand.NotifyCanExecuteChanged();
            PruneContainersCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanPrune))]
    private Task PruneBuildCache()
    {
        Ask("Clear Docker's build cache? Your next builds will be slower once while it refills.", ["build cache"],
            () => docker.PruneBuildCacheAsync(), "Build cache cleared.");
        return Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanPrune))]
    private Task PruneImages()
    {
        Ask("Remove unused image layers (untagged images left behind by rebuilds)? Tagged images stay.", ["dangling images"],
            () => docker.PruneDanglingImagesAsync(), "Unused image layers removed.");
        return Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanPrune))]
    private async Task PruneContainers()
    {
        var names = await docker.ListStoppedContainersAsync();
        if (names.Count == 0) { StatusText = "There are no stopped containers."; return; }
        Ask($"Permanently remove {names.Count} stopped {(names.Count == 1 ? "container" : "containers")}: {string.Join(", ", names.Take(8))}" +
            (names.Count > 8 ? $" and {names.Count - 8} more" : "") + "? Anything saved inside them (not in a volume) is lost.",
            names, () => docker.PruneStoppedContainersAsync(), "Stopped containers removed.");
    }

    private bool CanConfirm() => _pending is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmPending()
    {
        var run = _pending;
        if (run is null) return;
        var items = _pendingItems;
        var done = _pendingDone;
        ClearPending();
        IsBusy = true;
        ProcessResult result;
        try { result = await run(); }
        finally { IsBusy = false; }
        if (result.ExitCode == 0)
            actions.Add("delete", items.Select(i => new ActionItem(i, "", 0)).ToList(), "docker");
        await Refresh();
        StatusText = result.ExitCode == 0 ? done : "Docker said: " + result.Error.Trim();
    }

    [RelayCommand]
    private void CancelPending() => ClearPending();

    private void Ask(string question, IReadOnlyList<string> items, Func<Task<ProcessResult>> run, string done)
    {
        _pending = run;
        _pendingItems = items;
        _pendingDone = done;
        PendingText = question;
        ConfirmPendingCommand.NotifyCanExecuteChanged();
    }

    private void ClearPending()
    {
        _pending = null;
        PendingText = "";
        ConfirmPendingCommand.NotifyCanExecuteChanged();
    }
}
