using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miu.App.Services;
using Miu.Data;
using Miu.Dev.Docker;

namespace Miu.App.ViewModels;

/// <summary>The Docker page: every container, image and volume, with start/stop/pause/restart, logs and delete.</summary>
public sealed partial class DockerPageViewModel(MainWindowViewModel shell) : ObservableObject
{
    private DockerManager Docker => shell.Services.DockerManager;

    public ObservableCollection<ContainerRow> Containers { get; } = [];
    public ObservableCollection<ImageRow> Images { get; } = [];
    public ObservableCollection<VolumeRow> Volumes { get; } = [];
    public IReadOnlyList<string> Views { get; } = ["Containers", "Images", "Volumes"];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsContainers), nameof(IsImages), nameof(IsVolumes))] private string view = "Containers";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsUnavailable))] private bool isAvailable;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string message = "";
    [ObservableProperty] private bool messageIsError;
    [ObservableProperty] private string summary = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasLogs))] private string logsTitle = "";
    [ObservableProperty] private string logsText = "";

    public bool IsContainers => View == "Containers";
    public bool IsImages => View == "Images";
    public bool IsVolumes => View == "Volumes";
    public bool IsUnavailable => !IsAvailable && !IsBusy && _loaded;
    public bool HasLogs => LogsTitle.Length > 0;
    private bool _loaded;

    [RelayCommand]
    public async Task Refresh()
    {
        IsBusy = true;
        try
        {
            var inv = await Docker.ListAsync();
            _loaded = true;
            IsAvailable = inv is not null;
            Containers.Clear();
            Images.Clear();
            Volumes.Clear();
            if (inv is null)
            {
                Summary = "";
                Message = "Docker isn't installed or isn't running. Start Docker Desktop (or the docker service), then press Refresh.";
                MessageIsError = false;
                return;
            }
            foreach (var c in inv.Containers) Containers.Add(new ContainerRow(this, c));
            foreach (var i in inv.Images) Images.Add(new ImageRow(this, i));
            foreach (var v in inv.Volumes) Volumes.Add(new VolumeRow(this, v));
            var running = inv.Containers.Count(c => c.IsRunning);
            Summary = $"{inv.Containers.Count} containers ({running} running) · {inv.Images.Count} images · {inv.Volumes.Count} volumes";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsUnavailable));
        }
    }

    /// <summary>Runs one docker command, shows what happened, then refreshes the lists.</summary>
    internal async Task RunAsync(string doing, Func<Task<ProcessResult>> action, string done, string? logDeleted = null)
    {
        IsBusy = true;
        Message = doing;
        MessageIsError = false;
        ProcessResult r;
        try { r = await action(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { r = new ProcessResult(1, "", ex.Message); }
        finally { IsBusy = false; }
        MessageIsError = r.ExitCode != 0;
        Message = r.ExitCode == 0 ? done : "Docker said: " + FirstLine(r.Error.Length > 0 ? r.Error : r.Output);
        if (r.ExitCode == 0 && logDeleted is not null)
            shell.Services.Actions.Add("delete", [new ActionItem(logDeleted, "", 0)], "docker");
        await Refresh();
    }

    private static string FirstLine(string s) => s.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "it didn't work.";

    internal async Task ShowLogsAsync(DockerContainer c)
    {
        LogsTitle = $"Logs: {c.Name} (last 300 lines)";
        LogsText = "Loading…";
        var r = await Docker.LogsAsync(c.Id, 300);
        // docker writes the container's stderr to its own stderr: show both.
        var text = (r.Output + (r.Error.Length > 0 ? "\n" + r.Error : "")).Trim();
        LogsText = r.ExitCode == 0 ? (text.Length == 0 ? "(no output yet)" : text) : "Docker said: " + FirstLine(r.Error);
    }

    [RelayCommand]
    private void CloseLogs() => LogsTitle = "";

    [RelayCommand]
    private void SetView(string? v)
    {
        if (v is not null && Views.Contains(v)) View = v;
    }

    internal DockerManager Manager => Docker;
}

/// <summary>Deleting is two clicks: the first asks "Really delete?", the second deletes.</summary>
public abstract partial class DockerRowBase : ObservableObject
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DeleteText))] private bool confirmingDelete;
    public virtual string DeleteText => ConfirmingDelete ? "Really delete?" : "Delete";
    [RelayCommand] private void CancelDelete() => ConfirmingDelete = false;
}

