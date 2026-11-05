using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miu.App.Services;
using Miu.Core.Automation;
using Miu.Core.Formatting;
using Miu.Core.Sync;
using Miu.Platform.Abstractions;
using Miu.Platform.Portable;

namespace Miu.App.ViewModels;

public sealed record MappedRow(MappedDrive Drive)
{
    public string Title => Drive.Letter is { } l ? $"{l}  {Drive.Unc}" : Drive.Unc;
    public string Status => Drive.Available ? "Connected" : "Not connected right now";
    public string ScanPath => Drive.Letter is { } l ? l + Path.DirectorySeparatorChar : Drive.Unc;
}

public sealed record PreviewOpRow(string Action, string Path, string Reason);

public partial class SyncJobRow(CloudNetworkViewModel owner, SyncJob job) : ObservableObject
{
    public SyncJob Job { get; private set; } = job;
    public string Name => Job.Name;
    public string Route => $"{Job.Source}  →  {Job.Target}";
    public string Details => $"{Job.KindText}" + (Job.Kind == SyncKind.DriveBackup ? (Job.Mode == SyncMode.CopyOnly ? " (keeps files you delete)" : " (mirror)") : "")
        + $" · {Job.ScheduleText}" + (Job.CompareByHash ? " · compares content" : "")
        + (Job.Excludes is { Length: > 0 } x ? $" · skips {x.Length} {(x.Length == 1 ? "pattern" : "patterns")}" : "");
    public string LastText => Job.LastResult is { } r ? $"Last run {HumanTime.Ago(Job.LastRunUtc ?? DateTime.UtcNow, DateTime.UtcNow)}: {r}" : "Not run yet.";

    [ObservableProperty] private bool enabled = job.Enabled;

    partial void OnEnabledChanged(bool value) =>
        Job = owner.Services.SyncJobs.Patch(Job.Id, j => j with { Enabled = value }) ?? Job;

    [RelayCommand] private Task Preview() => owner.PreviewAsync(this);
    [RelayCommand] private void Delete() => owner.DeleteJob(this);
}

/// <summary>Cloud &amp; Network: network drives (find, map, scan), sync jobs, and cloud backup (Milestone 7).</summary>
public partial class CloudNetworkViewModel : ObservableObject
{
    private readonly MainWindowViewModel _shell;
    private CancellationTokenSource? _runCts;

    public CloudNetworkViewModel(MainWindowViewModel shell)
    {
        _shell = shell;
        Network = new NetworkService(shell.Services);
        Backup = new CloudBackupViewModel(shell);
        RefreshDrives();
        LoadJobs();
    }

    public CloudBackupViewModel Backup { get; }

    public NetworkService Network { get; }
    public AppServices Services => _shell.Services;

    // ---------- Network drives ----------

    public bool CanMap => Network.Drives.IsSupported;
    public bool CannotMap => !CanMap;
    public string UnsupportedReason => Network.Drives.UnsupportedReason;

    public ObservableCollection<DiscoveredDevice> Devices { get; } = [];
    public ObservableCollection<string> Shares { get; } = [];
    public ObservableCollection<MappedRow> Mapped { get; } = [];
    public ObservableCollection<string> Letters { get; } = [];

    [ObservableProperty] private bool isSearching;
    [ObservableProperty] private string devicesText = "Find computers and NAS boxes that share folders on your network.";
    [ObservableProperty] private string uncText = "";
    [ObservableProperty] private string? selectedLetter;
    [ObservableProperty] private string userName = "";
    [ObservableProperty] private string password = "";
    [ObservableProperty] private bool reconnect = true;
    [ObservableProperty] private string networkMessage = "";
    [ObservableProperty] private bool networkMessageIsError;
    [ObservableProperty] private string scanPathText = "";
    [ObservableProperty] private bool hasMapped;

    public const string NoLetter = "No letter";

    public void RefreshDrives()
    {
        Mapped.Clear();
        foreach (var m in Network.Drives.Mapped()) Mapped.Add(new MappedRow(m));
        HasMapped = Mapped.Count > 0;
        var keep = SelectedLetter;
        Letters.Clear();
        foreach (var l in Network.Drives.FreeLetters()) Letters.Add(l);
        Letters.Add(NoLetter);
        SelectedLetter = keep is not null && Letters.Contains(keep) ? keep : Letters.FirstOrDefault();
    }

