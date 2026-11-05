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

    [AvaloniaFact]
    public async Task Partial_undo_keeps_the_rest_undoable()
    {
        var bin = new FakeBin();
        var services = AppServices.Create(":memory:", bin);
        var session = await ScanSession.RunAsync(services, _root, null, CancellationToken.None);
        var cleanup = new CleanupService(services);
        var big = Path.Combine(_root, "a", "big");
        var locked = Path.Combine(_root, "locked.bin");
        var outcome = await cleanup.CleanAsync([Item(big, 4000), Item(locked, 500)], session, "app");
        bin.RestoreFailNames = ["locked.bin"];

        var first = await cleanup.UndoAsync(outcome.ActionId);
        first.Restored.Should().Equal(big);
        var action = services.Actions.Get(outcome.ActionId)!;
        action.Undone.Should().BeFalse();
        action.Items.Single().OriginalPath.Should().Be(locked);

        bin.RestoreFailNames = [];
        (await cleanup.UndoAsync(outcome.ActionId)).Restored.Should().Equal(locked);
        services.Actions.Get(outcome.ActionId)!.Undone.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task Cart_items_inside_cleaned_folders_and_missing_items_are_dropped()
    {
        var bin = new FakeBin();
        var services = AppServices.Create(":memory:", bin);
        var session = await ScanSession.RunAsync(services, _root, null, CancellationToken.None);
        var a = Path.Combine(_root, "a");
        services.Cart.Add(Item(Path.Combine(_root, "gone.bin"), 1));
        var outcome = await new CleanupService(services).CleanAsync([Item(a, 4100), Item(Path.Combine(_root, "gone.bin"), 1)], session, "app");
        outcome.Count.Should().Be(1);
        services.Cart.Items.Should().BeEmpty();
    }

    internal sealed class FakeBin : IRecycleBin
    {
        public List<string> Received { get; } = [];
        public List<string> Restored { get; } = [];
        public string[] FailNames { get; init; } = [];
        public string[] RestoreFailNames { get; set; } = [];
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
            var fail = items.Where(i => RestoreFailNames.Contains(Path.GetFileName(i.OriginalPath))).ToList();
            var ok = items.Except(fail).Select(i => i.OriginalPath).ToList();
            Restored.AddRange(ok);
            return Task.FromResult(new RestoreResult(ok, fail.Select(f => new FailedItem(f.OriginalPath, "exists")).ToList()));
        }
    }
}
