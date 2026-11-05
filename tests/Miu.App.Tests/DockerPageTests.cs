using Avalonia.Headless.XUnit;
using Miu.App.Services;
using Miu.App.ViewModels;
using Miu.Dev.Docker;

namespace Miu.App.Tests;

public sealed class DockerPageTests
{
    private sealed class FakeDocker : IProcessRunner
    {
        public List<string> Calls { get; } = [];
        public bool Running { get; set; } = true;
        public Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct)
        {
            var a = string.Join(' ', args);
            Calls.Add(a);
            if (a.StartsWith("ps", StringComparison.Ordinal))
                return Ok($$"""
                    {"ID":"aaa111","Image":"mysql:8","Labels":"com.docker.compose.project=shop","Names":"shop-db-1","Ports":"0.0.0.0:3306->3306/tcp","State":"{{(Running ? "running" : "exited")}}","Status":"Up"}
                    """);
            if (a.StartsWith("system df", StringComparison.Ordinal))
                return Ok("""{"Images":[{"Containers":"1","ID":"sha256:1","Repository":"mysql","Size":"590MB","Tag":"8","CreatedSince":"3 weeks ago"}],"Volumes":[{"Driver":"local","Links":"0","Name":"old_data","Size":"800MB"}]}""");
            if (a.StartsWith("stop", StringComparison.Ordinal)) Running = false;
            if (a.StartsWith("logs", StringComparison.Ordinal)) return Task.FromResult(new ProcessResult(0, "ready for connections", "warning: x"));
            return Ok("");
        }
        private static Task<ProcessResult> Ok(string o) => Task.FromResult(new ProcessResult(0, o, ""));
    }

    [AvaloniaFact]
    public async Task Lists_everything_and_runs_container_actions()
    {
        var fake = new FakeDocker();
        var s = AppServices.Create(":memory:", new CleanupServiceTests.FakeBin());
        s.DockerManager = new DockerManager(fake);
        var vm = new MainWindowViewModel(s) { Onboarding = null };
        var page = vm.DockerPage;
        await page.RefreshCommand.ExecuteAsync(null);
        page.IsAvailable.Should().BeTrue();
        page.Summary.Should().Be("1 containers (1 running) · 1 images · 1 volumes");
        var db = page.Containers.Single();
        (db.StateText, db.CanStop, db.CanStart, db.PortsText).Should().Be(("Running", true, false, "Ports 3306"));

        await db.StopCommand.ExecuteAsync(null);
        fake.Calls.Should().Contain("stop aaa111");
        page.Message.Should().Be("Stopped shop-db-1.");
        page.Containers.Single().CanStart.Should().BeTrue();

        await page.Containers.Single().LogsCommand.ExecuteAsync(null);
        page.LogsText.Should().Contain("ready for connections").And.Contain("warning: x");

        page.Images.Single().CanDelete.Should().BeFalse(); // in use
        var vol = page.Volumes.Single();
        await vol.DeleteCommand.ExecuteAsync(null);
        vol.DeleteText.Should().Be("Delete its data for good?");
        fake.Calls.Should().NotContain("volume rm old_data"); // asks first
        await vol.DeleteCommand.ExecuteAsync(null);
        fake.Calls.Should().Contain("volume rm old_data");
        s.Actions.Recent(5).Single().Items.Single().OriginalPath.Should().Be("volume old_data");
    }

    [AvaloniaFact]
    public async Task Without_docker_it_says_how_to_start_it()
    {
        var s = AppServices.Create(":memory:", new CleanupServiceTests.FakeBin());
        s.DockerManager = new DockerManager(new NoDocker());
        var page = new MainWindowViewModel(s) { Onboarding = null }.DockerPage;
        await page.RefreshCommand.ExecuteAsync(null);
        page.IsUnavailable.Should().BeTrue();
        page.Message.Should().Contain("Start Docker Desktop");
    }

    private sealed class NoDocker : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct) => Task.FromResult(new ProcessResult(127, "", "not found"));
    }
}
