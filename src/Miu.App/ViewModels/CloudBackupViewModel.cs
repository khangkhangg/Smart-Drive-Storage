using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miu.App.Services;
using Miu.Cloud;
using Miu.Core.Automation;
using Miu.Core.Backup;
using Miu.Core.Formatting;
using Miu.Data;

namespace Miu.App.ViewModels;

public sealed partial class AccountRow(CloudBackupViewModel owner, CloudAccount account) : ObservableObject
{
    public CloudAccount Account { get; } = account;
    public string Title => Account.DisplayName;
    public string Kind => CloudAccountsService.ProviderName(Account.Provider);
    [RelayCommand] private void Disconnect() => owner.Disconnect(this);
}

public sealed partial class BackupPlanRow(CloudBackupViewModel owner, BackupPlan plan, string accountName) : ObservableObject
{
    public BackupPlan Plan { get; private set; } = plan;
    public string Name => Plan.Name;
    public string Route => $"{string.Join(", ", Plan.Sources.Select(s => s.LocalPath))}  →  {accountName} › {Plan.RemoteFolder}";
    public string Details => $"{Plan.ModeText} · {Plan.ScheduleText} · keeps {Plan.KeepVersions} older versions" + (Plan.BandwidthKBps > 0 ? $" · up to {Plan.BandwidthKBps} KB/s" : "");
    public string LastText => Plan.LastResult is { } r ? $"Last run {HumanTime.Ago(Plan.LastRunUtc ?? DateTime.UtcNow, DateTime.UtcNow)}: {r}" : "Not run yet.";

    [ObservableProperty] private bool enabled = plan.Enabled;
    partial void OnEnabledChanged(bool value) => Plan = owner.Services.BackupPlans.Patch(Plan.Id, p => p with { Enabled = value }) ?? Plan;

    [RelayCommand] private Task Preview() => owner.PreviewAsync(this);
    [RelayCommand] private void Restore() => owner.OpenRestore(this);
    [RelayCommand] private void Delete() => owner.DeletePlan(this);
}

public sealed partial class RestoreRow(RestoreItem item) : ObservableObject
{
    public RestoreItem Item { get; } = item;
    public ManifestEntry Entry => Item.Entry;
    public string Name => Path.GetFileName(Entry.LocalPath);
    public string Folder => Path.GetDirectoryName(Entry.LocalPath) ?? "";
    public string Info => SizeFormatter.Format(Entry.Size) +
        (Item.IsVersion ? $" · {Item.Note}, kept {HumanTime.Ago(Entry.UploadedUtc, DateTime.UtcNow)}"
                        : (Entry.Freed ? " · freed from this computer" : "") + $" · backed up {HumanTime.Ago(Entry.UploadedUtc, DateTime.UtcNow)}");
    [ObservableProperty] private bool isChecked;
}

/// <summary>Cloud &amp; Network › Cloud backup: accounts, plans, preview and run, restore.</summary>
public partial class CloudBackupViewModel : ObservableObject
{
    private readonly MainWindowViewModel _shell;
    private CancellationTokenSource? _runCts;
    private string? _previewRefusal;

    public CloudBackupViewModel(MainWindowViewModel shell)
    {
        _shell = shell;
        RefreshProviders();
        Load();
    }

    /// <summary>After you add or remove your own app keys in Settings.</summary>
    public void RefreshProviders()
    {
        Providers.Clear();
        foreach (var p in Accounts.Providers.Where(p => p.Key != "s3")) Providers.Add(p);
        if (AccountMessageIsError) Say("", false);
    }

    public AppServices Services => _shell.Services;
    private CloudAccountsService Accounts => _shell.Automation.Accounts;
    private BackupService Backup => _shell.Automation.Backup;

    // ---------- Accounts ----------
    public ObservableCollection<CloudProviderChoice> Providers { get; } = [];
    public ObservableCollection<AccountRow> AccountRows { get; } = [];
    public ObservableCollection<CloudAccount> AccountChoices { get; } = [];

    [ObservableProperty] private bool hasAccounts;
    [ObservableProperty] private string accountMessage = "";
    [ObservableProperty] private bool accountMessageIsError;
    [ObservableProperty] private bool isConnecting;
    [ObservableProperty] private string s3Name = "";
    [ObservableProperty] private string s3Endpoint = "https://s3.us-west-002.backblazeb2.com";
    [ObservableProperty] private string s3Region = "us-west-002";
    [ObservableProperty] private string s3Bucket = "";
    [ObservableProperty] private string s3AccessKey = "";
    [ObservableProperty] private string s3SecretKey = "";

