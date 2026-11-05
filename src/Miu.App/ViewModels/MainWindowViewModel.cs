using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Miu.App.Services;
using Miu.Core.Cleanup;
using Miu.Core.Formatting;

namespace Miu.App.ViewModels;

public sealed record NavItem(string Title, string Icon, object Page);

public partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel(AppServices services, INotifier? notifier = null, Func<string, Miu.Notify.ITelegramApi>? telegramApi = null,
        UpdateService? updates = null)
    {
        Services = services;
        Updates = updates ?? new UpdateService(new HttpClient { Timeout = TimeSpan.FromSeconds(20) });
        var composite = new CompositeNotifier(notifier ?? new ToastNotifier());
        Notifier = composite;
        Automation = new AutomationService(services, Notifier, services.Power, () => DateTime.Now,
            items =>
            {
                ShowWindow?.Invoke(); // Miu may be in the tray
                Navigate(Home!);
                if (!IsReviewOpen) OpenReview(items, "Review suggestions", "cart");
            });
        Telegram = new TelegramService(services, Automation,
            telegramApi ?? (token => new Miu.Notify.TelegramBotApi(new HttpClient { Timeout = TimeSpan.FromSeconds(70) }, token)),
            () => DateTime.UtcNow);
        composite.Add(new TelegramNotifier(Telegram, services.Settings));
        Cleanup = new CleanupService(services);
        Learning = new LearningService(services);
        Cart = new CartViewModel(this, services.Cart);
        Activity = new ActivityViewModel(this);
        // Free space changes all the time (downloads, other apps): keep the Home drive cards current.
        _driveTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _driveTimer.Tick += (_, _) => { if (CurrentPage == Home) Home.RefreshDrives(); };
        _driveTimer.Start();
        Home = new HomeViewModel(this);
        Analyze = new AnalyzeViewModel(services, this);
        DevProjects = new DevProjectsViewModel(this);
        Rules = new RulesViewModel(this);
        About = new AboutViewModel();
        CloudNetwork = new CloudNetworkViewModel(this);
        Automations = new AutomationsViewModel(this);
        Settings = new SettingsViewModel(this);
        DockerPage = new DockerPageViewModel(this);
        NavItems =
        [
            new("Home", "M3,11 L12,4 L21,11 L21,20 L3,20 Z", Home),
            new("Analyze", "M3,3 L13,3 L13,21 L3,21 Z M13,3 L21,3 L21,11 L13,11 Z M13,11 L21,11 L21,21 L13,21 Z", Analyze),
            new("Dev Projects", "M8,7 L3,12 L8,17 M16,7 L21,12 L16,17 M14,4 L10,20", DevProjects),
            new("Docker", "M3,12 L21,12 C21,17 17,20 12,20 C7,20 3,17 3,12 Z M5,12 L5,9 L8,9 L8,12 M9,12 L9,9 L12,9 L12,12 M13,12 L13,9 L16,9 L16,12 M9,9 L9,6 L12,6 L12,9", DockerPage),
            new("Rules", "M12,3 L20,7 L20,12 C20,17 16.5,20 12,21 C7.5,20 4,17 4,12 L4,7 Z", Rules),
            new("Activity", "M12,3 A9,9 0 1 1 11.99,3 Z M12,7 L12,12 L15.5,14", Activity),
            new("Automations", "M12,3 A9,9 0 1 1 11.99,3 Z M12,7 L12,12 L16,12 M4,4 L7,7", Automations),
            new("Cloud & Network", "M7,18 A5,5 0 0 1 7,8 A6,6 0 0 1 18.5,10 A4,4 0 0 1 18,18 Z", CloudNetwork),
            new("Settings", "M12,9 A3,3 0 1 1 11.99,9 Z M12,2 L12,5 M12,19 L12,22 M2,12 L5,12 M19,12 L22,12 M5,5 L7,7 M17,17 L19,19 M5,19 L7,17 M17,7 L19,5", Settings),
            new("About", "M12,3 A9,9 0 1 1 11.99,3 Z M12,11 L12,17 M12,7.5 L12,8", About),
        ];
        if (!services.Settings.GetBool(Miu.Data.SettingKeys.OnboardingDone, false)) onboarding = new OnboardingViewModel(this);
        selectedNav = NavItems[0];
        currentPage = Home;
    }

    public AppServices Services { get; }
    public CleanupService Cleanup { get; }
    public CartViewModel Cart { get; }
    public ActivityViewModel Activity { get; }
    public DockerPageViewModel DockerPage { get; }
    private readonly Avalonia.Threading.DispatcherTimer _driveTimer;
    public HomeViewModel Home { get; }
    public AnalyzeViewModel Analyze { get; }
    public DevProjectsViewModel DevProjects { get; }
    public RulesViewModel Rules { get; }
    public LearningService Learning { get; }
    public SettingsViewModel Settings { get; }
    public AutomationsViewModel Automations { get; }
    public INotifier Notifier { get; }
    public TelegramService Telegram { get; }

    /// <summary>Brings the main window back (set by the app; Miu may be hidden in the tray).</summary>
    public Action? ShowWindow { get; set; }
    public AutomationService Automation { get; }
    public AboutViewModel About { get; }
    public CloudNetworkViewModel CloudNetwork { get; }
    public ObservableCollection<NavItem> NavItems { get; }

    [ObservableProperty] private NavItem selectedNav;
    [ObservableProperty] private object currentPage;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsReviewOpen))] private ReviewViewModel? review;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasNotice))] private NoticeViewModel? notice;

    public bool IsReviewOpen => Review is not null;
    public bool HasNotice => Notice is not null;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasOnboarding))] private OnboardingViewModel? onboarding;
    public bool HasOnboarding => Onboarding is not null;

    /// <summary>Shown when something unexpected went wrong on screen (it was logged; Miu keeps running).</summary>
    public void ShowProblem(Exception ex) =>
        Notice = new NoticeViewModel(this, "Something went wrong, but Miu kept running.", ex.Message, 0, "Open reports folder",
            () => ShellIntegration.Open(CrashReporter.Folder(Path.GetDirectoryName(MiuDatabasePath)!)));

    private static string MiuDatabasePath => Miu.Data.MiuDatabase.DefaultPath;

    // ---------- Updates ----------
    public UpdateService Updates { get; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateText), nameof(CanDownloadUpdate))] private UpdateInfo? availableUpdate;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsUpdateReady), nameof(CanDownloadUpdate))] private string? downloadedUpdate;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanDownloadUpdate))] private bool isDownloadingUpdate;
    [ObservableProperty] private string updateProgress = "";
    public bool IsUpdateReady => DownloadedUpdate is not null;
    public bool CanDownloadUpdate => HasUpdate && !IsDownloadingUpdate && !IsUpdateReady;

    /// <summary>Set by the app: quits Miu (so an installer can replace it).</summary>
    public Action? ExitApp { get; set; }

    /// <summary>Where updates are downloaded (tests use a temp folder).</summary>
    public string UpdateFolder { get; set; } = UpdateService.DownloadFolder();

    partial void OnAvailableUpdateChanged(UpdateInfo? value)
    {
        // A different version than the file we already have: that file is stale.
        if (value is null || DownloadedUpdate is { } f && !Path.GetFileName(f).Contains(value.Version, StringComparison.OrdinalIgnoreCase)) DownloadedUpdate = null;
    }
    public bool HasUpdate => AvailableUpdate is not null;
    public string UpdateText => AvailableUpdate is { } u ? $"Miu {u.Version} is available (you have {AppInfo.Version})." : "";

    /// <summary>
    /// Checks GitHub for a newer release: when asked, or once a day if automatic checks are on.
    /// Returns a short status for Settings. Never throws: being offline is fine.
    /// </summary>
    public async Task<string> CheckForUpdatesAsync(bool force, CancellationToken ct = default)
    {
        var s = Services.Settings;
        if (!force)
        {
            if (!s.GetBool(Miu.Data.SettingKeys.AutoUpdateCheck, true)) return "";
            if (s.Get(Miu.Data.SettingKeys.LastUpdateCheck) is { } last &&
                DateTime.TryParse(last, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var t) &&
                DateTime.UtcNow - t < TimeSpan.FromHours(20)) return "";
        }
        // Recorded before asking: offline or rate-limited, the next automatic try is tomorrow, not in a minute.
        s.Set(Miu.Data.SettingKeys.LastUpdateCheck, DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            var u = await Updates.CheckAsync(AppInfo.Version, ct, s.GetBool(Miu.Data.SettingKeys.IncludePrereleases, false));
            if (u is null) { AvailableUpdate = null; return $"You have the newest version ({AppInfo.Version})."; }
            if (!force && s.Get(Miu.Data.SettingKeys.SkippedVersion) == u.Version) return "";
            AvailableUpdate = u;
            if (s.GetBool(Miu.Data.SettingKeys.AutoDownloadUpdates, false) && u.DownloadUrl is not null)
            {
                await DownloadUpdateAsync(ct);
                return IsUpdateReady ? $"Miu {u.Version} is downloaded and ready to install." : $"Miu {u.Version} is available. {UpdateProgress}";
            }
            return $"Miu {u.Version} is available.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return "Couldn't check for updates right now. Are you online?";
        }
    }

    /// <summary>Downloads the update inside Miu (checked against the release's checksums), then offers Install now.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private Task DownloadUpdate() => DownloadUpdateAsync(default);

    public async Task DownloadUpdateAsync(CancellationToken ct)
    {
        if (AvailableUpdate is not { } u || IsDownloadingUpdate || IsUpdateReady) return;
        if (u.DownloadUrl is null) { ShellIntegration.Open(u.PageUrl); return; } // no file for this system: the release page
        IsDownloadingUpdate = true;
        UpdateProgress = "Downloading…";
        try
        {
            DownloadedUpdate = await Updates.DownloadAsync(u, UpdateFolder,
                new Progress<double>(f => UpdateProgress = $"Downloading… {f:P0}"), ct);
            UpdateProgress = $"Downloaded to {DownloadedUpdate}.";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException or UnauthorizedAccessException)
        {
            UpdateProgress = "Couldn't download it: " + ex.Message;
        }
        finally { IsDownloadingUpdate = false; }
    }

    /// <summary>
    /// Starts the downloaded update: on Windows the installer runs and Miu closes so it can be replaced; on macOS the
    /// .dmg opens; on Linux the file is shown (an AppImage is made runnable first).
    /// </summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void InstallUpdate()
    {
        if (DownloadedUpdate is not { } file || !File.Exists(file)) { DownloadedUpdate = null; return; }
        if (OperatingSystem.IsWindows() && file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true });
                ExitApp?.Invoke();
            }
            catch (System.ComponentModel.Win32Exception ex) { UpdateProgress = "The installer didn't start: " + ex.Message; }
            return;
        }
        if (!OperatingSystem.IsWindows() && file.EndsWith(".AppImage", StringComparison.Ordinal))
        {
            try { File.SetUnixFileMode(file, File.GetUnixFileMode(file) | UnixFileMode.UserExecute); } catch (IOException) { }
            ShellIntegration.Reveal(file);
            UpdateProgress = "The new AppImage is ready: quit Miu, then start the new file (you can delete the old one).";
            return;
        }
        if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            ShellIntegration.Reveal(file);
            UpdateProgress = "Quit Miu, unpack the new version over the old one, and start it again.";
            return;
        }
        ShellIntegration.Open(file); // .dmg, .deb: the system's installer takes it from here
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void SkipUpdate()
    {
        if (AvailableUpdate is { } u) Services.Settings.Set(Miu.Data.SettingKeys.SkippedVersion, u.Version);
        AvailableUpdate = null;
    }

    partial void OnSelectedNavChanged(NavItem value)
    {
        CurrentPage = value.Page;
        if (value.Page == Home) Home.RefreshDrives();
        if (value.Page == CloudNetwork) CloudNetwork.OnShown();
        if (value.Page == DockerPage) _ = DockerPage.RefreshCommand.ExecuteAsync(null);
        if (value.Page == Activity) Activity.Load();
        if (value.Page == Rules) Rules.Load();
        if (value.Page == Automations) Automations.Load();
        if (value.Page == DevProjects) _ = DevProjects.Docker.Refresh();
    }

    /// <summary>After any rule change: re-judge the current scan and refresh every screen that shows verdicts.</summary>
    public void ApplyRuleChanges()
    {
        if (Analyze.Session is not { } session) return;
        session.ReplaceRules(Services.AllRules());
        // Anything in the cart that you now keep (or that holds something you keep) leaves the cart.
        Services.Cart.RemoveAll(Services.Cart.Items.Where(i => session.KeepConflict(i.Path) is not null).Select(i => i.Path).ToList());
        Analyze.RefreshCurrent();
        OnScanCompleted(session);
    }

    public void OpenReview(IReadOnlyList<CartItem> items, string title, string origin)
    {
        // The same top-level-only rule as the cart, so nothing is shown or recycled twice.
        var unique = new CleanupCart(Miu.Core.Rules.PathNormalizer.Current);
        unique.AddRange(items);
        if (unique.Count == 0) return;
        Review = new ReviewViewModel(this, unique.Items.ToList(), title, origin);
    }

    public void CloseReview() => Review = null;

    /// <summary>Cleans through the guarded Recycle Bin, then refreshes every screen and shows the result with Undo.</summary>
    public async Task CleanAsync(IReadOnlyList<CartItem> items, string origin, IProgress<CleanProgress>? progress = null, CancellationToken stop = default)
    {
        var outcome = await Cleanup.CleanAsync(items, Analyze.Session, origin, progress, stop);
        CloseReview();
        if (Analyze.Session is { } session)
        {
            Analyze.RefreshCurrent();
            OnScanCompleted(session);
        }
        else Home.RefreshDrives();
        var message = (outcome.Count == 0
            ? "Nothing was cleaned."
            : $"Moved {outcome.Count} {(outcome.Count == 1 ? "item" : "items")} ({SizeFormatter.Format(outcome.FreedBytes)}) to the {Services.RecycleBinName}.")
            + (outcome.Stopped ? $" Stopped as you asked: {outcome.NotStarted} {(outcome.NotStarted == 1 ? "item was" : "items were")} left as they were." : "");
        var details = outcome.Failed.Count == 0 ? "" :
            $"{outcome.Failed.Count} could not be moved: " + string.Join("; ", outcome.Failed.Take(3).Select(f => $"{Path.GetFileName(f.Path)} — {f.Reason}"));
        Notice = new NoticeViewModel(this, message, details, outcome.ActionId);
        if (CurrentPage == Activity) Activity.Load();
        Home.RefreshRuleCounts();
    }

    /// <summary>Puts a clean back and rescans so the screens show the restored items.</summary>
    public async Task UndoAsync(long actionId)
    {
        if (Services.Actions.Get(actionId) is { Kind: "rule-added", Undone: false } added)
        {
            // Undoing a rule you added removes it again.
            foreach (var i in added.Items) Services.RulesStore.Delete(i.TrashLocation);
            Services.Actions.MarkUndone(actionId);
            Rules.Load();
            ApplyRuleChanges();
            Notice = new NoticeViewModel(this, "Rule removed.", "", 0);
            Activity.Load();
            return;
        }
        if (Services.Actions.Get(actionId) is { Kind: "sync" })
        {
            var (restored, skipped) = await Automation.Sync.UndoAsync(actionId);
            Notice = new NoticeViewModel(this, $"Put back {restored} {(restored == 1 ? "file" : "files")}.",
                skipped.Count == 0 ? "" : $"{skipped.Count} not restored: " + string.Join("; ", skipped.Take(3)), 0);
            Activity.Load();
            return;
        }
        var result = await Cleanup.UndoAsync(actionId);
        var details = result.Failed.Count == 0 ? "" :
            $"{result.Failed.Count} could not be restored: " + string.Join("; ", result.Failed.Take(3).Select(f => $"{Path.GetFileName(f.Path)} — {f.Reason}"));
        Notice = new NoticeViewModel(this, $"Restored {result.Restored.Count} {(result.Restored.Count == 1 ? "item" : "items")}.", details, 0);
        Activity.Load();
        if (Analyze.Session is { } session && result.Restored.Count > 0) await Analyze.RescanAsync(session.Tree.RootPath);
    }

    public void Navigate(object page) => SelectedNav = NavItems.First(n => n.Page == page);

    public const string BuyMeACoffeeUrl = "https://buymeacoffee.com/dkhang";
    public const string PayPalUrl = "https://www.paypal.com/donate/?hosted_button_id=FWYNCN2KN3C3L";

    /// <summary>Opens a donation page in the browser (only these two fixed links).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void OpenDonation(string? which) => ShellIntegration.Open(which == "paypal" ? PayPalUrl : BuyMeACoffeeUrl);

    /// <summary>Ctrl+1 … Ctrl+9 go to the pages in sidebar order.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void GoToPage(string? number)
    {
        if (int.TryParse(number, out var n) && n >= 1 && n <= NavItems.Count && !IsReviewOpen && !HasOnboarding) SelectedNav = NavItems[n - 1];
    }

    public void OnScanCompleted(ScanSession session)
    {
        // Things deleted outside Miu since they went into the cart leave it.
        Services.Cart.Refresh([], p => File.Exists(p) || Directory.Exists(p));
        Home.Update(session);
        DevProjects.Update(session);
    }
}

public sealed record PlaceholderViewModel(string Title, string Message);
