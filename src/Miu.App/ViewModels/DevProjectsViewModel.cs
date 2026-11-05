using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miu.App.Services;
using Miu.Core.Formatting;
using Miu.Dev;

namespace Miu.App.ViewModels;

public sealed record ProjectRow(int NodeId, string Name, string Path, string StacksText, string LastActivityText,
    string BucketText, IBrush BucketBg, IBrush BucketFg, string TotalText, string RebuildableText, double RebuildableFraction,
    string GitText = "", bool GitWarning = false)
{
    public bool HasGit => GitText.Length > 0;
}

public partial class DevProjectsViewModel(MainWindowViewModel shell) : ObservableObject
{
    public ObservableCollection<ProjectRow> Projects { get; } = [];
    public DockerViewModel Docker { get; } = new(shell.Services.Docker, shell.Services.Actions);

    private readonly Dictionary<int, Miu.Dev.Git.GitState> _git = new();
    private int _gitGeneration;

    [ObservableProperty] private string idleCleanText = "";
    [ObservableProperty] private bool hasIdle;

    /// <summary>Opens Review &amp; Clean with one project's rebuildable folders (node_modules, build output…).</summary>
    [RelayCommand]
    private void CleanProject(ProjectRow? row)
    {
        if (row is null || shell.Analyze.Session is not { } s || s.ProjectAt(row.NodeId) is not { } p) return;
        shell.OpenReview(JunkOf(s, [p]), $"Clean {p.Name}", "dev-project");
    }

    [RelayCommand]
    private void CleanIdle()
    {
        if (shell.Analyze.Session is not { } s) return;
        shell.OpenReview(JunkOf(s, s.Projects.Where(p => p.Bucket >= IdleBucket.Idle)), "Clean idle projects", "dev-project");
    }

    /// <summary>
    /// A project's rebuildable folders. Folders holding work git doesn't have are left out; in a project with
    /// uncommitted or unpushed work everything starts unticked, so you confirm on purpose.
    /// </summary>
    private List<Miu.Core.Cleanup.CartItem> JunkOf(ScanSession s, IEnumerable<DetectedProject> projects)
    {
        var items = new List<Miu.Core.Cleanup.CartItem>();
        foreach (var p in projects)
        {
            _git.TryGetValue(p.NodeId, out var git);
            foreach (var c in s.Tree.Children(p.NodeId))
            {
                if (!s.Tree.IsDir(c) || !DevJunkCatalog.IsJunkFolder(s.Tree.Name(c), p.Stacks)) continue;
                var v = s.VerdictFor(c);
                if (v.Verdict == Miu.Core.Rules.Verdict.Protected || s.Tree.Size(c) == 0) continue;
                var full = s.Tree.GetFullPath(c);
                if (git is not null && git.WorkInside(Path.GetRelativePath(git.Root, full).Replace('\\', '/')).Count > 0) continue;
                var (verdict, reason) = git is { HasUncommitted: true } or { HasUnpushed: true }
                    ? (Miu.Core.Rules.Verdict.Review, $"{p.Name} has {git.Describe().ToLowerInvariant()} in git. Tick only if you're sure.")
                    : (v.Verdict, v.Reason);
                items.Add(new Miu.Core.Cleanup.CartItem(full, s.Tree.Name(c), s.Tree.Size(c), verdict, reason));
            }
        }
        return items;
    }

    /// <summary>Asks git about each project in the background and shows ⚠ where work isn't committed or pushed.</summary>
    private async Task LoadGitAsync(ScanSession s)
    {
        var generation = ++_gitGeneration;
        _git.Clear();
        var git = shell.Services.NewGit();
        foreach (var p in s.Projects.Where(p => p.Stacks.Contains("Git")).ToList())
        {
            Miu.Dev.Git.GitCheck check;
            try { check = await git.CheckAsync(p.Path); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException) { continue; }
            if (generation != _gitGeneration) return; // a newer scan replaced this list
            var i = Projects.ToList().FindIndex(r => r.NodeId == p.NodeId);
            if (check.Problem is { } problem)
            {
                if (i >= 0) Projects[i] = Projects[i] with { GitText = "⚠ " + problem, GitWarning = true };
                continue;
            }
            if (check.State is not { } state) continue;
            _git[p.NodeId] = state;
            var warn = state.HasUncommitted || state.HasUnpushed;
            if (i >= 0) Projects[i] = Projects[i] with { GitText = (warn ? "⚠ " : "") + state.Describe(), GitWarning = warn };
        }
    }

    /// <summary>For tests: waits until git has been asked about every project.</summary>
    public Task? GitLoading { get; private set; }

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
        GitLoading = LoadGitAsync(s);
        var idle = s.Projects.Where(p => p.Bucket >= IdleBucket.Idle).ToList();
        HasIdle = idle.Any(p => p.RebuildableBytes > 0);
        IdleCleanText = $"Clean {idle.Count} idle {(idle.Count == 1 ? "project" : "projects")} ({SizeFormatter.Format(idle.Sum(p => p.RebuildableBytes))})";
        SummaryText = HasProjects
            ? $"{Projects.Count} projects · {SizeFormatter.Format(s.Projects.Sum(p => p.RebuildableBytes))} rebuildable · " +
              $"{idle.Count} idle for more than 90 days ({SizeFormatter.Format(idle.Sum(p => p.RebuildableBytes))} you could free)"
            : "No code projects found in this scan.";
    }
}
