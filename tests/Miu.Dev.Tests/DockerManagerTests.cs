using Miu.Dev.Docker;

namespace Miu.Dev.Tests;

public class DockerManagerTests
{
    private const string Ps = """
        {"Command":"\"docker-entrypoint.s…\"","CreatedAt":"2026-10-01 10:00:00 +0700 +07","ID":"aaa111","Image":"mysql:8","Labels":"com.docker.compose.project=shop,com.docker.compose.service=db","Names":"shop-db-1","Ports":"0.0.0.0:3306->3306/tcp, [::]:3306->3306/tcp","State":"running","Status":"Up 2 hours"}
        {"CreatedAt":"2026-09-01 10:00:00 +0700 +07","ID":"bbb222","Image":"redis:7","Labels":"","Names":"cache","Ports":"","State":"exited","Status":"Exited (0) 3 days ago"}
        {"CreatedAt":"2026-09-01 10:00:00 +0700 +07","ID":"ccc333","Image":"nginx","Labels":"com.docker.compose.project=shop","Names":"shop-web-1","Ports":"0.0.0.0:8080->80/tcp","State":"paused","Status":"Up 1 hour (Paused)"}
        not json
        """;

    private const string DfV = """
        {"Images":[{"Containers":"1","CreatedSince":"3 weeks ago","ID":"sha256:111","Repository":"mysql","Size":"590MB","Tag":"8"},{"Containers":"0","CreatedSince":"2 months ago","ID":"sha256:222","Repository":"<none>","Size":"1.2GB","Tag":"<none>"}],
         "Volumes":[{"Driver":"local","Links":"1","Name":"shop_dbdata","Size":"2.3GB"},{"Driver":"local","Links":"0","Name":"old_data","Size":"800MB"}]}
        """;

    [Fact]
    public void Containers_are_grouped_by_compose_project_with_state_and_ports()
    {
        var list = DockerManager.ParseContainers(Ps);
        list.Select(c => c.Name).Should().Equal("shop-db-1", "shop-web-1", "cache");
        var db = list[0];
        (db.Project, db.IsRunning, db.Image).Should().Be(("shop", true, "mysql:8"));
        db.HostPorts.Should().Equal(3306);
        list[1].IsPaused.Should().BeTrue();
        list[1].HostPorts.Should().Equal(8080);
        list[2].IsStopped.Should().BeTrue();
        list[2].Project.Should().BeEmpty();
    }

    [Fact]
    public void Images_and_volumes_come_with_sizes_and_users()
    {
        var (images, volumes) = DockerManager.ParseDfVerbose(DfV);
        images.Select(i => (i.Name, i.Containers)).Should().Equal(("mysql:8", 1), ("<untagged> sha256:222", 0));
        images[1].IsDangling.Should().BeTrue();
        volumes.Select(v => (v.Name, v.Links, v.Size)).Should().Equal(("old_data", 0, "800MB"), ("shop_dbdata", 1, "2.3GB"));
    }

    [Fact]
    public async Task Actions_pass_ids_as_arguments_and_refuse_odd_names()
    {
        var runner = new Runner();
        var m = new DockerManager(runner);
        await m.StartAsync("aaa111");
        await m.PauseAsync("shop-db-1");
        await m.RemoveContainerAsync("cache", force: false);
        await m.RemoveVolumeAsync("old_data");
        await m.LogsAsync("aaa111", 50);
        await m.StopAllAsync(["a1", "b2"]);
        runner.Calls.Should().Equal("start aaa111", "pause shop-db-1", "rm cache", "volume rm old_data", "logs --tail 50 --timestamps aaa111", "stop a1 b2");
        var bad = () => m.RemoveVolumeAsync("x; rm -rf /");
        await bad.Should().ThrowAsync<ArgumentException>();
        var dash = () => m.StopAsync("--all");
        await dash.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task No_docker_means_no_inventory()
    {
        var m = new DockerManager(new Runner { Exit = 127 });
        (await m.ListAsync()).Should().BeNull();
    }

    private sealed class Runner : IProcessRunner
    {
        public int Exit { get; init; }
        public List<string> Calls { get; } = [];
        public Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct)
        {
            Calls.Add(string.Join(' ', args));
            return Task.FromResult(new ProcessResult(Exit, "", ""));
        }
    }
}
