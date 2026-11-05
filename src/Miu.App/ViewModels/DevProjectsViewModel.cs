using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miu.App.Services;
using Miu.Core.Formatting;
using Miu.Dev;

namespace Miu.App.ViewModels;

public sealed record ProjectRow(int NodeId, string Name, string Path, string StacksText, string LastActivityText,
    string BucketText, IBrush BucketBg, IBrush BucketFg, string TotalText, string RebuildableText, double RebuildableFraction);

public partial class DevProjectsViewModel(MainWindowViewModel shell) : ObservableObject
{
    public ObservableCollection<ProjectRow> Projects { get; } = [];

    [ObservableProperty] private string summaryText = "Scan a folder with your code (for example your Projects or Code folder) to find rebuildable files.";
    [ObservableProperty] private bool hasProjects;

    [RelayCommand]
    private void Open(ProjectRow? row)
    {
        if (row is null) return;
        shell.Analyze.ShowNode(row.NodeId);
        shell.Navigate(shell.Analyze);
    }

    public void Update(ScanSession s)
    {
        Projects.Clear();
        var max = Math.Max(1, s.Projects.Count == 0 ? 1 : s.Projects.Max(p => p.RebuildableBytes));
        foreach (var p in s.Projects)
        {
            var (bg, fg, text) = Palette.ForBucket(p.Bucket);
            Projects.Add(new ProjectRow(p.NodeId, p.Name, p.Path, string.Join(" · ", p.Stacks),
                "Last activity " + HumanTime.Ago(p.LastActivityUtc, s.NowUtc), text, bg, fg,
                SizeFormatter.Format(p.TotalBytes), SizeFormatter.Format(p.RebuildableBytes), (double)p.RebuildableBytes / max));
        }
        HasProjects = Projects.Count > 0;
        var idle = s.Projects.Where(p => p.Bucket >= IdleBucket.Idle).ToList();
        SummaryText = HasProjects
            ? $"{Projects.Count} projects · {SizeFormatter.Format(s.Projects.Sum(p => p.RebuildableBytes))} rebuildable · " +
              $"{idle.Count} idle for more than 90 days ({SizeFormatter.Format(idle.Sum(p => p.RebuildableBytes))} you could free)"
            : "No code projects found in this scan.";
    }
}
