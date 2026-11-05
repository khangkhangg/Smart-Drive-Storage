using Miu.Platform.Abstractions;
using Miu.Platform.Portable;

namespace Miu.Platform.Tests;

public sealed class ManagedScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "miu-scan-" + Guid.NewGuid().ToString("N"));

    public ManagedScannerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a", "b"));
        File.WriteAllBytes(Path.Combine(_root, "a", "b", "c.txt"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(_root, "a", "d.bin"), new byte[500]);
        File.WriteAllBytes(Path.Combine(_root, "e.txt"), new byte[20]);
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
            foreach (var d in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
                try { File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }
        Directory.Delete(_root, true);
    }

    [Fact]
    public async Task Scans_sizes_and_counts()
    {
        var tree = await new ManagedScanner().ScanAsync(_root, null, CancellationToken.None);
        tree.Size(tree.Root).Should().Be(1520);
        tree.FileCount(tree.Root).Should().Be(3);
        var a = tree.FindChild(tree.Root, "a")!.Value;
        tree.Size(a).Should().Be(1500);
        tree.Children(tree.Root).Select(tree.Name).Should().Equal("a", "e.txt");
    }

    [Fact]
    public async Task Skips_symlinked_directories()
    {
        Directory.CreateSymbolicLink(Path.Combine(_root, "loop"), _root);
        var tree = await new ManagedScanner().ScanAsync(_root, null, CancellationToken.None);
        tree.Size(tree.Root).Should().Be(1520);
        var loop = tree.FindChild(tree.Root, "loop");
        if (loop is not null) tree.Children(loop.Value).Should().BeEmpty();
    }

    [Fact]
    public async Task Skips_unreadable_directory_without_throwing()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return; // root can read everything
#pragma warning disable CA1416
        var locked = Path.Combine(_root, "locked");
        Directory.CreateDirectory(locked);
        File.WriteAllBytes(Path.Combine(locked, "x"), new byte[7]);
        File.SetUnixFileMode(locked, UnixFileMode.None);
#pragma warning restore CA1416
        var tree = await new ManagedScanner().ScanAsync(_root, null, CancellationToken.None);
        tree.Size(tree.Root).Should().Be(1520);
    }

    [Fact]
    public async Task Honors_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => new ManagedScanner().ScanAsync(_root, null, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Reports_progress()
    {
        var reports = new List<ScanProgress>();
        var progress = new SyncProgress<ScanProgress>(reports.Add);
        await new ManagedScanner().ScanAsync(_root, progress, CancellationToken.None);
        reports.Should().NotBeEmpty();
        reports[^1].Files.Should().Be(3);
        reports[^1].Bytes.Should().Be(1520);
    }

    private sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) { lock (this) action(value); }
    }
}
