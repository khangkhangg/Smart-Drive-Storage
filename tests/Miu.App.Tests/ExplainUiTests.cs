using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Miu.App.Services;
using Miu.App.ViewModels;
using Miu.Core.Rules;
using Miu.Dev.Git;

namespace Miu.App.Tests;

public sealed class ExplainUiTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "miu-explain-" + Guid.NewGuid().ToString("N"));

    public ExplainUiTests()
    {
        void F(string rel, int bytes, string? text = null)
        {
            var p = Path.Combine(_root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            if (text is not null) File.WriteAllText(p, text);
            else File.WriteAllBytes(p, new byte[bytes]);
        }
        F("Miu Music Player/.claude/worktrees/frosty-sammet/.git", 0, "gitdir: ../../../.git/worktrees/frosty-sammet");
        F("Miu Music Player/.claude/worktrees/frosty-sammet/src/main.rs", 50_000);
        F("web/package.json", 10, "{}");
        F("web/node_modules/react/index.js", 30_000);
        F("mysql/binlog.000001", 40_000);
        F("mysql/binlog.000002", 20_000);
        F("notes.txt", 0, string.Join('\n', Enumerable.Range(1, 500).Select(i => $"line {i}")));
        F("blob.bin", 0, "abc\0def");
    }

    public void Dispose() => Directory.Delete(_root, true);

    private async Task<MainWindowViewModel> Shell()
    {
        var vm = new MainWindowViewModel(AppServices.Create(":memory:", new CleanupServiceTests.FakeBin())) { Onboarding = null };
        await vm.Analyze.ScanPathAsync(_root);
        return vm;
    }

    private static NodeItemViewModel Open(AnalyzeViewModel a, params string[] names)
    {
        NodeItemViewModel item = null!;
        foreach (var n in names)
        {
            item = a.Items.Single(i => i.Name == n);
            if (n != names[^1]) a.OpenCommand.Execute(item);
        }
        a.Selected = item;
        return item;
    }

    [AvaloniaFact]
    public async Task Selecting_a_known_item_shows_what_it_is_and_how_to_remove_it()
    {
        var vm = await Shell();
        var a = vm.Analyze;
        Open(a, "web", "node_modules");
        a.HasExplanation.Should().BeTrue();
        a.Explanation!.Title.Should().Contain("node_modules");
        a.Explanation.SafetyLabel.Should().Be("Safe to delete");
        a.Explanation.Commands.Should().Contain(c => c.Text == "npm ci");

        a.GoToCrumbCommand.Execute(a.Breadcrumbs[0]);
        Open(a, "mysql", "binlog.000001");
        a.Explanation!.Title.Should().Contain("binary log");
        a.Explanation.SafetyLabel.Should().Be("Remove it with its tool");
        a.Explanation.Commands.Should().Contain(c => c.Text.Contains("PURGE BINARY LOGS"));
        a.Selected!.Verdict.Verdict.Should().Be(Verdict.Keep); // never cleaned by hand
    }

    [AvaloniaFact]
    public async Task A_worktree_is_named_after_its_project_and_its_commands_use_its_path()
    {
        var vm = await Shell();
        var a = vm.Analyze;
        var wt = Open(a, "Miu Music Player", ".claude", "worktrees", "frosty-sammet");
        wt.Reason.Should().StartWith("Git worktree: extra checkout of Miu Music Player.");
        a.Explanation!.Title.Should().Be("Git worktree: extra checkout of Miu Music Player");
        a.Explanation.Commands.First().Text.Should().Be($"git worktree remove \"{wt.FullPath}\"");
    }

    [Theory]
    [InlineData(1, false, "Branch main · 1 commit not pushed. Removing it would lose that work.")]
    [InlineData(-1, false, "Branch main · not pushed anywhere: its commits may exist only here. Removing it would lose that work.")]
    [InlineData(0, false, "Branch main · pushed. Nothing here is lost if you remove it.")]
    [InlineData(0, true, "Branch main · pushed · 1 files not committed. Removing it would lose that work.")]
    public void Worktree_git_text(int ahead, bool dirty, string expected) =>
        ExplanationViewModel.Describe(new GitCheck(new GitState("/r", "main", dirty ? ["a.txt"] : [], [], ahead))).Should().Be(expected);

    [AvaloniaFact]
    public async Task Ctrl_click_selects_several_and_adds_them_together_leaving_out_kept_items()
    {
        var vm = await Shell();
        var a = vm.Analyze;
        Open(a, "mysql", "binlog.000001");
        a.GoToCrumbCommand.Execute(a.Breadcrumbs[0]);
        var notes = a.Items.Single(i => i.Name == "notes.txt");
        var blob = a.Items.Single(i => i.Name == "blob.bin");
        a.SetSelection([notes, blob]);
        a.IsMultiSelection.Should().BeTrue();
        a.MultiTitle.Should().Be("2 items selected");
        a.AddSelectedText.Should().StartWith("Add 2 to cleanup cart");
        a.AddSelectedToCartCommand.Execute(null);
        vm.Services.Cart.Items.Select(i => i.Name).Should().BeEquivalentTo("notes.txt", "blob.bin");
        a.AddSelectedText.Should().Be("Nothing to add");
        a.MultiNote.Should().Contain("2 already in the cart");

        // Kept items (binlogs) are left out of a multi-add.
        a.OpenCommand.Execute(a.Items.Single(i => i.Name == "mysql"));
        a.SetSelection(a.Items);
        a.AddSelectedText.Should().Be("Nothing to add");
        a.MultiNote.Should().Contain("2 you keep or Miu protects");
    }

    [Fact]
    public void Quick_preview_shows_text_start_details_for_binary_and_folders()
    {
        var text = PreviewContent.For(Path.Combine(_root, "notes.txt"), "notes.txt", false, 3000, "changed today", null, null);
        text.Kind.Should().Be(PreviewKind.Text);
        text.Text.Should().StartWith("line 1\nline 2").And.EndWith("…");
        text.Text.Split('\n').Length.Should().Be(PreviewContent.MaxTextLines + 1);

        var bin = PreviewContent.For(Path.Combine(_root, "blob.bin"), "blob.bin", false, 7, "changed today", null, null);
        bin.Kind.Should().Be(PreviewKind.Details);
        bin.Facts.Should().Contain(f => f.Contains("Search the web"));

        var dir = PreviewContent.For(_root, "x", true, 10, "changed today", "A folder", [("a", 5, false)]);
        dir.Kind.Should().Be(PreviewKind.Details);
        dir.Facts.Should().Contain(f => f.Contains("a · 5 B"));
    }

    [Fact]
    public void The_web_search_never_contains_the_path() =>
        ExplanationViewModel.SearchUrlFor(Miu.Core.Explain.Explainer.SearchQuery("binlog.000001", false, null))
            .Should().StartWith("https://www.google.com/search?q=").And.NotContain("mysql%2F").And.Contain("binlog.000001");

    [AvaloniaFact]
    public async Task Screenshot_of_the_what_is_this_card()
    {
        if (Environment.GetEnvironmentVariable("MIU_SCREENSHOT_DIR") is not { Length: > 0 } outDir) return;
        var vm = await Shell();
        vm.GoToPageCommand.Execute("2");
        Open(vm.Analyze, "Miu Music Player", ".claude", "worktrees", "frosty-sammet");
        vm.Analyze.Explanation!.GitText = "Branch claude/frosty-sammet · 3 commits not pushed. Removing it would lose that work.";
        var window = new Miu.App.Views.MainWindow { DataContext = vm, Width = 1500, Height = 1000 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()!.Save(Path.Combine(outDir, "what-is-this.png"));
        window.Close();
    }
}
