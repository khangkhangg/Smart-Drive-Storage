using System.Diagnostics;
using Miu.Dev.Docker;
using Miu.Dev.Git;

namespace Miu.Dev.Tests;

public class GitStatusTests
{
    [Fact]
    public void Parses_branch_changes_renames_conflicts_and_untracked()
    {
        var output = string.Join('\0',
            "# branch.oid abc", "# branch.head main", "# branch.upstream origin/main", "# branch.ab +2 -0",
            "1 .M N... 100644 100644 100644 a1 a2 src/app.ts",
            "2 R. N... 100644 100644 100644 b1 b2 R100 docs/new name.md", "docs/old.md",
            "u UU N... 100644 100644 100644 100644 c1 c2 c3 conflict.txt",
            "? notes/todo.txt", "? scratch/", "");
        var s = GitStatus.Parse("/repo", output);
        s.Branch.Should().Be("main");
        s.Ahead.Should().Be(2);
        s.Changed.Should().Equal("src/app.ts", "docs/new name.md", "conflict.txt");
        s.Untracked.Should().Equal("notes/todo.txt", "scratch/");
        s.Describe().Should().Be("3 uncommitted changes · 2 new files not in git · 2 commits not pushed");
        s.WorkInside("docs").Should().Equal("docs/new name.md");
        s.WorkInside("scratch").Should().Equal("scratch/");
        s.WorkInside("node_modules").Should().BeEmpty();
        s.WorkInside("src/ap").Should().BeEmpty(); // a prefix of a name is not a folder
        s.WorkInside("scratch/sub/deeper").Should().Equal("scratch/"); // inside a folder git doesn't know
    }

    [Fact]
    public void No_upstream_means_unknown_ahead()
    {
        GitStatus.Parse("/r", "# branch.head feature\0").Ahead.Should().Be(-1);
        GitStatus.Parse("/r", "# branch.head main\0# branch.ab +0 -3\0").Describe().Should().Be("All committed");
    }

    [Fact]
    public void Risky_repository_settings_are_detected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "miu-gitcfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        try
        {
            File.WriteAllText(Path.Combine(dir, ".git", "config"), "[core]\n\tbare = false\n");
            GitService.RiskyConfig(dir).Should().BeNull();
            File.WriteAllText(Path.Combine(dir, ".git", "config"), "[core]\n\tfsmonitor = /tmp/x.sh\n");
            GitService.RiskyConfig(dir).Should().Be("fsmonitor");
            File.WriteAllText(Path.Combine(dir, ".git", "config"), "[filter \"lfs\"]\n\tclean = run-me\n");
            GitService.RiskyConfig(dir).Should().Be("a file filter");
            File.WriteAllText(Path.Combine(dir, ".git", "config"), "[include]\n\tpath = ../evil\n");
            GitService.RiskyConfig(dir).Should().Be("an include");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Real_git_repository()
    {
        var dir = Path.Combine(Path.GetTempPath(), "miu-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            if (!Git(dir, "init", "-q")) return; // git not installed
            Git(dir, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "--allow-empty", "-m", "init");
            File.WriteAllText(Path.Combine(dir, ".gitignore"), "node_modules/\n");
            Directory.CreateDirectory(Path.Combine(dir, "node_modules", "x"));
            File.WriteAllText(Path.Combine(dir, "node_modules", "x", "i.js"), "x");
            Directory.CreateDirectory(Path.Combine(dir, "dist"));
            File.WriteAllText(Path.Combine(dir, "dist", "hand-written.js"), "work");
            var s = await new GitService(new ProcessRunner()).StatusAsync(dir);
            s.Should().NotBeNull();
            s!.WorkInside("node_modules").Should().BeEmpty(); // ignored
            s.WorkInside("dist").Should().NotBeEmpty();
            GitStatus.FindRoot(Path.Combine(dir, "dist", "hand-written.js")).Should().Be(dir);
            (await new GitService(new ProcessRunner()).StatusAsync(Path.GetTempPath())).Should().BeNull(); // not a repo
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, true);
        }
    }

    private static bool Git(string dir, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}