public sealed partial class ContainerRow(DockerPageViewModel page, DockerContainer c) : DockerRowBase
{
    public DockerContainer Container => c;
    public string Name => c.Name;
    public string Image => c.Image;
    public string Status => c.Status;
    public string Project => c.Project.Length > 0 ? "Compose: " + c.Project : "";
    public bool HasProject => c.Project.Length > 0;
    public string PortsText => c.HostPorts.Count == 0 ? "" : "Ports " + string.Join(", ", c.HostPorts);
    public bool HasPort => c.HostPorts.Count > 0;
    public bool CanStart => c.IsStopped;
    public bool CanStop => c.IsRunning || c.IsPaused;
    public bool CanPause => c.IsRunning;
    public bool CanResume => c.IsPaused;
    public string StateText => c.IsRunning ? "Running" : c.IsPaused ? "Paused" : "Stopped";
    public IBrush StateBg => new SolidColorBrush(Color.Parse(c.IsRunning ? "#E6F2E6" : c.IsPaused ? "#FDF0E1" : "#EBEBEB"));
    public IBrush StateFg => new SolidColorBrush(Color.Parse(c.IsRunning ? "#0E700E" : c.IsPaused ? "#8A4600" : "#5D5D5D"));
    public override string DeleteText => ConfirmingDelete ? (c.IsStopped ? "Really delete?" : "Stop and delete?") : "Delete";

    [RelayCommand] private Task Start() => page.RunAsync($"Starting {c.Name}…", () => page.Manager.StartAsync(c.Id), $"Started {c.Name}.");
    [RelayCommand] private Task Stop() => page.RunAsync($"Stopping {c.Name}…", () => page.Manager.StopAsync(c.Id), $"Stopped {c.Name}.");
    [RelayCommand] private Task Restart() => page.RunAsync($"Restarting {c.Name}…", () => page.Manager.RestartAsync(c.Id), $"Restarted {c.Name}.");
    [RelayCommand] private Task Pause() => page.RunAsync($"Pausing {c.Name}…", () => page.Manager.PauseAsync(c.Id), $"Paused {c.Name}. Resume it to continue where it was.");
    [RelayCommand] private Task Resume() => page.RunAsync($"Resuming {c.Name}…", () => page.Manager.UnpauseAsync(c.Id), $"Resumed {c.Name}.");
    [RelayCommand] private Task Logs() => page.ShowLogsAsync(c);
    [RelayCommand] private void OpenPort() { if (c.HostPorts.Count > 0) ShellIntegration.Open($"http://localhost:{c.HostPorts[0]}"); }

    [RelayCommand]
    private Task Delete()
    {
        if (!ConfirmingDelete) { ConfirmingDelete = true; return Task.CompletedTask; }
        ConfirmingDelete = false;
        // Its volumes stay: they can hold data (delete them on the Volumes tab if you're sure).
        return page.RunAsync($"Deleting {c.Name}…", () => page.Manager.RemoveContainerAsync(c.Id, force: !c.IsStopped),
            $"Deleted container {c.Name}. Its image and volumes were kept.", $"container {c.Name}");
    }
}

public sealed partial class ImageRow(DockerPageViewModel page, DockerImage i) : DockerRowBase
{
    public string Name => i.Name;
    public string Details => $"{i.Size} · created {i.CreatedSince}";
    public string UsedText => i.Containers == 0 ? "Not used by any container" : $"Used by {i.Containers} {(i.Containers == 1 ? "container" : "containers")}";
    public bool CanDelete => i.Containers == 0;

    [RelayCommand]
    private Task Delete()
    {
        if (!ConfirmingDelete) { ConfirmingDelete = true; return Task.CompletedTask; }
        ConfirmingDelete = false;
        return page.RunAsync($"Deleting {i.Name}…", () => page.Manager.RemoveImageAsync(i.Id), $"Deleted image {i.Name}. Docker downloads it again if something needs it.", $"image {i.Name}");
    }
}

public sealed partial class VolumeRow(DockerPageViewModel page, DockerVolume v) : DockerRowBase
{
    public string Name => v.Name;
    public string Details => $"{(v.Size.Length > 0 ? v.Size : "size unknown")} · {v.Driver}";
    public string UsedText => v.Links == 0 ? "Not used by any container" : $"Used by {v.Links} {(v.Links == 1 ? "container" : "containers")}";
    public bool CanDelete => v.Links == 0;
    public override string DeleteText => ConfirmingDelete ? "Delete its data for good?" : "Delete";

    [RelayCommand]
    private Task Delete()
    {
        if (!ConfirmingDelete) { ConfirmingDelete = true; return Task.CompletedTask; }
        ConfirmingDelete = false;
        return page.RunAsync($"Deleting volume {v.Name}…", () => page.Manager.RemoveVolumeAsync(v.Name), $"Deleted volume {v.Name} and its data.", $"volume {v.Name}");
    }
}
