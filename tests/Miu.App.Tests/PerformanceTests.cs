using System.Diagnostics;
using Miu.App.Services;
using Xunit.Abstractions;

namespace Miu.App.Tests;

/// <summary>
/// Scan + verdicts + project detection on a synthetic tree (many small files, nested node_modules, build output).
/// Always runs a small version with a generous limit; set MIU_BENCH=1 for the large one (prints timings).
/// </summary>
public sealed class PerformanceTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "miu-perf-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private int Build(int projects, int filesPerFolder)
    {
        var n = 0;
        for (var p = 0; p < projects; p++)
        {
            var proj = Path.Combine(_root, "code", $"app-{p}");
            Directory.CreateDirectory(proj);
            File.WriteAllText(Path.Combine(proj, "package.json"), "{}");
            File.WriteAllText(Path.Combine(proj, "package-lock.json"), "{}");
            foreach (var dir in new[] { "src", "src/components", "node_modules/react/lib", "node_modules/lodash", "dist", "docs" })
            {
                var d = Path.Combine(proj, dir);
                Directory.CreateDirectory(d);
                for (var f = 0; f < filesPerFolder; f++) { File.WriteAllText(Path.Combine(d, $"f{f}.js"), "x"); n++; }
            }
        }
        return n;
    }

    [Fact]
    public async Task Scanning_and_judging_stays_fast()
    {
        var big = Environment.GetEnvironmentVariable("MIU_BENCH") == "1";
        var files = Build(big ? 400 : 40, big ? 80 : 40);
        var s = AppServices.Create(":memory:", new CleanupServiceTests.FakeBin(), secrets: new TelegramServiceTests.MemorySecrets());
        var sw = Stopwatch.StartNew();
        var session = await ScanSession.RunAsync(s, _root, null, CancellationToken.None);
        sw.Stop();
        output.WriteLine($"{files:N0} files: scan + verdicts + projects in {sw.Elapsed.TotalSeconds:0.00} s ({files / Math.Max(0.001, sw.Elapsed.TotalSeconds):N0} files/s)");
        session.Projects.Should().HaveCount(big ? 400 : 40);
        session.Projects.Sum(p => p.RebuildableBytes).Should().BeGreaterThan(0);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(big ? 120 : 20));
        var mem = GC.GetTotalMemory(forceFullCollection: true);
        output.WriteLine($"managed memory after scan: {mem / (1 << 20)} MB");
    }
}
