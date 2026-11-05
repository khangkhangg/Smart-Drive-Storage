using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miu.Core.Formatting;
using Miu.Dev.Docker;

namespace Miu.App.ViewModels;

public sealed record DockerRow(string Type, string CountText, string SizeText, string ReclaimableText);

/// <summary>Docker's disk usage and the three safe cleanups (volumes are left for the user to review).</summary>
public partial class DockerViewModel(DockerService docker) : ObservableObject
{
    public ObservableCollection<DockerRow> Rows { get; } = [];

    [ObservableProperty] private bool isAvailable;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(PruneBuildCacheCommand), nameof(PruneImagesCommand), nameof(PruneContainersCommand))]
    private bool isBusy;
    [ObservableProperty] private string statusText = "Checking Docker…";
    [ObservableProperty] private string reclaimableText = "";

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
            var safe = usage.Where(u => u.Type != "Local Volumes").Sum(u => u.ReclaimableBytes);
            ReclaimableText = SizeFormatter.Format(safe);
            StatusText = "Volumes can hold databases, so Miu never removes them for you. Review them in Docker Desktop.";
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
    private Task PruneBuildCache() => Run(docker.PruneBuildCacheAsync, "Build cache cleared.");

    [RelayCommand(CanExecute = nameof(CanPrune))]
    private Task PruneImages() => Run(docker.PruneDanglingImagesAsync, "Unused image layers removed.");

    [RelayCommand(CanExecute = nameof(CanPrune))]
    private Task PruneContainers() => Run(docker.PruneStoppedContainersAsync, "Stopped containers removed.");

    private async Task Run(Func<CancellationToken, Task<ProcessResult>> action, string done)
    {
        IsBusy = true;
        ProcessResult result;
        try { result = await action(CancellationToken.None); }
        finally { IsBusy = false; }
        await Refresh();
        StatusText = result.ExitCode == 0 ? done : "Docker said: " + result.Error.Trim();
    }
}
