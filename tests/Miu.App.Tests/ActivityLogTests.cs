using Avalonia.Headless.XUnit;
using Miu.App.Services;
using Miu.App.ViewModels;
using Miu.Core.Cleanup;
using Miu.Core.Rules;
using Miu.Platform.Abstractions;

namespace Miu.App.Tests;

public sealed class ActivityLogTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "miu-activity-" + Guid.NewGuid().ToString("N"));

    public ActivityLogTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private CartItem Item(string name, long bytes = 100)
    {
        var p = Path.Combine(_root, name);
        File.WriteAllText(p, "x");
        return new CartItem(p, name, bytes, Verdict.Safe, "test");
    }

    /// <summary>Like Windows when an item is too big for the bin: gone, but no bin copy found.</summary>
    private sealed class NoCopyBin : IRecycleBin
    {
        public bool IsSupported => true;
        public Task<RecycleResult> RecycleAsync(IReadOnlyList<string> paths, IProgress<int>? progress, CancellationToken ct)
        {
            foreach (var p in paths) File.Delete(p);
            return Task.FromResult(new RecycleResult([], paths.Select(p => new FailedItem(p, "no Recycle Bin copy was found")).ToList()));
        }
        public Task<RestoreResult> RestoreAsync(IReadOnlyList<RecycledItem> items, CancellationToken ct) => Task.FromResult(new RestoreResult([], []));
    }

    private static MainWindowViewModel Shell(IRecycleBin bin) =>
        new(AppServices.Create(":memory:", bin, secrets: new TelegramServiceTests.MemorySecrets()),
            new AutomationTests.FakeNotifier(), _ => new TelegramServiceTests.FakeTelegram()) { Onboarding = null };

    [AvaloniaFact]
    public async Task Items_removed_without_a_bin_copy_and_items_that_failed_are_in_activity()
    {
        var vm = Shell(new NoCopyBin());
        await vm.CleanAsync([Item("big.iso", 5000)], "quick-clean");
        var row = vm.Activity.Rows.Should().ContainSingle().Subject;
        row.Summary.Should().Contain("Removed 1 item").And.Contain("without a");
        row.ItemsText.Should().Contain("big.iso").And.Contain("deleted for good");
        row.CanUndo.Should().BeFalse();

        var vm2 = Shell(new CleanupServiceTests.FakeBin { FailNames = ["locked.txt"] });
        await vm2.CleanAsync([Item("locked.txt"), Item("ok.txt")], "cart");
        vm2.Activity.Rows.Select(r => r.Summary).Should().Contain(s => s.StartsWith("Couldn't clean 1 item"))
            .And.Contain(s => s.StartsWith("Moved 1 item"));
        vm2.Activity.Rows.Single(r => r.Summary.StartsWith("Couldn't")).ItemsText.Should().Be("locked.txt: It is in use");
    }

    [AvaloniaFact]
    public async Task Teaching_a_rule_shows_in_activity_and_undo_removes_the_rule()
    {
        var vm = Shell(new CleanupServiceTests.FakeBin());
        var dir = Path.Combine(_root, "photos");
        Directory.CreateDirectory(dir);
        vm.Learning.Teach(dir, true, Miu.Core.Learning.DecisionAction.Keep, null, null);
        var row = vm.Activity.Rows.Should().ContainSingle().Subject;
        row.Summary.Should().StartWith("New rule · Keep: ");
        row.OriginText.Should().Be("Teach Miu");
        row.CanUndo.Should().BeTrue();
        vm.Services.RulesStore.All().Should().ContainSingle();

        await vm.UndoAsync(row.Id);
        vm.Services.RulesStore.All().Should().BeEmpty();
        vm.Activity.Rows.Single(r => r.Id == row.Id).Undone.Should().BeTrue();
    }
}
