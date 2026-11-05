using FluentAssertions;
using Miu.Core.Explain;
using Miu.Core.Rules;

namespace Miu.Core.Tests;

public class ExplainerTests
{
    private static readonly Explainer Kb = Explainer.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "rules", "explain"), caseInsensitive: true);

    [Fact]
    public void The_knowledge_files_load_and_every_entry_is_complete()
    {
        Kb.Entries.Count.Should().BeGreaterThan(60);
        Kb.Entries.Select(e => e.Id).Should().OnlyHaveUniqueItems();
        foreach (var e in Kb.Entries)
        {
            e.Title.Should().NotBeNullOrWhiteSpace(e.Id);
            e.What.Should().NotBeNullOrWhiteSpace(e.Id);
            e.SafetyText.Should().NotBeNullOrWhiteSpace(e.Id);
            e.Match.IsEmpty.Should().BeFalse(e.Id);
            // Patterns must compile.
            if (e.Match.Path is { } p) Glob.IsMatch(p, "/x", true);
            foreach (var n in e.Match.Names ?? []) Glob.IsMatch(n, "x", true);
            // "Use the tool" entries say which tool.
            if (e.Safety == Safety.UseTool) ((e.Commands?.Length ?? 0) + (e.HowTo?.Length ?? 0)).Should().BePositive(e.Id);
        }
    }

    [Theory]
    [InlineData(@"C:\Projects\Miu Music Player\.claude\worktrees\frosty-sammet-115bb4", "frosty-sammet-115bb4", true, "git-worktree", Safety.Careful)]
    [InlineData(@"C:\Projects\app\node_modules", "node_modules", true, "node-modules", Safety.Safe)]
    [InlineData(@"C:\ProgramData\MySQL\MySQL Server 8.0\Data\binlog.000123", "binlog.000123", false, "mysql-binlog", Safety.UseTool)]
    [InlineData("/var/lib/mysql/mysql-bin.000004", "mysql-bin.000004", false, "mysql-binlog", Safety.UseTool)]
    [InlineData(@"C:\pagefile.sys", "pagefile.sys", false, "pagefile", Safety.Never)]
    [InlineData(@"C:\hiberfil.sys", "hiberfil.sys", false, "hiberfil", Safety.UseTool)]
    [InlineData("/Users/k/Pictures/.DS_Store", ".DS_Store", false, "ds-store", Safety.Safe)]
    [InlineData(@"C:\Users\k\AppData\Local\Docker\wsl\disk\docker_data.vhdx", "docker_data.vhdx", false, "docker-desktop-disk", Safety.Never)]
    [InlineData(@"C:\Users\k\AppData\Local\Packages\CanonicalGroupLimited.Ubuntu_79rhkp1fndgsc\LocalState\ext4.vhdx", "ext4.vhdx", false, "wsl-disk", Safety.Never)]
    [InlineData(@"C:\Projects\x\.git", ".git", true, "git-dir", Safety.Never)]
    [InlineData(@"C:\Users\k\AppData\Local\npm-cache", "npm-cache", true, "npm-cache", Safety.UseTool)]
    [InlineData(@"C:\Windows\WinSxS", "WinSxS", true, "winsxs", Safety.UseTool)]
    [InlineData(@"C:\Windows\Installer", "Installer", true, "windows-installer", Safety.Never)]
    [InlineData("/home/k/.cache/huggingface", "huggingface", true, "hf-cache", Safety.UseTool)]
    [InlineData("/home/k/app/server.log", "server.log", false, "log-files", Safety.Safe)]
    public void Finds_the_right_explanation(string path, string name, bool isDir, string id, Safety safety)
    {
        var e = Kb.Find(path, name, isDir, childExists: n => n == ".git");
        e.Should().NotBeNull();
        e!.Id.Should().Be(id);
        e.Safety.Should().Be(safety);
    }

    [Fact]
    public void Markers_are_required_where_an_entry_asks_for_them()
    {
        // "target" is Rust output only next to Cargo.toml.
        Kb.Find("/p/target", "target", true, siblingExists: n => n == "Cargo.toml")!.Id.Should().Be("rust-target");
        Kb.Find("/p/target", "target", true, siblingExists: n => n == "pom.xml")!.Id.Should().Be("java-target");
        Kb.Find("/p/target", "target", true, siblingExists: _ => false).Should().BeNull();
        // A folder in .claude/worktrees that isn't a checkout (no .git) isn't called a worktree.
        Kb.Find("/p/.claude/worktrees/x", "x", true, childExists: _ => false).Should().BeNull();
    }

    [Fact]
    public void Web_search_uses_the_name_never_the_path()
    {
        var q = Explainer.SearchQuery("binlog.000123", false, null);
        q.Should().Contain("binlog.000123").And.NotContain("/").And.NotContain("\\");
        Explainer.SearchQuery("x", true, Kb.Entries.First(e => e.Id == "mysql-binlog")).Should().Contain("PURGE BINARY LOGS");
    }

    [Theory]
    [InlineData(@"C:\Projects\Miu Music Player\.claude\worktrees\frosty", "Git worktree: extra checkout of Miu Music Player.")]
    [InlineData("/home/k/code/shop/.worktrees/feature-x", "Git worktree: extra checkout of shop.")]
    [InlineData("/x", "Git worktree: extra checkout of a project.")]
    public void Rule_reasons_name_the_project(string path, string expected) =>
        RuleEngine.Fill("Git worktree: extra checkout of {project}.", path).Should().Be(expected);

    [Fact]
    public void Builtin_rules_flag_worktrees_and_binlogs()
    {
        var rules = RuleLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "rules", "builtin"));
        var engine = new RuleEngine(rules, ProtectedPathGuard.ForUnix("/nohome"), caseInsensitive: true);
        var now = DateTime.UtcNow;
        var wt = engine.Evaluate(new ItemFacts("/home/k/Miu Music Player/.claude/worktrees/frosty", "frosty", true, 1, now, now, null, _ => false, n => n == ".git"));
        wt.RuleId.Should().Be("dev.git-worktree");
        wt.Verdict.Should().Be(Verdict.Review);
        wt.Reason.Should().StartWith("Git worktree: extra checkout of Miu Music Player.");
        var bin = engine.Evaluate(new ItemFacts("/nohome/mysql-data/binlog.000042", "binlog.000042", false, 1, now, now, null, _ => false));
        bin.RuleId.Should().Be("db.mysql-binlog");
        bin.Verdict.Should().Be(Verdict.Keep);
    }
}
