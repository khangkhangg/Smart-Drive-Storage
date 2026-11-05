using Miu.Core.Rules;
using Miu.Platform.Abstractions;
using Miu.Platform.Portable;

namespace Miu.Platform.Tests;

public sealed class SafeRecyclerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "miu-safe-" + Guid.NewGuid().ToString("N"));
    private readonly FakeBin _inner = new();

    public SafeRecyclerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "ok"));
        File.WriteAllText(Path.Combine(_root, "ok", "a.txt"), "x");
    }

    public void Dispose() => Directory.Delete(_root, true);

    private SafeRecycler Sut(params string[] protectedTrees) =>
        new(_inner, new ProtectedPathGuard(protectedTrees, [], PathNormalizer.Current));

    [Fact]
    public async Task Protected_paths_never_reach_inner()
    {
        var prot = Path.Combine(_root, "ok");
        var r = await Sut(prot).RecycleAsync([Path.Combine(prot, "a.txt")], null, CancellationToken.None);
        _inner.Received.Should().BeEmpty();
        r.Failed.Single().Reason.Should().Contain("protected");
    }

    [Fact]
    public async Task Link_into_protected_is_refused()
    {
        if (OperatingSystem.IsWindows()) return;
        var prot = Path.Combine(_root, "system");
        Directory.CreateDirectory(prot);
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, prot);
        var r = await Sut(prot).RecycleAsync([link], null, CancellationToken.None);
        _inner.Received.Should().BeEmpty();
        r.Failed.Should().ContainSingle();
    }

    [Fact]
    public async Task Missing_and_empty_paths_reported()
    {
        var r = await Sut().RecycleAsync([Path.Combine(_root, "gone"), "  "], null, CancellationToken.None);
        _inner.Received.Should().BeEmpty();
        r.Failed.Should().HaveCount(2);
    }

    [Fact]
    public async Task Allowed_paths_pass_and_inner_failures_merge()
    {
        var ok = Path.Combine(_root, "ok", "a.txt");
        _inner.FailNext = true;
        var r = await Sut(Path.Combine(_root, "nothing-here")).RecycleAsync([ok, Path.Combine(_root, "gone")], null, CancellationToken.None);
        _inner.Received.Should().Equal(ok);
        r.Failed.Should().HaveCount(2);
    }

    private sealed class FakeBin : IRecycleBin
    {
        public List<string> Received { get; } = [];
        public bool FailNext { get; set; }
        public bool IsSupported => true;

        public Task<RecycleResult> RecycleAsync(IReadOnlyList<string> paths, IProgress<int>? progress, CancellationToken ct)
        {
            Received.AddRange(paths);
            return Task.FromResult(FailNext
                ? new RecycleResult([], paths.Select(p => new FailedItem(p, "in use")).ToList())
                : new RecycleResult(paths.Select(p => new RecycledItem(p, "/trash/" + Path.GetFileName(p), 1)).ToList(), []));
        }

        public Task<RestoreResult> RestoreAsync(IReadOnlyList<RecycledItem> items, CancellationToken ct) =>
            Task.FromResult(new RestoreResult(items.Select(i => i.OriginalPath).ToList(), []));
    }
}
