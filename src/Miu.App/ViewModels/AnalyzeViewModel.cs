using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miu.App.Services;
using Miu.Core.Formatting;
using Miu.Data;
using Miu.Platform.Abstractions;

namespace Miu.App.ViewModels;

public sealed record Crumb(string Name, int NodeId);

public sealed record TypeRow(string Name, string SizeText, double Fraction, string FilesText, Avalonia.Media.IBrush Brush);

public sealed record DuplicateFileRow(int NodeId, string Path, bool IsKeep, string ModifiedText, long Size);

public sealed record DuplicateGroupRow(string SizeText, string WastedText, IReadOnlyList<DuplicateFileRow> Files, long WastedBytes);

public partial class AnalyzeViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly MainWindowViewModel _shell;
    private CancellationTokenSource? _cts;
    private int _scanGeneration;
    private int _current = -1;

    public AnalyzeViewModel(AppServices services, MainWindowViewModel shell)
    {
        _services = services;
        _shell = shell;
        pathInput = services.Settings.Get(SettingKeys.LastScanPath)
                    ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        services.Cart.Changed += (_, _) => CartChanged();
    }

    public bool SelectedInCart => Selected is { } s && _services.Cart.Contains(s.FullPath);
    public bool CanAddSelected => Selected is { } s && s.Verdict.Verdict != Miu.Core.Rules.Verdict.Protected && !SelectedInCart;

    partial void OnSelectedChanged(NodeItemViewModel? value) => CartChanged();

    private void CartChanged()
    {
        OnPropertyChanged(nameof(SelectedInCart));
        OnPropertyChanged(nameof(CanAddSelected));
        AddToCartCommand.NotifyCanExecuteChanged();
    }

    private bool CanAdd(NodeItemViewModel? item) =>
        item is not null && item.Verdict.Verdict != Miu.Core.Rules.Verdict.Protected && !_services.Cart.Contains(item.FullPath);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddToCart(NodeItemViewModel? item)
    {
        if (item is null || !CanAdd(item)) return;
        _services.Cart.Add(new Miu.Core.Cleanup.CartItem(item.FullPath, item.Name, item.Size, item.Verdict.Verdict, item.Reason));
    }

    [RelayCommand]
    private void RemoveFromCart(NodeItemViewModel? item)
    {
        if (item is not null) _services.Cart.Remove(item.FullPath);
    }

    /// <summary>Re-reads the current folder after a clean (climbing up if it was removed itself).</summary>
    public void RefreshCurrent()
    {
        if (Session is null) return;
        var id = _current;
        while (id > Session.Tree.Root && Session.Tree.IsRemoved(id)) id = Session.Tree.Parent(id);
        var selectedId = Selected?.Id;
        ShowNode(Math.Max(id, Session.Tree.Root));
        Selected = Items.FirstOrDefault(i => i.Id == selectedId) ?? Selected;
        var t = Session.Tree;
        TotalText = $"{SizeFormatter.Format(t.Size(t.Root))} · {t.FileCount(t.Root):N0} files";
    }

    public Task RescanAsync(string path) => ScanPathAsync(path);

    public ScanSession? Session { get; private set; }
    public ObservableCollection<NodeItemViewModel> Items { get; } = [];
    public ObservableCollection<Crumb> Breadcrumbs { get; } = [];

    [ObservableProperty] private string pathInput;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle))] private bool isScanning;
    [ObservableProperty] private string progressText = "Pick a drive or folder and press Scan.";
    [ObservableProperty] private string totalText = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSelection))] private NodeItemViewModel? selected;
    [ObservableProperty] private bool hasResult;

    public bool IsIdle => !IsScanning;

    // ---------- View switch (Folders | Types | Largest | Duplicates) ----------

    public ObservableCollection<TypeRow> TypeRows { get; } = [];
    public ObservableCollection<NodeItemViewModel> LargestRows { get; } = [];
    public ObservableCollection<DuplicateGroupRow> DuplicateGroups { get; } = [];
    public IReadOnlyList<string> Views { get; } = ["Folders", "Types", "Largest", "Duplicates"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFoldersView), nameof(IsTypesView), nameof(IsLargestView), nameof(IsDuplicatesView))]
    private string viewMode = "Folders";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(FindDuplicatesCommand))] private bool isFindingDuplicates;
    [ObservableProperty] private string duplicatesStatus = "Find files that are exact copies of each other. Miu keeps the oldest copy and offers the rest.";
    [ObservableProperty] private string duplicatesTotalText = "";

    public long DuplicateMinBytes { get; set; } = 1 << 20;
    public bool IsFoldersView => ViewMode == "Folders";
    public bool IsTypesView => ViewMode == "Types";
    public bool IsLargestView => ViewMode == "Largest";
    public bool IsDuplicatesView => ViewMode == "Duplicates";

    partial void OnViewModeChanged(string value) => RefreshInsights();

    [RelayCommand]
    private void SetView(string? view)
    {
        if (view is null || !Views.Contains(view)) return;
        ViewMode = view;
    }

    private void RefreshInsights()
    {
        if (Session is null) return;
        var t = Session.Tree;
        var node = _current < 0 ? t.Root : _current;
        if (IsTypesView)
        {
            TypeRows.Clear();
            var rows = Miu.Core.Insights.TypeBreakdown.Compute(t, node);
            var max = Math.Max(1, rows.Count == 0 ? 1 : rows.Max(r => r.Bytes));
            foreach (var (cat, bytes, files) in rows)
                TypeRows.Add(new TypeRow(cat == Miu.Core.Scanning.FileCategory.AppDataCache ? "Caches & temp" : cat.ToString(),
                    SizeFormatter.Format(bytes), (double)bytes / max, $"{files:N0} {(files == 1 ? "file" : "files")}",
                    Palette.ForTile(cat, false, Miu.Core.Rules.Verdict.Review).Fill));
        }
        else if (IsLargestView)
        {
            LargestRows.Clear();
            var size = t.Size(node);
            foreach (var id in Miu.Core.Insights.LargestFiles.Find(t, node, 100)) LargestRows.Add(new NodeItemViewModel(Session, id, size));
        }
    }

    private CancellationTokenSource? _dupCts;

    private bool CanFindDuplicates() => Session is not null && !IsFindingDuplicates;

    [RelayCommand(CanExecute = nameof(CanFindDuplicates))]
    private async Task FindDuplicates()
    {
        if (Session is not { } s) return;
        IsFindingDuplicates = true;
        DuplicatesStatus = "Comparing files…";
        _dupCts = new CancellationTokenSource();
        try
        {
            var node = _current < 0 ? s.Tree.Root : _current;
            var groups = await Miu.Platform.Portable.DuplicateFinder.FindAsync(s.Tree, node, DuplicateMinBytes, null, _dupCts.Token);
            DuplicateGroups.Clear();
            foreach (var g in groups.Take(300))
                DuplicateGroups.Add(new DuplicateGroupRow(
                    $"{g.Nodes.Count} copies of {SizeFormatter.Format(g.Size)}", SizeFormatter.Format(g.WastedBytes) + " wasted",
                    g.Nodes.OrderByDescending(n => n == g.KeepNode).Select(n => new DuplicateFileRow(n, s.Tree.GetFullPath(n), n == g.KeepNode,
                        "changed " + HumanTime.Ago(s.Tree.ModifiedUtc(n), s.NowUtc), g.Size)).ToList(), g.WastedBytes));
            var wasted = groups.Sum(g => g.WastedBytes);
            DuplicatesTotalText = groups.Count == 0 ? "" : $"{groups.Count:N0} groups · {SizeFormatter.Format(wasted)} could be freed";
            DuplicatesStatus = groups.Count == 0 ? "No duplicates found (files of 1 MB or more)." : "Miu keeps the oldest copy of each. Copies go to the cart as Review.";
        }
        catch (OperationCanceledException) { DuplicatesStatus = "Stopped."; }
        finally { IsFindingDuplicates = false; }
    }

    [RelayCommand]
    private void CancelDuplicates() => _dupCts?.Cancel();

    /// <summary>
    /// Adds the copies of one group, but never all of them: nothing is added when the kept copy is gone, already in the
    /// cart, or itself marked Safe (it could be cleaned too), and copies you keep are skipped.
    /// </summary>
    [RelayCommand]
    private void AddGroupToCart(DuplicateGroupRow? group)
    {
        if (group is null || Session is not { } s) return;
        var keep = group.Files.First(f => f.IsKeep);
        var keepNode = s.FindNode(keep.Path);
        if (keepNode is null || !File.Exists(keep.Path) || _services.Cart.Contains(keep.Path)
            || s.VerdictFor(keepNode.Value).Verdict == Miu.Core.Rules.Verdict.Safe)
        {
            DuplicatesStatus = $"Skipped {Path.GetFileName(keep.Path)}: the copy Miu would keep is gone, in the cart, or marked Safe itself.";
            return;
        }
        foreach (var f in group.Files.Where(f => !f.IsKeep))
        {
            if (s.FindNode(f.Path) is not { } node || !File.Exists(f.Path)) continue;
            if (s.VerdictFor(node).Verdict is Miu.Core.Rules.Verdict.Keep or Miu.Core.Rules.Verdict.Protected) continue;
            _services.Cart.Add(new Miu.Core.Cleanup.CartItem(f.Path, Path.GetFileName(f.Path), f.Size,
                Miu.Core.Rules.Verdict.Review, $"Duplicate of {keep.Path}", FromDuplicates: true));
        }
    }

    [RelayCommand]
    private void AddAllDuplicatesToCart()
    {
        foreach (var g in DuplicateGroups) AddGroupToCart(g);
    }

    // ---------- Teach Miu ----------

    public IReadOnlyList<string> Reasons => LearningService.Reasons;

    [ObservableProperty] private string? selectedReason;

    [RelayCommand]
    private void TeachKeep() => Teach(Miu.Core.Learning.DecisionAction.Keep);

    [RelayCommand]
    private void TeachOk() => Teach(Miu.Core.Learning.DecisionAction.Delete);

    private void Teach(Miu.Core.Learning.DecisionAction action)
    {
        if (Selected is not { } item || Session is null) return;
        if (item.Verdict.Verdict == Miu.Core.Rules.Verdict.Protected) return;
        _shell.Learning.Teach(item.FullPath, item.IsDir, action, SelectedReason, Session);
        SelectedReason = null;
        RefreshCurrent();
        _shell.OnScanCompleted(Session);
        _shell.Rules.Load();
        _shell.Notice = new NoticeViewModel(_shell, action == Miu.Core.Learning.DecisionAction.Keep
            ? $"Got it. Miu will keep {item.Name} and never suggest it."
            : $"Got it. Miu will treat {item.Name} as safe to delete.", "", 0);
    }
    public bool HasSelection => Selected is not null;

    [RelayCommand]
    private async Task Scan()
    {
        var path = PathInput?.Trim();
        if (string.IsNullOrEmpty(path)) return;
        if (!Directory.Exists(path)) { ProgressText = $"Folder not found: {path}"; return; }

        // A newer scan replaces an older one; only the newest may update the screen.
        var generation = ++_scanGeneration;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsScanning = true;
        ProgressText = "Scanning…";
        var clock = Stopwatch.StartNew();
        var progress = new Progress<ScanProgress>(p =>
        {
            if (generation == _scanGeneration) ProgressText = $"Scanning… {p.Files:N0} files · {SizeFormatter.Format(p.Bytes)}";
        });
        try
        {
            var session = await ScanSession.RunAsync(_services, path, progress, token);
            if (generation != _scanGeneration) return;
            Session = session;
            // "What grew" compares with an older scan of the same place; then remember this one.
            _shell.Home.SetGrew(ScanInsights.WhatGrew(_services, session, DateTime.UtcNow));
            ScanInsights.Record(_services, session, DateTime.UtcNow);
            _shell.Home.RefreshForecasts();
            DuplicateGroups.Clear();
            DuplicatesTotalText = "";
            var t = Session.Tree;
            TotalText = $"{SizeFormatter.Format(t.Size(t.Root))} · {t.FileCount(t.Root):N0} files";
            ProgressText = $"Scanned in {clock.Elapsed.TotalSeconds:0.0} s";
            HasResult = true;
            _services.Settings.Set(SettingKeys.LastScanPath, path);
            ShowNode(t.Root);
            _shell.OnScanCompleted(Session);
        }
        catch (OperationCanceledException)
        {
            if (generation == _scanGeneration) ProgressText = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            if (generation == _scanGeneration) ProgressText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            if (generation == _scanGeneration) IsScanning = false;
        }
    }

    [RelayCommand]
    private void CancelScan() => _cts?.Cancel();

    [RelayCommand]
    private void Open(NodeItemViewModel? item)
    {
        if (item is { IsDir: true }) ShowNode(item.Id);
    }

    [RelayCommand]
    private void GoUp()
    {
        if (Session is null || _current <= Session.Tree.Root) return;
        var child = _current;
        ShowNode(Session.Tree.Parent(_current));
        Selected = Items.FirstOrDefault(i => i.Id == child) ?? Selected;
    }

    [RelayCommand]
    private void GoToCrumb(Crumb? crumb)
    {
        if (crumb is not null) ShowNode(crumb.NodeId);
    }

    [RelayCommand]
    private void Reveal(NodeItemViewModel? item)
    {
        if (item is not null) ShellIntegration.Reveal(item.FullPath);
    }

    public async Task ScanPathAsync(string path)
    {
        PathInput = path;
        await ScanCommand.ExecuteAsync(null);
    }

    /// <summary>Opens a folder of the current scan by node id (used by Home and Dev Projects).</summary>
    public void ShowNode(int id)
    {
        if (Session is null) return;
        var t = Session.Tree;
        _current = id;
        Items.Clear();
        var size = t.Size(id);
        foreach (var c in t.Children(id).Take(500)) Items.Add(new NodeItemViewModel(Session, c, size));

        Breadcrumbs.Clear();
        var chain = new List<int>();
        for (var n = id; n >= 0; n = t.Parent(n)) chain.Add(n);
        chain.Reverse();
        foreach (var n in chain) Breadcrumbs.Add(new Crumb(n == t.Root ? t.RootPath : t.Name(n), n));
        Selected = Items.FirstOrDefault();
        RefreshInsights();
    }
}
