using Avalonia.Headless.XUnit;
using Miu.App.Services;
using Miu.Core.Cleanup;
using Miu.Core.Rules;
using Miu.Platform.Abstractions;

namespace Miu.App.Tests;

public sealed class CleanupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "miu-clean-" + Guid.NewGuid().ToString("N"));

    public CleanupServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a", "big"));
        File.WriteAllBytes(Path.Combine(_root, "a", "big", "x.bin"), new byte[4000]);
        File.WriteAllBytes(Path.Combine(_root, "a", "keep.txt"), new byte[100]);
        File.WriteAllBytes(Path.Combine(_root, "locked.bin"), new byte[500]);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private static CartItem Item(string path, long bytes) => new(path, Path.GetFileName(path), bytes, Verdict.Safe, "test");

    [AvaloniaFact]
    public async Task Clean_logs_action_and_updates_tree()
    {
        var bin = new FakeBin();
        var services = AppServices.Create(":memory:", bin);
        var session = await ScanSession.RunAsync(services, _root, null, CancellationToken.None);
        var cleanup = new CleanupService(services);
        var big = Path.Combine(_root, "a", "big");
        services.Cart.Add(Item(big, 4000));

        var outcome = await cleanup.CleanAsync(services.Cart.Items, session, "app");

        outcome.FreedBytes.Should().Be(4000);
        outcome.Count.Should().Be(1);
        bin.Received.Should().Equal(big);
        services.Actions.Get(outcome.ActionId)!.Items.Single().OriginalPath.Should().Be(big);
        session.Tree.Size(session.Tree.Root).Should().Be(600);
        session.FindNode(big).Should().BeNull();
        services.Cart.Items.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task Failed_items_stay_in_tree_and_cart()
    {
        var bin = new FakeBin { FailNames = ["locked.bin"] };
        var services = AppServices.Create(":memory:", bin);
        var session = await ScanSession.RunAsync(services, _root, null, CancellationToken.None);
        var locked = Path.Combine(_root, "locked.bin");
        services.Cart.Add(Item(locked, 500));

        var outcome = await new CleanupService(services).CleanAsync(services.Cart.Items, session, "app");

        outcome.Count.Should().Be(0);
        outcome.Failed.Single().Path.Should().Be(locked);
        outcome.ActionId.Should().Be(0);
        session.FindNode(locked).Should().NotBeNull();
        services.Cart.Items.Should().ContainSingle();
    }

    [AvaloniaFact]
    public async Task Undo_calls_restore_and_marks_undone()
    {
        var bin = new FakeBin();
        var services = AppServices.Create(":memory:", bin);
        var session = await ScanSession.RunAsync(services, _root, null, CancellationToken.None);
        var cleanup = new CleanupService(services);
        var outcome = await cleanup.CleanAsync([Item(Path.Combine(_root, "a", "big"), 4000)], session, "app");

        var restored = await cleanup.UndoAsync(outcome.ActionId);

        restored.Restored.Should().ContainSingle();
        bin.Restored.Should().ContainSingle();
        services.Actions.Get(outcome.ActionId)!.Undone.Should().BeTrue();
        (await cleanup.UndoAsync(outcome.ActionId)).Restored.Should().BeEmpty(); // only once
    }

    [AvaloniaFact]
    public async Task Protected_paths_are_refused_even_if_in_the_list()
    {
        var bin = new FakeBin();
        var services = AppServices.Create(":memory:", bin);
        var session = await ScanSession.RunAsync(services, _root, null, CancellationToken.None);
        var system = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.Windows) : "/usr/lib";
        var outcome = await new CleanupService(services).CleanAsync([Item(system, 1)], session, "app");
        bin.Received.Should().BeEmpty();
        outcome.Failed.Should().ContainSingle();
    }

    internal sealed class FakeBin : IRecycleBin
    {
        public List<string> Received { get; } = [];
        public List<string> Restored { get; } = [];
        public string[] FailNames { get; init; } = [];
        public bool IsSupported => true;

        public Task<RecycleResult> RecycleAsync(IReadOnlyList<string> paths, IProgress<int>? progress, CancellationToken ct)
        {
            Received.AddRange(paths);
            var fail = paths.Where(p => FailNames.Contains(Path.GetFileName(p))).ToList();
            var ok = paths.Except(fail).ToList();
            return Task.FromResult(new RecycleResult(
                ok.Select(p => new RecycledItem(p, "/trash/" + Path.GetFileName(p), File.Exists(p) ? new FileInfo(p).Length : 4000)).ToList(),
                fail.Select(p => new FailedItem(p, "It is in use")).ToList()));
        }

        public Task<RestoreResult> RestoreAsync(IReadOnlyList<RecycledItem> items, CancellationToken ct)
        {
            Restored.AddRange(items.Select(i => i.OriginalPath));
            return Task.FromResult(new RestoreResult(items.Select(i => i.OriginalPath).ToList(), []));
        }
    }
}
