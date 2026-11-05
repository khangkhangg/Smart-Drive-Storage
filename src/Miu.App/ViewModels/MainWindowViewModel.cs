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
        Home = new HomeViewModel(this);
        Analyze = new AnalyzeViewModel(services, this);
        DevProjects = new DevProjectsViewModel(this);
        Rules = new RulesViewModel(this);
        About = new AboutViewModel();
        CloudNetwork = new CloudNetworkViewModel(this);
        Automations = new AutomationsViewModel(this);
        Settings = new SettingsViewModel(this);
        NavItems =
        [
            new("Home", "M3,11 L12,4 L21,11 L21,20 L3,20 Z", Home),
            new("Analyze", "M3,3 L13,3 L13,21 L3,21 Z M13,3 L21,3 L21,11 L13,11 Z M13,11 L21,11 L21,21 L13,21 Z", Analyze),
            new("Dev Projects", "M8,7 L3,12 L8,17 M16,7 L21,12 L16,17 M14,4 L10,20", DevProjects),
            new("Rules", "M12,3 L20,7 L20,12 C20,17 16.5,20 12,21 C7.5,20 4,17 4,12 L4,7 Z", Rules),
            new("Activity", "M12,3 A9,9 0 1 1 11.99,3 Z M12,7 L12,12 L15.5,14", Activity),
            new("Automations", "M12,3 A9,9 0 1 1 11.99,3 Z M12,7 L12,12 L16,12 M4,4 L7,7", Automations),
            new("Cloud & Network", "M7,18 A5,5 0 0 1 7,8 A6,6 0 0 1 18.5,10 A4,4 0 0 1 18,18 Z", CloudNetwork),
            new("Settings", "M12,9 A3,3 0 1 1 11.99,9 Z M12,2 L12,5 M12,19 L12,22 M2,12 L5,12 M19,12 L22,12 M5,5 L7,7 M17,17 L19,19 M5,19 L7,17 M17,7 L19,5", Settings),
            new("About", "M12,3 A9,9 0 1 1 11.99,3 Z M12,11 L12,17 M12,7.5 L12,8", About),
        ];
        selectedNav = NavItems[0];
        currentPage = Home;
    }

    public AppServices Services { get; }
    public CleanupService Cleanup { get; }
    public CartViewModel Cart { get; }
    public ActivityViewModel Activity { get; }
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

    /// <summary>Shown when something unexpected went wrong on screen (it was logged; Miu keeps running).</summary>
    public void ShowProblem(Exception ex) =>
        Notice = new NoticeViewModel(this, "Something went wrong, but Miu kept running.", ex.Message, 0, "Open reports folder",
            () => ShellIntegration.Open(CrashReporter.Folder(Path.GetDirectoryName(MiuDatabasePath)!)));

    private static string MiuDatabasePath => Miu.Data.MiuDatabase.DefaultPath;

    // ---------- Updates ----------
    public UpdateService Updates { get; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateText))] private UpdateInfo? availableUpdate;
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
        try
        {
            var u = await Updates.CheckAsync(AppInfo.Version, ct);
            s.Set(Miu.Data.SettingKeys.LastUpdateCheck, DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            if (u is null) { AvailableUpdate = null; return $"You have the newest version ({AppInfo.Version})."; }
            if (!force && s.Get(Miu.Data.SettingKeys.SkippedVersion) == u.Version) return "";
            AvailableUpdate = u;
            return $"Miu {u.Version} is available.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return "Couldn't check for updates right now. Are you online?";
        }
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void DownloadUpdate()
    {
        if (AvailableUpdate is { } u) ShellIntegration.Open(u.DownloadUrl ?? u.PageUrl);
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
    public async Task CleanAsync(IReadOnlyList<CartItem> items, string origin)
    {
        var outcome = await Cleanup.CleanAsync(items, Analyze.Session, origin);
        CloseReview();
        if (Analyze.Session is { } session)
        {
            Analyze.RefreshCurrent();
            OnScanCompleted(session);
        }
        var message = outcome.Count == 0
            ? "Nothing was cleaned."
            : $"Moved {outcome.Count} {(outcome.Count == 1 ? "item" : "items")} ({SizeFormatter.Format(outcome.FreedBytes)}) to the {Services.RecycleBinName}.";
        var details = outcome.Failed.Count == 0 ? "" :
            $"{outcome.Failed.Count} could not be moved: " + string.Join("; ", outcome.Failed.Take(3).Select(f => $"{Path.GetFileName(f.Path)} — {f.Reason}"));
        Notice = new NoticeViewModel(this, message, details, outcome.ActionId);
        if (CurrentPage == Activity) Activity.Load();
        Home.RefreshRuleCounts();
    }

    /// <summary>Puts a clean back and rescans so the screens show the restored items.</summary>
    public async Task UndoAsync(long actionId)
    {
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

    public void OnScanCompleted(ScanSession session)
    {
        Home.Update(session);
        DevProjects.Update(session);
    }
}

public sealed record PlaceholderViewModel(string Title, string Message);
