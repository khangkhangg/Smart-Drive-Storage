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
    }

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
    }
}