    public void Load()
    {
        var keep = SelectedAccount?.Id;
        AccountRows.Clear();
        AccountChoices.Clear();
        foreach (var a in Services.CloudAccounts.All())
        {
            AccountRows.Add(new AccountRow(this, a));
            AccountChoices.Add(a);
        }
        HasAccounts = AccountRows.Count > 0;
        SelectedAccount = AccountChoices.FirstOrDefault(a => a.Id == keep) ?? AccountChoices.FirstOrDefault();
        LoadPlans();
    }

    [RelayCommand]
    private async Task ConnectOAuth(CloudProviderChoice? choice)
    {
        if (choice is null) return;
        if (!choice.Available)
        {
            // Not set up yet: take you to the step-by-step guide.
            Say("", false);
            _shell.Navigate(_shell.Settings);
            return;
        }
        IsConnecting = true;
        Say($"Finish signing in to {choice.Name} in your browser…", false);
        try
        {
            var a = await Accounts.ConnectOAuthAsync(choice.Key);
            Say($"Connected {a.DisplayName}.", false);
            Load();
        }
        catch (Exception ex) { Say(ex is OperationCanceledException ? "Sign-in took too long. Try again." : ex.Message, true); }
        finally { IsConnecting = false; }
    }

    [RelayCommand]
    private async Task ConnectS3()
    {
        if (new[] { S3Endpoint, S3Region, S3Bucket, S3AccessKey, S3SecretKey }.Any(string.IsNullOrWhiteSpace)) { Say("Fill in the endpoint, region, bucket and both keys.", true); return; }
        if (!Uri.TryCreate(S3Endpoint.Trim(), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) { Say("The endpoint must start with https://", true); return; }
        IsConnecting = true;
        try
        {
            var a = await Accounts.ConnectS3Async(S3Name, new S3Config(S3Endpoint.Trim(), S3Region.Trim(), S3Bucket.Trim(), S3AccessKey.Trim(), S3SecretKey.Trim()));
            S3SecretKey = S3AccessKey = ""; // kept only in the secret store
            Say($"Connected {a.DisplayName}.", false);
            Load();
        }
        catch (Exception ex) { Say("Couldn't connect: " + (ex is OperationCanceledException ? "the storage took too long to answer." : ex.Message), true); }
        finally { IsConnecting = false; }
    }

    public void Disconnect(AccountRow row)
    {
        if (Accounts.Disconnect(row.Account) is { } why) { Say(why, true); return; }
        Say($"Disconnected {row.Title}.", false);
        Load();
    }

    private void Say(string text, bool error)
    {
        AccountMessage = text;
        AccountMessageIsError = error;
    }

    // ---------- Plans ----------
    public ObservableCollection<BackupPlanRow> Plans { get; } = [];
    public IReadOnlyList<string> Modes { get; } = ["Copy new and changed", "Mirror", "Back up, then free space"];
    public IReadOnlyList<string> Frequencies { get; } = ["Only when I press Run", "Every day", "Every week", "When I sign in"];
    public IReadOnlyList<DayOfWeek> Days { get; } = Enum.GetValues<DayOfWeek>();
    public IReadOnlyList<int> Hours { get; } = Enumerable.Range(0, 24).ToList();

    [ObservableProperty] private bool hasPlans;
    [ObservableProperty] private CloudAccount? selectedAccount;
    [ObservableProperty] private string planName = "";
    [ObservableProperty] private string foldersText = "";
    [ObservableProperty] private string remoteFolder = "Miu Backup";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ModeHelp), nameof(IsFreeMode))] private string mode = "Copy new and changed";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowsDay), nameof(ShowsTime))] private string frequency = "Every day";
    [ObservableProperty] private DayOfWeek day = DayOfWeek.Sunday;
    [ObservableProperty] private int hour = 1;
    [ObservableProperty] private decimal freeAfterDays = 90;
    [ObservableProperty] private decimal keepVersions = 5;
    [ObservableProperty] private decimal bandwidthKBps;
    [ObservableProperty] private string planMessage = "";
    [ObservableProperty] private bool planMessageIsError;

    public bool ShowsDay => Frequency == "Every week";
    public bool ShowsTime => Frequency is "Every day" or "Every week";
    public bool IsFreeMode => Mode == "Back up, then free space";

    public string ModeHelp => Mode switch
    {
        "Mirror" => "The cloud matches your folders. Files you delete here are kept in the cloud as older versions (in .miu-versions), not erased.",
        "Back up, then free space" => "Uploads everything, then moves files you haven't touched for a while to the Recycle Bin, but only after Miu has checked each one's copy in the cloud. You can restore them any time.",
        _ => "Uploads new and changed files. Nothing in the cloud is removed; replaced copies are kept as older versions.",
    };

    private BackupMode ParsedMode => Mode switch { "Mirror" => BackupMode.Mirror, "Back up, then free space" => BackupMode.BackupThenFree, _ => BackupMode.CopyNew };

    private WatchSchedule? ParsedSchedule => Frequency switch
    {
        "Every day" => WatchSchedule.Daily(new TimeOnly(Hour, 0)),
        "Every week" => WatchSchedule.Weekly(Day, new TimeOnly(Hour, 0)),
        "When I sign in" => WatchSchedule.AtSignIn(),
        _ => null,
    };

    public void LoadPlans()
    {
        Plans.Clear();
        var names = Services.CloudAccounts.All().ToDictionary(a => a.Id, a => a.DisplayName);
        foreach (var p in Services.BackupPlans.All()) Plans.Add(new BackupPlanRow(this, p, names.GetValueOrDefault(p.AccountId, "?")));
        HasPlans = Plans.Count > 0;
    }

    [RelayCommand]
    private async Task SavePlan()
    {
        var folders = FoldersText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var schedule = ParsedSchedule;
        var plan = new BackupPlan(0, string.IsNullOrWhiteSpace(PlanName) ? string.Join(" + ", folders.Select(f => Path.GetFileName(Path.TrimEndingDirectorySeparator(f)))) : PlanName.Trim(),
            SelectedAccount?.Id ?? 0, BackupService.SourcesFor(folders), CloudPath.Normalize(RemoteFolder), ParsedMode, schedule,
            (int)Math.Clamp(KeepVersions, 1, 100), (int)Math.Clamp(FreeAfterDays, 7, 3650), (int)Math.Clamp(BandwidthKBps, 0, 1_000_000),
            NextRunLocal: schedule is null ? null : Scheduler.NextRun(schedule, DateTime.Now));
        if (Backup.Validate(plan) is { } problem) { PlanMessage = problem; PlanMessageIsError = true; return; }
        var id = Services.BackupPlans.Add(plan);
        PlanMessage = $"Saved “{plan.Name}”. Here's what the first run would do:";
        PlanMessageIsError = false;
        PlanName = FoldersText = "";
        LoadPlans();
        if (Plans.FirstOrDefault(p => p.Plan.Id == id) is { } row) await PreviewAsync(row);
    }

    public void DeletePlan(BackupPlanRow row)
    {
        if (IsRunning) { PlanMessage = "Wait for the running backup or restore to finish first."; PlanMessageIsError = true; return; }
        Services.BackupPlans.Delete(row.Plan.Id);
        if (Previewing == row) Previewing = null;
        if (Restoring == row) Restoring = null;
        PlanMessage = $"Deleted the plan “{row.Name}”. Its files stay in the cloud.";
        PlanMessageIsError = false;
        LoadPlans();
    }

    // ---------- Preview / run ----------
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPreview))] private BackupPlanRow? previewing;
    [ObservableProperty] private string previewText = "";
    [ObservableProperty] private bool previewCanRun;
    [ObservableProperty] private bool previewRefused;
    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private string progressText = "";
    public bool HasPreview => Previewing is not null;

    public async Task PreviewAsync(BackupPlanRow row)
    {
        Restoring = null;
        Previewing = row;
        PreviewText = "Comparing your folders with the backup…";
        PreviewCanRun = PreviewRefused = false;
        try
        {
            var p = await Backup.PreviewAsync(Services.BackupPlans.Get(row.Plan.Id) ?? row.Plan);
            PreviewText = p.Text;
            _previewRefusal = p.Result.Refusal;
            PreviewCanRun = p.CanRun;
            PreviewRefused = p.CanRunAnyway;
        }
        catch (Exception ex) { PreviewText = "Couldn't compare: " + ex.Message; }
    }

    [RelayCommand] private Task RunPreview() => RunAsync(null);
    [RelayCommand] private Task RunAnyway() => RunAsync(_previewRefusal);

    [RelayCommand]
    private void ClosePreview()
    {
        _runCts?.Cancel();
        Previewing = null;
    }

    private async Task RunAsync(string? overrideRefusal)
    {
        if (Previewing is not { } row || IsRunning) return;
        IsRunning = true;
        _runCts = new CancellationTokenSource();
        var progress = new Progress<BackupProgress>(p => ProgressText = $"{Math.Min(p.Done + 1, p.Total)} of {p.Total} · {SizeFormatter.Format(p.Bytes)} · {p.Current}");
        try
        {
            var o = await _shell.Automation.RunBackupAsync(Services.BackupPlans.Get(row.Plan.Id) ?? row.Plan, overrideRefusal, progress, _runCts.Token);
            PlanMessage = $"“{row.Name}”: {o.Summary}";
            PlanMessageIsError = o.Status is "failed" or "refused" or "partial";
            if (o.CleanActionId > 0) _shell.Activity.Load();
        }
        catch (OperationCanceledException) { PlanMessage = "Stopped. Uploaded files are kept; the next run continues from there."; }
        catch (Exception ex) { PlanMessage = "Couldn't back up: " + ex.Message; PlanMessageIsError = true; }
        finally
        {
            IsRunning = false;
            ProgressText = "";
            Previewing = null;
            LoadPlans();
        }
    }

    // ---------- Restore ----------
    public ObservableCollection<RestoreRow> RestoreRows { get; } = [];
    private List<RestoreRow> _allRestoreRows = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasRestore))] private BackupPlanRow? restoring;
    [ObservableProperty] private string restoreFilter = "";
    [ObservableProperty] private bool restoreToOriginal = true;
    [ObservableProperty] private string restoreFolder = "";
    [ObservableProperty] private string restoreMessage = "";
    [ObservableProperty] private string restoreCountText = "";
    public bool HasRestore => Restoring is not null;

    partial void OnRestoreFilterChanged(string value) => FilterRestore();

    public void OpenRestore(BackupPlanRow row)
    {
        Previewing = null;
        Restoring = row;
        RestoreMessage = "";
        _allRestoreRows = Backup.RestoreChoices(Services.BackupPlans.Get(row.Plan.Id) ?? row.Plan)
            .OrderBy(i => i.Entry.LocalPath, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.IsVersion).ThenByDescending(i => i.Entry.UploadedUtc)
            .Select(i => new RestoreRow(i)).ToList();
        FilterRestore();
    }

    private void FilterRestore()
    {
        RestoreRows.Clear();
        var f = RestoreFilter.Trim();
        var matches = _allRestoreRows.Where(r => f.Length == 0 || r.Entry.LocalPath.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var r in matches.Take(300)) RestoreRows.Add(r);
        RestoreCountText = matches.Count > 300 ? $"Showing 300 of {matches.Count}. Type to narrow down." : $"{matches.Count} {(matches.Count == 1 ? "file" : "files")} in the backup.";
    }

    [RelayCommand]
    private void SelectAllRestore()
    {
        foreach (var r in RestoreRows) r.IsChecked = true;
    }

    [RelayCommand]
    private void CloseRestore() => Restoring = null;

    [RelayCommand]
    private async Task RunRestore()
    {
        if (Restoring is not { } row || IsRunning) return;
        var chosen = _allRestoreRows.Where(r => r.IsChecked).Select(r => r.Item).ToList();
        if (chosen.Count == 0) { RestoreMessage = "Tick the files to restore."; return; }
        if (!RestoreToOriginal && string.IsNullOrWhiteSpace(RestoreFolder)) { RestoreMessage = "Type the folder to restore into."; return; }
        IsRunning = true;
        var progress = new Progress<BackupProgress>(p => RestoreMessage = $"Restoring {p.Done + 1} of {p.Total}: {p.Current}");
        try
        {
            var r = await Backup.RestoreAsync(Services.BackupPlans.Get(row.Plan.Id) ?? row.Plan, chosen, RestoreToOriginal ? null : RestoreFolder.Trim(), progress);
            RestoreMessage = $"Restored {r.Restored} {(r.Restored == 1 ? "file" : "files")}." +
                             (r.Skipped > 0 ? $" {r.Skipped} were already there." : "") +
                             (r.RenamedTo.Count > 0 ? $" {r.RenamedTo.Count} saved with “(restored …)” in the name because a different file was in the way." : "") +
                             (r.Failed.Count > 0 ? $" {r.Failed.Count} failed (first: {Path.GetFileName(r.Failed[0].Path)} — {r.Failed[0].Reason})." : "");
            OpenRestoreKeepMessage(row);
        }
        catch (Exception ex) { RestoreMessage = "Couldn't restore: " + ex.Message; }
        finally { IsRunning = false; }
    }

    private void OpenRestoreKeepMessage(BackupPlanRow row)
    {
        var msg = RestoreMessage;
        OpenRestore(row);
        RestoreMessage = msg;
    }
}
