using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miu.App.Services;
using Miu.Core.Explain;
using Miu.Core.Rules;

namespace Miu.App.ViewModels;

public sealed record ExplainStep(int Number, string Text);

/// <summary>A command shown in the "What is this?" card, with the item's path filled in.</summary>
public sealed record ExplainCommandRow(string Label, string Text);

/// <summary>The "What is this?" card: what an item is, whether it's safe to delete, and how to remove it properly.</summary>
public sealed partial class ExplanationViewModel : ObservableObject
{
    public ExplanationViewModel(Explanation e, string fullPath, string name, bool isDir)
    {
        Entry = e;
        FullPath = fullPath;
        Name = name;
        IsDir = isDir;
        Steps = (e.HowTo ?? []).Select((t, i) => new ExplainStep(i + 1, t)).ToList();
        Commands = (e.Commands ?? []).Select(c => new ExplainCommandRow(c.Label, Fill(c.Text, fullPath))).ToList();
        IsWorktree = e.Id == "git-worktree";
        if (IsWorktree) GitText = "Checking its branch with git…";
    }

    public Explanation Entry { get; }
    public string FullPath { get; }
    public string Name { get; }
    public bool IsDir { get; }

    public string Title => IsWorktree ? $"Git worktree: extra checkout of {RuleEngine.Fill("{project}", FullPath)}" : Entry.Title;
    public string What => Entry.What;
    public string? WhyBig => Entry.WhyBig;
    public bool HasWhyBig => !string.IsNullOrEmpty(Entry.WhyBig);
    public string SafetyText => Entry.SafetyText;
    public IReadOnlyList<ExplainStep> Steps { get; }
    public bool HasSteps => Steps.Count > 0;
    public IReadOnlyList<ExplainCommandRow> Commands { get; }
    public bool HasCommands => Commands.Count > 0;
    public bool IsWorktree { get; }

    /// <summary>Worktrees: the branch and whether it's pushed (filled in by <see cref="CheckGitAsync"/>).</summary>
    [ObservableProperty] private string gitText = "";

    public string SafetyLabel => Entry.Safety switch
    {
        Safety.Safe => "Safe to delete",
        Safety.UseTool => "Remove it with its tool",
        Safety.Careful => "Check first",
        _ => "Don't delete",
    };

    public IBrush SafetyBg => Palette.ForVerdict(AsVerdict).Bg;
    public IBrush SafetyFg => Palette.ForVerdict(AsVerdict).Fg;

    private Verdict AsVerdict => Entry.Safety switch
    {
        Safety.Safe => Verdict.Safe,
        Safety.Never => Verdict.Protected,
        _ => Verdict.Review,
    };

    /// <summary>Fills "&lt;path&gt;" in a command with the item's own path.</summary>
    public static string Fill(string command, string fullPath) => command.Replace("<path>", fullPath, StringComparison.Ordinal);

    /// <summary>The web search for this item. Only the name goes out, never the path.</summary>
    public string SearchUrl => SearchUrlFor(Explainer.SearchQuery(Name, IsDir, Entry));

    public static string SearchUrlFor(string query) => "https://www.google.com/search?q=" + Uri.EscapeDataString(query);

    [RelayCommand]
    private void Search() => ShellIntegration.Open(SearchUrl);

    /// <summary>For a worktree: which branch it's on and whether its work exists anywhere else.</summary>
    public async Task CheckGitAsync(Miu.Dev.Git.GitService git)
    {
        if (!IsWorktree) return;
        try
        {
            var check = await git.CheckAsync(FullPath);
            GitText = Describe(check);
        }
        catch (Exception ex) { GitText = "Couldn't check with git: " + ex.Message; }
    }

    public static string Describe(Miu.Dev.Git.GitCheck check)
    {
        if (check.GitMissing) return "git isn't installed, so Miu can't tell whether this branch is pushed.";
        if (check.Problem is { } p) return p;
        if (check.State is not { } s) return "";
        var branch = s.Branch is { Length: > 0 } b ? $"Branch {b}" : "No branch (detached)";
        var pushed = s.HasNoUpstream ? "not pushed anywhere: its commits may exist only here"
            : s.HasUnpushed ? $"{s.Ahead} {(s.Ahead == 1 ? "commit" : "commits")} not pushed"
            : "pushed";
        var work = s.HasUncommitted ? $" · {s.Changed.Count + s.Untracked.Count} files not committed" : "";
        var verdict = !s.HasNoUpstream && !s.HasUnpushed && !s.HasUncommitted
            ? " Nothing here is lost if you remove it." : " Removing it would lose that work.";
        return $"{branch} · {pushed}{work}.{verdict}";
    }
}
