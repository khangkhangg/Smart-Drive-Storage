using Avalonia.Headless.XUnit;
using Miu.App.ViewModels;
using Miu.Data;
using Miu.Dev.Docker;

namespace Miu.App.Tests;

public class DockerUiTests
{
    private const string Df = """
        {"Active":"1","Reclaimable":"512MB","Size":"520MB","TotalCount":"3","Type":"Containers"}
        {"Active":"0","Reclaimable":"8GB","Size":"8GB","TotalCount":"40","Type":"Build Cache"}
        """;

    [AvaloniaFact]
    public async Task Container_prune_asks_first_lists_names_and_is_logged()
    {
        var db = new MiuDatabase(":memory:");
        db.Migrate();
        var actions = new ActionsRepository(db);
        var runner = new Runner();
        var vm = new DockerViewModel(new DockerService(runner), actions);
        await vm.Refresh();

        await vm.PruneContainersCommand.ExecuteAsync(null);
        runner.Calls.Should().NotContain(c => c.StartsWith("container prune"));
        vm.HasPending.Should().BeTrue();
        vm.PendingText.Should().Contain("old-postgres").And.Contain("web-test");

        await vm.ConfirmPendingCommand.ExecuteAsync(null);
        runner.Calls.Should().Contain("container prune -f");
        var logged = actions.Recent(1).Single();
        logged.Kind.Should().Be("delete");
        logged.Origin.Should().Be("docker");
        logged.Items.Select(i => i.OriginalPath).Should().Contain("old-postgres");
    }

    [AvaloniaFact]
    public async Task Cancelling_runs_nothing()
    {
        var db = new MiuDatabase(":memory:");
        db.Migrate();
        var runner = new Runner();
        var vm = new DockerViewModel(new DockerService(runner), new ActionsRepository(db));
        await vm.Refresh();
        await vm.PruneBuildCacheCommand.ExecuteAsync(null);
        vm.CancelPendingCommand.Execute(null);
        vm.HasPending.Should().BeFalse();
        runner.Calls.Should().NotContain(c => c.Contains("prune"));
    }

    private sealed class Runner : IProcessRunner
    {
        public List<string> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct)
        {
            var cmd = string.Join(' ', args);
            Calls.Add(cmd);
            var output = cmd.StartsWith("system df") ? Df : cmd.StartsWith("ps -a") ? "old-postgres\nweb-test\n" : "";
            return Task.FromResult(new ProcessResult(0, output, ""));
        }
    }
}
