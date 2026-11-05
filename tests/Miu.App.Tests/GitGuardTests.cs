using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Miu.App.Services;
using Miu.App.ViewModels;
using Miu.Core.Cleanup;
using Miu.Core.Rules;

namespace Miu.App.Tests;

public sealed class GitGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "miu-gitguard-" + Guid.NewGuid().ToString("N"));
    private string Repo => Path.Combine(_root, "shop-app");

    public GitGuardTests() => Directory.CreateDirectory(Repo);

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private bool InitRepo()
    {
        if (!Git("init", "-q")) return false;
        Write(Path.Combine(Repo, ".gitignore"), "node_modules/\n");
        Write(Path.Combine(Repo, "package.json"), "{}");
        Write(Path.Combine(Repo, "package-lock.json"), "{}");
        Git("add", ".");
        Git("-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "-m", "init");
        Write(Path.Combine(Repo, "node_modules", "lib", "index.js"), new string('x', 5000));
        Write(Path.Combine(Repo, "dist", "hand-written.js"), "my work, not ignored, not committed");
        return true;
    }

    [Fact]
    public async Task Cleaning_never_removes_work_git_does_not_have()
    {
        if (!InitRepo()) return; // git not installed
        var bin = new CleanupServiceTests.FakeBin();
        var s = AppServices.Create(":memory:", bin, secrets: new TelegramServiceTests.MemorySecrets());
        var outcome = await new CleanupService(s).CleanAsync(
        [
            new CartItem(Path.Combine(Repo, "node_modules"), "node_modules", 5000, Verdict.Safe, "deps"),
            new CartItem(Path.Combine(Repo, "dist"), "dist", 30, Verdict.Safe, "build output"),
        ], null, "cart");
        bin.Received.Should().Equal(Path.Combine(Repo, "node_modules"));
        outcome.Failed.Single().Reason.Should().Contain("haven't committed").And.Contain("dist/");
    }

    [AvaloniaFact]
    public async Task Dev_projects_show_git_status_and_ask_before_cleaning_a_project_with_uncommitted_work()
    {
        if (!InitRepo()) return;
        Write(Path.Combine(Repo, "src", "new-feature.ts"), "uncommitted");
        var vm = new MainWindowViewModel(AppServices.Create(":memory:", new CleanupServiceTests.FakeBin(), secrets: new TelegramServiceTests.MemorySecrets()),
            new AutomationTests.FakeNotifier(), _ => new TelegramServiceTests.FakeTelegram());
        await vm.Analyze.ScanPathAsync(_root);
        await vm.DevProjects.GitLoading!;
        var row = vm.DevProjects.Projects.Single(p => p.Name == "shop-app");
        row.GitWarning.Should().BeTrue();
        row.GitText.Should().Contain("new files not in git");

        vm.DevProjects.CleanProjectCommand.Execute(row);
        var review = vm.Review!;
        review.Rows.Should().NotContain(r => r.Item.Name == "dist"); // holds uncommitted work: not offered at all
        review.Rows.Should().Contain(r => r.Item.Name == "node_modules");
        review.Rows.Should().OnlyContain(r => !r.IsChecked); // you confirm on purpose
    }

    private bool Git(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = Repo, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}
