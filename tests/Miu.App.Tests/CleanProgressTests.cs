using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Miu.App.Services;
using Miu.App.ViewModels;
using Miu.Core.Cleanup;
using Miu.Core.Rules;
using Miu.Platform.Abstractions;

namespace Miu.App.Tests;

/// <summary>A Recycle Bin that holds one item until the test lets it go (to look at progress mid-clean).</summary>
internal sealed class GatedBin(string holdName) : IRecycleBin
{
    public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<string> Received { get; } = [];
    public bool IsSupported => true;

    public async Task<RecycleResult> RecycleAsync(IReadOnlyList<string> paths, IProgress<int>? progress, CancellationToken ct)
    {
        foreach (var p in paths)
        {
            if (Path.GetFileName(p) == holdName) { Held.TrySetResult(); await Release.Task; }
            Received.Add(p);
        }
        return new RecycleResult(paths.Select(p => new RecycledItem(p, "/trash/" + Path.GetFileName(p), 10)).ToList(), []);
    }

    public Task<RestoreResult> RestoreAsync(IReadOnlyList<RecycledItem> items, CancellationToken ct) => Task.FromResult(new RestoreResult([], []));
}

public sealed class CleanProgressTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "miu-progress-" + Guid.NewGuid().ToString("N"));

    public CleanProgressTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        ReviewViewModel.SlowAfter = TimeSpan.FromSeconds(20);
        Directory.Delete(_root, true);
    }

    private CartItem Item(string name, long bytes)
    {
        var p = Path.Combine(_root, name);
        File.WriteAllText(p, "x");
        return new CartItem(p, name, bytes, Verdict.Safe, "test");
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Progress_is_shown_and_stop_after_this_item_leaves_the_rest()
    {
        var bin = new GatedBin("b.bin");
        var vm = new MainWindowViewModel(AppServices.Create(":memory:", bin, secrets: new TelegramServiceTests.MemorySecrets()),
            new AutomationTests.FakeNotifier(), _ => new TelegramServiceTests.FakeTelegram()) { Onboarding = null };
        const long MB = 1_000_000; // big enough that each goes on its own (not batched)
        vm.OpenReview([Item("a.bin", 100 * MB), Item("b.bin", 300 * MB), Item("c.bin", 600 * MB)], "Quick Clean", "quick-clean");
        var review = vm.Review!;
        var cleaning = review.CleanCommand.ExecuteAsync(null);

        await bin.Held.Task;
        await Until(() => review.ProgressText.StartsWith("Moving 2"));
        review.IsWorking.Should().BeTrue();
        review.IsIdle.Should().BeFalse();
        review.ProgressText.Should().Contain("Moving 2 of 3").And.Contain("b.bin").And.Contain(" done");
        review.ProgressValue.Should().BeApproximately(60, 0.01); // largest first: c.bin (600 of 1000 bytes) is done

        ReviewViewModel.SlowAfter = TimeSpan.Zero;
        review.CheckSlow();
        review.SlowText.Should().Contain("b.bin");
        if (Environment.GetEnvironmentVariable("MIU_SCREENSHOT_DIR") is { Length: > 0 } outDir)
        {
            var window = new Miu.App.Views.MainWindow { DataContext = vm, Width = 1360, Height = 860 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()!.Save(Path.Combine(outDir, "clean-progress.png"));
            window.Close();
        }

        review.StopCommand.Execute(null);
        review.ProgressText.Should().StartWith("Stopping after b.bin");
        bin.Release.SetResult();
        await cleaning;

        bin.Received.Select(Path.GetFileName).Should().Equal("c.bin", "b.bin"); // b finished, a never started
        vm.HasNotice.Should().BeTrue();
        vm.Notice!.Message.Should().Contain("Moved 2 items").And.Contain("1 item was left");
        vm.Notice.CanUndo.Should().BeTrue();
        File.Exists(Path.Combine(_root, "a.bin")).Should().BeTrue();
    }

    [Fact]
    public async Task The_service_reports_checking_then_each_move()
    {
        var reports = new List<CleanProgress>();
        var s = AppServices.Create(":memory:", new CleanupServiceTests.FakeBin(), secrets: new TelegramServiceTests.MemorySecrets());
        var progress = new SyncProgressList(reports);
        const long Big = 200_000_000; // two of these don't fit in one batch
        await new CleanupService(s).CleanAsync([Item("x.bin", Big), Item("y.bin", Big)], null, "cart", progress);
        reports.Select(r => (r.Phase, r.Done, r.Current)).Should().Equal(
            ("checking", 0, "x.bin"), ("checking", 1, "y.bin"), ("moving", 0, "x.bin"), ("moving", 1, "y.bin"), ("moving", 2, ""));
        reports[^1].BytesDone.Should().Be(2 * Big);
    }

    [Fact]
    public async Task Thousands_of_small_files_go_in_batches_and_report_each_batch()
    {
        var reports = new List<CleanProgress>();
        var bin = new CountingBin();
        var s = AppServices.Create(":memory:", bin, secrets: new TelegramServiceTests.MemorySecrets());
        var items = Enumerable.Range(0, 250).Select(i => Item($"f{i:000}.log", 1000)).ToList();
        var outcome = await new CleanupService(s).CleanAsync(items, null, "quick-clean", new SyncProgressList(reports));
        bin.Calls.Should().Equal(100, 100, 50);
        outcome.Count.Should().Be(250);
        reports.Where(r => r.Phase == "moving").Select(r => (r.Done, r.Current)).Should().Equal(
            (0, "f000.log and 99 more"), (100, "f100.log and 99 more"), (200, "f200.log and 49 more"), (250, ""));
    }

    [Fact]
    public void Big_items_are_never_batched_with_others()
    {
        CartItem I(long b) => new("/x/" + b, "n", b, Verdict.Safe, "t");
        var batches = CleanupService.Batches([I(5), I(300_000_000), I(6), I(7)]).Select(b => b.Count).ToList();
        batches.Should().Equal(1, 1, 2);
    }

    private sealed class CountingBin : IRecycleBin
    {
        public List<int> Calls { get; } = [];
        public bool IsSupported => true;
        public Task<RecycleResult> RecycleAsync(IReadOnlyList<string> paths, IProgress<int>? progress, CancellationToken ct)
        {
            Calls.Add(paths.Count);
            return Task.FromResult(new RecycleResult(paths.Select(p => new RecycledItem(p, "/trash/" + Path.GetFileName(p), 1000)).ToList(), []));
        }
        public Task<RestoreResult> RestoreAsync(IReadOnlyList<RecycledItem> items, CancellationToken ct) => Task.FromResult(new RestoreResult([], []));
    }

    [Fact]
    public async Task Cleaning_inside_a_cart_folder_updates_the_cart()
    {
        var s = AppServices.Create(":memory:", new CleanupServiceTests.FakeBin(), secrets: new TelegramServiceTests.MemorySecrets());
        var wt = Path.Combine(_root, "worktree");
        Directory.CreateDirectory(Path.Combine(wt, "target"));
        s.Cart.Add(new CartItem(wt, "worktree", 1000, Verdict.Review, "wt"));
        s.Cart.Add(new CartItem(Path.Combine(_root, "deleted-elsewhere"), "deleted-elsewhere", 50, Verdict.Safe, "x"));
        var target = new CartItem(Path.Combine(wt, "target"), "target", 800, Verdict.Safe, "build");
        await new CleanupService(s).CleanAsync([target], null, "quick-clean");
        s.Cart.Items.Should().ContainSingle().Which.Bytes.Should().Be(200);
    }

    private sealed class SyncProgressList(List<CleanProgress> list) : IProgress<CleanProgress>
    {
        public void Report(CleanProgress value) => list.Add(value);
    }
}
