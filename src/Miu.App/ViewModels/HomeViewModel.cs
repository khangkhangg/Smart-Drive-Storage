using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miu.App.Services;
using Miu.Core.Formatting;

namespace Miu.App.ViewModels;

public sealed record DriveCard(string Title, string Subtitle, string Root, double UsedFraction, string UsedText, string FreeText,
    string Status, IBrush StatusBg, IBrush StatusFg, IBrush Bar);

public sealed record SafeRow(int NodeId, string Name, string Path, string SizeText, string Reason);

public partial class HomeViewModel : ObservableObject
{
    private readonly MainWindowViewModel _shell;

    public HomeViewModel(MainWindowViewModel shell)
    {
        _shell = shell;
        foreach (var d in DiscoverDrives()) Drives.Add(d);
    }

    public ObservableCollection<DriveCard> Drives { get; } = [];
    public ObservableCollection<SafeRow> TopSafe { get; } = [];

    [ObservableProperty] private bool hasScan;
    [ObservableProperty] private long safeTotalBytes;
    [ObservableProperty] private string safeTotalText = "";
    [ObservableProperty] private string safeSummary = "";
    [ObservableProperty] private string devSummary = "";
    [ObservableProperty] private string scannedPath = "";

    [RelayCommand]
    private async Task ScanDrive(DriveCard? card)
    {
        if (card is null) return;
        _shell.Navigate(_shell.Analyze);
        await _shell.Analyze.ScanPathAsync(card.Root);
    }

    [RelayCommand]
    private void OpenSafeItem(SafeRow? row)
    {
        if (row is null || _shell.Analyze.Session is null) return;
        var parent = _shell.Analyze.Session.Tree.Parent(row.NodeId);
        _shell.Analyze.ShowNode(parent < 0 ? row.NodeId : parent);
        _shell.Analyze.Selected = _shell.Analyze.Items.FirstOrDefault(i => i.Id == row.NodeId) ?? _shell.Analyze.Selected;
        _shell.Navigate(_shell.Analyze);
    }

    [RelayCommand]
    private void OpenDevProjects() => _shell.Navigate(_shell.DevProjects);

    public void Update(ScanSession s)
    {
        var t = s.Tree;
        HasScan = true;
        ScannedPath = t.RootPath;
        SafeTotalBytes = s.SafeTotalBytes;
        SafeTotalText = SizeFormatter.Format(s.SafeTotalBytes);
        SafeSummary = s.SafeItems.Count == 0
            ? "Nothing is clearly safe to remove here yet. Busy projects and personal files are left for you to decide."
            : $"{s.SafeItems.Count:N0} {(s.SafeItems.Count == 1 ? "item" : "items")} Miu is confident can be removed and rebuilt or re-downloaded.";
        TopSafe.Clear();
        foreach (var (id, v) in s.SafeItems.Take(6))
            TopSafe.Add(new SafeRow(id, t.Parent(id) is var p && p > t.Root ? $"{t.Name(p)} › {t.Name(id)}" : t.Name(id), t.GetFullPath(id), SizeFormatter.Format(t.Size(id)), v.Reason));

        var projects = s.Projects;
        var rebuildable = projects.Sum(p => p.RebuildableBytes);
        var idle = projects.Count(p => p.IdleDays > 90);
        DevSummary = projects.Count == 0
            ? "No code projects found in this scan."
            : $"{projects.Count} projects hold {SizeFormatter.Format(rebuildable)} of rebuildable files · {idle} idle for more than 90 days";
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
                var title = OperatingSystem.IsWindows() ? root.TrimEnd('\\') + (string.IsNullOrEmpty(d.VolumeLabel) ? "" : " " + d.VolumeLabel) : root;
                var kind = d.DriveType switch { DriveType.Network => "Network drive", DriveType.Removable => "Removable drive", _ => "Local drive" } + " · " + d.DriveFormat;
                card = new DriveCard(title, kind, root, frac, SizeFormatter.Format(used) + " used", SizeFormatter.Format(d.AvailableFreeSpace) + " free",
                    low ? "Low space" : "Healthy",
                    new SolidColorBrush(Color.Parse(low ? "#FDF0E1" : "#E6F2E6")), new SolidColorBrush(Color.Parse(low ? "#8A4600" : "#0E700E")),
                    new SolidColorBrush(Color.Parse(low ? "#C25700" : "#005FB8")));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (card is not null) yield return card;
        }
    }
}
