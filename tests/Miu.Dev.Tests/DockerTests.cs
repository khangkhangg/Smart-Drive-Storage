using Miu.Dev.Docker;

namespace Miu.Dev.Tests;

public class DockerTests
{
    private const string DfOutput = """
        {"Active":"3","Reclaimable":"2.104GB (45%)","Size":"4.675GB","TotalCount":"12","Type":"Images"}
        {"Active":"1","Reclaimable":"512.3MB (98%)","Size":"520MB","TotalCount":"5","Type":"Containers"}
        {"Active":"2","Reclaimable":"1.2GB (60%)","Size":"2GB","TotalCount":"4","Type":"Local Volumes"}
        {"Active":"0","Reclaimable":"8.25GB","Size":"8.25GB","TotalCount":"140","Type":"Build Cache"}
        """;

    [Fact]
    public void Parse_df_output()
    {
        var usage = DockerDf.Parse(DfOutput);
        usage.Should().HaveCount(4);
        var images = usage.Single(u => u.Type == "Images");
        images.TotalCount.Should().Be(12);
        images.Active.Should().Be(3);
        images.SizeBytes.Should().Be((long)(4.675 * 1e9));
        images.ReclaimableBytes.Should().Be((long)(2.104 * 1e9));
        usage.Single(u => u.Type == "Build Cache").ReclaimableBytes.Should().Be((long)(8.25 * 1e9));
        usage.Single(u => u.Type == "Containers").ReclaimableBytes.Should().Be((long)(512.3 * 1e6));
    }

    [Theory]
    [InlineData("0B", 0L)]
    [InlineData("1.5kB", 1500L)]
    [InlineData("12MB (3%)", 12_000_000L)]
    [InlineData("2GB", 2_000_000_000L)]
    [InlineData("1.1TB", 1_100_000_000_000L)]
    [InlineData("garbage", 0L)]
    public void ParseSize(string text, long expected) => DockerDf.ParseSize(text).Should().Be(expected);

    [Fact]
    public async Task Missing_docker_returns_null()
    {
        var runner = new FakeRunner { ExitCode = 127 };
        (await new DockerService(runner).GetUsageAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Usage_runs_df_with_json_format()
    {
        var runner = new FakeRunner { Output = DfOutput };
        var usage = await new DockerService(runner).GetUsageAsync();
        usage.Should().HaveCount(4);
        runner.Calls.Single().Should().Equal("system", "df", "--format", "{{json .}}");
    }

    [Fact]
    public async Task Prune_runs_expected_commands()
    {
        var runner = new FakeRunner();
        var docker = new DockerService(runner);
        await docker.PruneBuildCacheAsync();
        await docker.PruneDanglingImagesAsync();
        await docker.PruneStoppedContainersAsync();
        runner.Calls.Select(c => string.Join(' ', c)).Should().Equal(
            "builder prune -f", "image prune -f", "container prune -f");
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public int ExitCode { get; init; }
        public string Output { get; init; } = "";
        public List<string[]> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct)
        {
            file.Should().Be("docker");
            Calls.Add(args.ToArray());
            return Task.FromResult(new ProcessResult(ExitCode, Output, ""));
        }
    }
}
