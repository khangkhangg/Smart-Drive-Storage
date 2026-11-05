using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miu.App.Services;
using Miu.Core.Formatting;
using Miu.Dev;

namespace Miu.App.ViewModels;

public sealed record DriveCard(string Title, string Subtitle, string Root, double UsedFraction, string UsedText, string FreeText,
    string Status, IBrush StatusBg, IBrush StatusFg, IBrush Bar, string Badge, long TotalBytes = 0, string? ForecastText = null)
{
    public bool HasForecast => ForecastText is not null;
}

public sealed record GrowRow(string Path, string Name, string DeltaText);

public sealed record SafeRow(int NodeId, string Name, string Path, string SizeText, string Reason);

public sealed record CategoryChip(string Name, long Bytes, string SizeText);

public sealed record ProjectChip(int NodeId, string Name, string Detail, string RebuildableText);

/// <summary>
/// Storage overview for everyone (safe-to-remove, locations, what grew, rules), plus a developer section.
/// </summary>
public partial class HomeViewModel : ObservableObject
{
    private readonly MainWindowViewModel _shell;
    private DateTime? _scannedAtUtc;

    public HomeViewModel(MainWindowViewModel shell)
    {
        _shell = shell;
        foreach (var d in DiscoverDrives()) Drives.Add(d);
        BuiltInRuleCount = shell.Services.Rules.Count;
        RefreshRuleCounts();
        RefreshForecasts();
    }

    public ObservableCollection<DriveCard> Drives { get; } = [];
    public ObservableCollection<SafeRow> TopSafe { get; } = [];
    public ObservableCollection<CategoryChip> Categories { get; } = [];
    public ObservableCollection<GrowRow> Grew { get; } = [];

    [ObservableProperty] private bool hasGrew;

    public void SetGrew(IReadOnlyList<(string Path, long Delta)> grew)
    {
        Grew.Clear();
        foreach (var (p, d) in grew) Grew.Add(new GrowRow(p, System.IO.Path.GetFileName(p), "+" + SizeFormatter.Format(d)));
        HasGrew = Grew.Count > 0;
    }

    /// <summary>Adds "Full in ~N weeks" to drive cards that have enough history.</summary>
    public void RefreshForecasts()
    {
        for (var i = 0; i < Drives.Count; i++)
        {
            var card = Drives[i];
            var samples = _shell.Services.SpaceSamples.For(card.Root, 60);
            var total = samples.Count > 0 ? samples[^1].Total : card.TotalBytes;
            var text = Miu.Core.Insights.Forecast.Describe(Miu.Core.Insights.Forecast.DaysUntilFull(samples.Select(x => (x.Ts, x.Used)).ToList(), total));
            if (text != card.ForecastText) Drives[i] = card with { ForecastText = text };
        }
    }
    public ObservableCollection<ProjectChip> TopProjects { get; } = [];

    public string VersionText => $"{AppInfo.Name} v{AppInfo.Version}";
    public string MadeByText => $"Made with love by {AppInfo.Author}";
    public int BuiltInRuleCount { get; }

    [ObservableProperty] private int learnedRuleCount;
    [ObservableProperty] private string rulesSummary = "Every verdict comes from a rule you can read. Mark things Keep or OK to delete and Miu learns from you.";