    [RelayCommand]
    private async Task FindDevices()
    {
        IsSearching = true;
        DevicesText = "Looking for devices…";
        try
        {
            await Task.Yield();
            var found = await Network.DiscoverAsync();
            Devices.Clear();
            foreach (var d in found) Devices.Add(d);
            DevicesText = found.Count == 0
                ? @"Nothing answered. Some devices don't announce themselves: type \\name\folder below."
                : $"Found {found.Count} {(found.Count == 1 ? "device" : "devices")}. Pick one to see its shared folders.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { DevicesText = "Couldn't search the network: " + ex.Message; }
        finally { IsSearching = false; }
    }

    [RelayCommand]
    private async Task PickDevice(DiscoveredDevice? d)
    {
        if (d is null) return;
        UncText = $@"\\{d.Address}\";
        Shares.Clear();
        try { foreach (var s in await Network.Drives.ListSharesAsync(d.Address, default)) Shares.Add(s); }
        catch (Exception ex) when (ex is not OperationCanceledException) { System.Diagnostics.Trace.WriteLine("Listing shares failed: " + ex.Message); }
        NetworkMessageIsError = false;
        NetworkMessage = Shares.Count > 0 ? $"{d.Name} shares {Shares.Count} {(Shares.Count == 1 ? "folder" : "folders")}. Pick one." : $"Type the shared folder name after \\\\{d.Address}\\.";
    }

    [RelayCommand]
    private void PickShare(string? share)
    {
        if (share is null || !UncPath.TryParse(UncText.TrimEnd('\\') + "\\" + share, out var p)) return;
        UncText = p!.ToString();
    }

    [RelayCommand]
    private async Task Map()
    {
        var letter = SelectedLetter == NoLetter ? null : SelectedLetter;
        NetResult r;
        try { r = await Network.MapAsync(UncText, letter, UserName, Password, Reconnect); }
        catch (Exception ex) when (ex is not OperationCanceledException) { r = NetResult.Fail("Couldn't connect: " + ex.Message); }
        Password = ""; // never kept on screen
        NetworkMessageIsError = !r.Ok;
        NetworkMessage = r.Message;
        if (r.Ok) RefreshDrives();
    }

    [RelayCommand]
    private async Task Unmap(MappedRow? row)
    {
        if (row is null) return;
        NetResult r;
        try { r = await Network.UnmapAsync(row.Drive); }
        catch (Exception ex) when (ex is not OperationCanceledException) { r = NetResult.Fail("Couldn't disconnect: " + ex.Message); }
        NetworkMessageIsError = !r.Ok;
        NetworkMessage = r.Message;
        RefreshDrives();
    }

    [RelayCommand]
    private async Task ScanMapped(MappedRow? row)
    {
        if (row is null) return;
        _shell.Navigate(_shell.Analyze);
        await _shell.Analyze.ScanPathAsync(row.ScanPath);
    }

    [RelayCommand]
    private async Task ScanPath()
    {
        var path = UncPath.TryParse(ScanPathText, out var unc) && OperatingSystem.IsWindows() ? unc!.ToString() : ScanPathText.Trim();
        if (path.Length == 0 || !await Task.Run(() => Directory.Exists(path)))
        {
            NetworkMessageIsError = true;
            NetworkMessage = $"Miu can't open {path}. Is it connected?";
            return;
        }
        _shell.Navigate(_shell.Analyze);
        await _shell.Analyze.ScanPathAsync(path);
    }

    // ---------- Sync jobs ----------

    public ObservableCollection<SyncJobRow> Jobs { get; } = [];
    public ObservableCollection<PreviewOpRow> PreviewOps { get; } = [];

    public IReadOnlyList<string> Modes { get; } = ["Mirror", "Copy new and changed", "Two-way"];
    public IReadOnlyList<string> Frequencies { get; } = ["Only when I press Run", "Every day", "Every week", "When I sign in"];
    public IReadOnlyList<DayOfWeek> Days { get; } = Enum.GetValues<DayOfWeek>();
    public IReadOnlyList<int> Hours { get; } = Enumerable.Range(0, 24).ToList();

    [ObservableProperty] private bool hasJobs;
    [ObservableProperty] private string jobName = "";
    [ObservableProperty] private string jobSource = "";
    [ObservableProperty] private string jobTarget = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ModeHelp))] private string mode = "Mirror";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowsDay), nameof(ShowsTime))] private string frequency = "Every day";
    [ObservableProperty] private DayOfWeek day = DayOfWeek.Sunday;
    [ObservableProperty] private int hour = 2;
    [ObservableProperty] private bool compareByHash;

    // ---------- What kind of job: folders, or a whole drive ----------

    public IReadOnlyList<string> JobKinds { get; } = ["Sync or mirror folders", "Back up a whole drive"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDriveBackup), nameof(IsFolderJob), nameof(EditorTitle), nameof(TargetCaption), nameof(TargetWatermark))]
    private string jobKind = "Sync or mirror folders";

    public bool IsDriveBackup => JobKind == "Back up a whole drive";
    public bool IsFolderJob => !IsDriveBackup;
    public string EditorTitle => IsDriveBackup ? "New drive backup" : "New sync job";
    public string TargetCaption => IsDriveBackup ? "Back up to (a folder on another drive)" : "To (target) · one per line to mirror to several places";
    public string TargetWatermark => IsDriveBackup
        ? (OperatingSystem.IsWindows() ? @"E:\Miu Backups" : "/Volumes/Backup/Miu Backups")
        : (OperatingSystem.IsWindows() ? @"\\nas\photos\backup" : "/mnt/nas/photos");

    /// <summary>Drives you can back up (connected, readable).</summary>
    public IReadOnlyList<string> SourceDrives => DriveInfo.GetDrives()
        .Where(d => { try { return d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable; } catch (IOException) { return false; } })
        .Select(d => d.RootDirectory.FullName).ToList();

    [ObservableProperty] private bool keepDeletedFiles = true;
    [ObservableProperty] private bool skipRebuildable = true;
    [ObservableProperty] private bool runOnChange;
    [ObservableProperty] private string jobExcludes = "";

    partial void OnJobKindChanged(string value)
    {
        if (IsDriveBackup)
        {
            JobSource = SourceDrives.FirstOrDefault() ?? "";
            JobExcludes = string.Join("\n", SyncFilter.DriveBackupDefaults(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS()));
            RunOnChange = false;
            if (Frequency == "Only when I press Run") Frequency = "Every day";
        }
        else JobExcludes = "";
    }

    /// <summary>The skip list as saved: what you typed, plus rebuildable folders when that box is ticked.</summary>
    private string[] ParsedExcludes =>
        SyncFilter.Parse(JobExcludes).Concat(SkipRebuildable ? SyncFilter.Rebuildable : []).Distinct().ToArray();
    [ObservableProperty] private string syncMessage = "";
    [ObservableProperty] private bool syncMessageIsError;

    [ObservableProperty] private SyncJobRow? previewing;
    [ObservableProperty] private string previewText = "";
    [ObservableProperty] private bool previewCanRun;
    [ObservableProperty] private bool previewRefused;
    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private string progressText = "";

    public bool ShowsDay => Frequency == "Every week";
    public bool ShowsTime => Frequency is "Every day" or "Every week";
    public bool HasPreview => Previewing is not null;
    partial void OnPreviewingChanged(SyncJobRow? value) => OnPropertyChanged(nameof(HasPreview));

    public string ModeHelp => Mode switch
    {
        "Mirror" => "The target becomes an exact copy of the source. Files you remove from the source are set aside in the target's .miu-sync-trash, not deleted.",
        "Copy new and changed" => "Adds new and newer files to the target. Nothing is ever removed there.",
        _ => "Changes on either side are copied to the other. If a file changed on both sides, Miu keeps both versions.",
    };

    private SyncMode ParsedMode => Mode switch { "Mirror" => SyncMode.Mirror, "Copy new and changed" => SyncMode.CopyOnly, _ => SyncMode.TwoWay };

    private WatchSchedule? ParsedSchedule => Frequency switch
    {
        "Every day" => WatchSchedule.Daily(new TimeOnly(Hour, 0)),
        "Every week" => WatchSchedule.Weekly(Day, new TimeOnly(Hour, 0)),
        "When I sign in" => WatchSchedule.AtSignIn(),
        _ => null,
    };

    public void LoadJobs()
    {
        Jobs.Clear();
        foreach (var j in Services.SyncJobs.All()) Jobs.Add(new SyncJobRow(this, j));
        HasJobs = Jobs.Count > 0;
    }

    [RelayCommand]
    private async Task SaveJob()
    {
        var repo = Services.SyncJobs;
        var kind = IsDriveBackup ? SyncKind.DriveBackup : SyncKind.Folder;
        // Several targets (one per line): one job each, so each can be previewed, paused and undone on its own.
        var targets = (JobTarget ?? "").Split('\n').Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList();
        if (targets.Count == 0) targets.Add("");
        if (kind == SyncKind.DriveBackup && targets.Count > 1) targets = [targets[0]];
        foreach (var t in targets)
            if (_shell.Automation.Sync.Validate(JobSource, t, kind) is { } problem)
            {
                SyncMessageIsError = true;
                SyncMessage = targets.Count > 1 ? $"{t}: {problem}" : problem;
                return;
            }
        var schedule = ParsedSchedule;
        var mode = kind == SyncKind.DriveBackup ? (KeepDeletedFiles ? SyncMode.CopyOnly : SyncMode.Mirror) : ParsedMode;
        var excludes = ParsedExcludes;
        var source = JobSource.Trim();
        var ids = new List<long>();
        foreach (var target in targets)
        {
            var name = !string.IsNullOrWhiteSpace(JobName) ? JobName.Trim() + (targets.Count > 1 ? $" → {Leaf(target)}" : "")
                : kind == SyncKind.DriveBackup ? $"Back up {source.TrimEnd('\\', '/')} → {Leaf(target)}"
                : $"{Leaf(source)} → {Leaf(target)}";
            ids.Add(repo.Add(new SyncJob(0, name, source, target, mode, schedule, CompareByHash,
                NextRunLocal: schedule is null ? null : Scheduler.NextRun(schedule, DateTime.Now),
                Excludes: excludes.Length == 0 ? null : excludes, RunOnChange: RunOnChange && kind == SyncKind.Folder, Kind: kind)));
        }
        SyncMessageIsError = false;
        SyncMessage = ids.Count == 1 ? "Saved. Here's what the first run would do:" : $"Saved {ids.Count} jobs, one per target. Here's what the first one would do:";
        JobName = JobSource = JobTarget = "";
        LoadJobs();
        if (Jobs.FirstOrDefault(r => r.Job.Id == ids[0]) is { } row) await PreviewAsync(row);
    }

    private static string Leaf(string path) => Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : path;

    public async Task PreviewAsync(SyncJobRow row)
    {
        Previewing = row;
        PreviewOps.Clear();
        PreviewText = "Comparing the two folders…";
        PreviewCanRun = PreviewRefused = false;
        SyncPreview p;
        try { p = await _shell.Automation.Sync.PreviewAsync(Services.SyncJobs.Get(row.Job.Id) ?? row.Job); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PreviewText = "Couldn't compare the folders: " + ex.Message;
            return;
        }
        PreviewText = p.Text;
        _previewRefusal = p.Plan.Refusal;
        PreviewRefused = p.CanRunAnyway;
        PreviewCanRun = p.CanRun;
        foreach (var op in p.Plan.Ops.Take(100))
            PreviewOps.Add(new PreviewOpRow(op.Kind switch
            {
                SyncOpKind.Copy => op.From == Side.Source ? "Copy →" : "← Copy",
                SyncOpKind.Archive => op.From == Side.Source ? "Set aside (source)" : "Set aside (target)",
                _ => "Keep both",
            }, op.RelPath, op.Reason + (op.Kind == SyncOpKind.Archive ? "" : $" · {SizeFormatter.Format(op.Bytes)}")));
        if (p.Plan.Ops.Count > 100) PreviewOps.Add(new PreviewOpRow("", $"…and {p.Plan.Ops.Count - 100} more", ""));
    }

    private string? _previewRefusal;

    [RelayCommand] private Task RunPreview() => RunAsync(null);
    [RelayCommand] private Task RunAnyway() => RunAsync(_previewRefusal);

    [RelayCommand]
    private void ClosePreview()
    {
        _runCts?.Cancel();
        Previewing = null;
    }

    /// <param name="overrideRefusal">The refusal shown in this preview; a different one (things changed since) still stops the run.</param>
    private async Task RunAsync(string? overrideRefusal)
    {
        if (Previewing is not { } row || IsRunning) return;
        IsRunning = true;
        _runCts = new CancellationTokenSource();
        var progress = new Progress<SyncProgress>(p => ProgressText = p.Total == 0 ? "" : $"{p.Done} of {p.Total} · {SizeFormatter.Format(p.Bytes)} · {p.Current}");
        try
        {
            var outcome = await _shell.Automation.RunSyncAsync(Services.SyncJobs.Get(row.Job.Id) ?? row.Job, overrideRefusal, progress, _runCts.Token);
            SyncMessageIsError = outcome.Status is "failed" or "refused" or "partial";
            SyncMessage = $"“{row.Name}”: {outcome.Summary}";
            if (outcome.ActionId > 0) _shell.Activity.Load();
        }
        catch (OperationCanceledException) { SyncMessage = "Stopped. Finished files are kept, and anything set aside is in Activity. The rest will be copied next time."; }
        catch (Exception ex)
        {
            SyncMessageIsError = true;
            SyncMessage = "Couldn't sync: " + ex.Message;
        }
        finally
        {
            IsRunning = false;
            ProgressText = "";
            Previewing = null;
            LoadJobs();
        }
    }

    public void DeleteJob(SyncJobRow row)
    {
        Services.SyncJobs.Delete(row.Job.Id);
        if (Previewing == row) Previewing = null;
        LoadJobs();
    }
}

public static class CloudNetworkConverters
{
    public static readonly Avalonia.Data.Converters.IValueConverter CloseOrStop =
        new Avalonia.Data.Converters.FuncValueConverter<bool, string>(running => running ? "Stop" : "Close");
}