    public void RefreshRuleCounts()
    {
        LearnedRuleCount = _shell.Services.RulesStore.All().Count;
        var pending = _shell.Services.Suggestions.Pending().Count;
        RulesSummary = pending > 0
            ? $"Miu noticed {pending} {(pending == 1 ? "pattern" : "patterns")} in your choices. Open Rules to turn {(pending == 1 ? "it" : "them")} on."
            : "Every verdict comes from a rule you can read. Mark things Keep or OK to delete and Miu learns from you.";
    }
    public string HomeFolder { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasNoScan))] private bool hasScan;
    [ObservableProperty] private long safeTotalBytes;
    [ObservableProperty] private string safeTotalText = "";
    [ObservableProperty] private string categoriesText = "";
    [ObservableProperty] private string scannedPath = "";
    [ObservableProperty] private bool showSafeItems;
    [ObservableProperty] private int devProjectCount;
    [ObservableProperty] private string devRebuildableText = "";
    [ObservableProperty] private int devIdleCount;
    [ObservableProperty] private string devSummary = "";

    public bool HasNoScan => !HasScan;
    public string SafeHint => $"Temp files, caches, crash dumps, old installers and rebuildable dev folders. Everything goes to the {_shell.Services.RecycleBinName} first.";
    public string LocationsText => $"{Drives.Count} {(Drives.Count == 1 ? "location" : "locations")} connected";
    public string LastScanText => _scannedAtUtc is { } at
        ? $"Last scan {HumanTime.Ago(at, DateTime.UtcNow)} · {LocationsText}"
        : $"Not scanned yet · {LocationsText}";

    [RelayCommand]
    private async Task ScanDrive(DriveCard? card)
    {
        if (card is null) return;
        _shell.Navigate(_shell.Analyze);
        await _shell.Analyze.ScanPathAsync(card.Root);
    }

    [RelayCommand]
    private async Task ScanHome()
    {
        _shell.Navigate(_shell.Analyze);
        await _shell.Analyze.ScanPathAsync(HomeFolder);
    }

    private bool CanQuickClean() => HasScan && SafeTotalBytes > 0 && _shell.Services.CanClean;

    /// <summary>Opens Review &amp; Clean with every 🟢 Safe item from the last scan.</summary>
    [RelayCommand(CanExecute = nameof(CanQuickClean))]
    private void QuickClean()
    {
        if (_shell.Analyze.Session is not { } s) return;
        var items = s.SafeItems.Select(i => new Miu.Core.Cleanup.CartItem(
            s.Tree.GetFullPath(i.NodeId), s.Tree.Name(i.NodeId), s.Tree.Size(i.NodeId), i.Verdict.Verdict, i.Verdict.Reason)).ToList();
        _shell.OpenReview(items, "Quick Clean", "quick-clean");
    }

    [RelayCommand]
    private void ToggleSafeItems() => ShowSafeItems = !ShowSafeItems;

    [RelayCommand]
    private void OpenSafeItem(SafeRow? row) => Reveal(row?.NodeId);

    [RelayCommand]
    private void OpenProject(ProjectChip? project)
    {
        if (project is null) return;
        _shell.Analyze.ShowNode(project.NodeId);
        _shell.Navigate(_shell.Analyze);
    }

    [RelayCommand]
    private void OpenDevProjects() => _shell.Navigate(_shell.DevProjects);

    [RelayCommand]
    private void OpenRules() => _shell.Navigate(_shell.Rules);

    [RelayCommand]
    private void OpenAbout() => _shell.Navigate(_shell.About);

    private void Reveal(int? nodeId)
    {
        if (nodeId is not { } id || _shell.Analyze.Session is null) return;
        var parent = _shell.Analyze.Session.Tree.Parent(id);
        _shell.Analyze.ShowNode(parent < 0 ? id : parent);
        _shell.Analyze.Selected = _shell.Analyze.Items.FirstOrDefault(i => i.Id == id) ?? _shell.Analyze.Selected;
        _shell.Navigate(_shell.Analyze);
    }

    public void Update(ScanSession s)
    {
        var t = s.Tree;
        _scannedAtUtc = DateTime.UtcNow;
        HasScan = true;
        ScannedPath = t.RootPath;
        SafeTotalBytes = s.SafeTotalBytes;
        SafeTotalText = SizeFormatter.Format(s.SafeTotalBytes);

        Categories.Clear();
        foreach (var g in s.SafeItems.GroupBy(i => SafeCategories.For(i.Verdict.RuleId))
                     .Select(g => (Name: g.Key, Bytes: g.Sum(i => t.Size(i.NodeId))))
                     .OrderByDescending(g => g.Bytes))
            Categories.Add(new CategoryChip(g.Name, g.Bytes, SizeFormatter.Format(g.Bytes)));
        CategoriesText = Categories.Count switch
        {
            0 => "nothing clearly safe yet",
            1 => "in 1 category",
            var n => $"across {n} categories",
        };

        TopSafe.Clear();
        foreach (var (id, v) in s.SafeItems.Take(8))
        {
            var p = t.Parent(id);
            TopSafe.Add(new SafeRow(id, p > t.Root ? $"{t.Name(p)} › {t.Name(id)}" : t.Name(id), t.GetFullPath(id),
                SizeFormatter.Format(t.Size(id)), v.Reason));
        }

        var projects = s.Projects;
        DevProjectCount = projects.Count;
        DevRebuildableText = SizeFormatter.Format(projects.Sum(p => p.RebuildableBytes));
        DevIdleCount = projects.Count(p => p.Bucket >= IdleBucket.Idle);
        DevSummary = projects.Count == 0
            ? "No code projects in this scan. Scan your code folder to find node_modules, build output and caches."
            : $"{projects.Count} projects hold {DevRebuildableText} of rebuildable files · {DevIdleCount} idle for more than 90 days";
        TopProjects.Clear();
        foreach (var p in projects.Where(p => p.RebuildableBytes > 0).Take(3))
            TopProjects.Add(new ProjectChip(p.NodeId, p.Name,
                $"{string.Join(" · ", p.Stacks)} · last activity {HumanTime.Ago(p.LastActivityUtc, s.NowUtc)}",
                SizeFormatter.Format(p.RebuildableBytes)));

        OnPropertyChanged(nameof(LastScanText));
        QuickCleanCommand.NotifyCanExecuteChanged();
    }

    private static IEnumerable<DriveCard> DiscoverDrives()
    {
        DriveInfo[] all;
        try { all = DriveInfo.GetDrives(); } catch { yield break; }
        var seen = new HashSet<string>();
        foreach (var d in all)
        {
            DriveCard? card = null;
            try
            {
                if (!d.IsReady || d.TotalSize < 1L << 30) continue;
                if (d.DriveType is not (DriveType.Fixed or DriveType.Network or DriveType.Removable)) continue;
                var root = d.RootDirectory.FullName;
                if (!OperatingSystem.IsWindows() && root != "/" &&
                    !new[] { "/home", "/mnt", "/media", "/Volumes", "/run/media" }.Any(p => root.StartsWith(p, StringComparison.Ordinal)))
                    continue;
                if (!seen.Add($"{d.TotalSize}:{d.AvailableFreeSpace}")) continue;
                var used = d.TotalSize - d.TotalFreeSpace;
                var frac = (double)used / d.TotalSize;
                var low = frac > 0.85;
                var windows = OperatingSystem.IsWindows();
                var title = windows
                    ? (string.IsNullOrEmpty(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel)
                    : root == "/" ? "System" : Path.GetFileName(root.TrimEnd('/'));
                var badge = windows ? root.TrimEnd('\\') : root == "/" ? "/" : title[..Math.Min(2, title.Length)].ToUpperInvariant();
                var kind = d.DriveType switch { DriveType.Network => "Network drive", DriveType.Removable => "Removable drive", _ => "Local drive" } + " · " + d.DriveFormat;
                card = new DriveCard(title, kind, root, frac, SizeFormatter.Format(used) + " used", SizeFormatter.Format(d.AvailableFreeSpace) + " free",
                    low ? "Low space" : "Healthy",
                    new SolidColorBrush(Color.Parse(low ? "#FDF0E1" : "#E6F2E6")), new SolidColorBrush(Color.Parse(low ? "#8A4600" : "#0E700E")),
                    new SolidColorBrush(Color.Parse(low ? "#C25700" : "#005FB8")), badge, d.TotalSize);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (card is not null) yield return card;
        }
    }
}
