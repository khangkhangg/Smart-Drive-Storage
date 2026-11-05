using Avalonia.Headless.XUnit;
using Miu.App.Services;
using Miu.App.ViewModels;
using Miu.Core.Cleanup;
using Miu.Core.Learning;
using Miu.Core.Rules;

namespace Miu.App.Tests;

public sealed class LearningSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "miu-ls-" + Guid.NewGuid().ToString("N"));

    public LearningSafetyTests()
    {
        var dup = new byte[3000];
        new Random(3).NextBytes(dup);
        void F(string rel, byte[] data, int age)
        {
            var p = Path.Combine(_root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, data);
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-age));
        }
        F("proj/keep.txt", new byte[100], 10);
        F("proj/junk.bin", new byte[900], 10);
        F("Photos/p0.jpg", dup, 400);
        F("Downloads/p0 (1).jpg", dup, 2);
        var seed = 20;
        foreach (var n in new[] { "a", "b", "c" })
        {
            var b = new byte[1500];
            new Random(seed++).NextBytes(b);
            F($"Downloads/{n}.iso", b, 5);
        }
    }

    public void Dispose() => Directory.Delete(_root, true);

    private async Task<MainWindowViewModel> Shell()
    {
        var vm = new MainWindowViewModel(AppServices.Create(":memory:", new CleanupServiceTests.FakeBin()));
        await vm.Analyze.ScanPathAsync(_root);
        return vm;
    }

    private string P(string rel) => Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));

    [AvaloniaFact]
    public async Task Safe_folder_containing_a_kept_item_is_never_offered_whole()
    {
        var vm = await Shell();
        var s = vm.Analyze.Session!;
        vm.Learning.Teach(P("proj/keep.txt"), false, DecisionAction.Keep, null, s);
        vm.Learning.Teach(P("proj"), true, DecisionAction.Delete, null, s);
        s.SafeItems.Select(i => s.Tree.GetFullPath(i.NodeId)).Should().NotContain(P("proj"));
        s.SafeItems.Select(i => s.Tree.GetFullPath(i.NodeId)).Should().Contain(P("proj/junk.bin"));
    }

    [AvaloniaFact]
    public async Task Cleaning_a_folder_with_a_kept_item_inside_is_refused()
    {
        var vm = await Shell();
        var s = vm.Analyze.Session!;
        vm.Learning.Teach(P("proj/keep.txt"), false, DecisionAction.Keep, null, s);
        var outcome = await vm.Cleanup.CleanAsync([new CartItem(P("proj"), "proj", 1000, Verdict.Review, "r")], s, "cart");
        outcome.Count.Should().Be(0);
        outcome.Failed.Single().Reason.Should().Contain("Keep");
    }

    [AvaloniaFact]
    public async Task Rule_changes_drop_now_kept_items_from_the_cart()
    {
        var vm = await Shell();
        vm.Services.Cart.Add(new CartItem(P("proj"), "proj", 1000, Verdict.Review, "r"));
        vm.Learning.Teach(P("proj/keep.txt"), false, DecisionAction.Keep, null, vm.Analyze.Session);
        vm.ApplyRuleChanges();
        vm.Services.Cart.Items.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task Cleaning_duplicates_teaches_nothing()
    {
        var vm = await Shell();
        vm.Analyze.DuplicateMinBytes = 1000;
        vm.Analyze.SetViewCommand.Execute("Duplicates");
        await vm.Analyze.FindDuplicatesCommand.ExecuteAsync(null);
        vm.Analyze.AddGroupToCartCommand.Execute(vm.Analyze.DuplicateGroups.Single());
        await vm.CleanAsync(vm.Services.Cart.Items.ToList(), "cart");
        vm.Services.Decisions.All().Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task Duplicate_group_is_not_added_when_the_kept_copy_is_in_the_cart()
    {
        var vm = await Shell();
        vm.Analyze.DuplicateMinBytes = 1000;
        vm.Analyze.SetViewCommand.Execute("Duplicates");
        await vm.Analyze.FindDuplicatesCommand.ExecuteAsync(null);
        var g = vm.Analyze.DuplicateGroups.Single();
        var keep = g.Files.Single(f => f.IsKeep);
        keep.Path.Should().Be(P("Photos/p0.jpg"));
        vm.Services.Cart.Add(new CartItem(keep.Path, "p0.jpg", 3000, Verdict.Review, "r"));
        vm.Analyze.AddGroupToCartCommand.Execute(g);
        vm.Services.Cart.Items.Should().ContainSingle(); // only the kept copy the user added
    }

    [AvaloniaFact]
    public async Task Duplicates_are_cleared_after_a_new_scan()
    {
        var vm = await Shell();
        vm.Analyze.DuplicateMinBytes = 1000;
        await vm.Analyze.FindDuplicatesCommand.ExecuteAsync(null);
        vm.Analyze.DuplicateGroups.Should().NotBeEmpty();
        await vm.Analyze.ScanPathAsync(P("proj"));
        vm.Analyze.DuplicateGroups.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task Undo_contradiction_retires_pending_suggestion()
    {
        var vm = await Shell();
        var s = vm.Analyze.Session!;
        var isos = new[] { "a", "b", "c" }.Select(n => P($"Downloads/{n}.iso"))
            .Select(p => new CartItem(p, Path.GetFileName(p), 1500, Verdict.Review, "r")).ToList();
        var outcome = await vm.Cleanup.CleanAsync(isos, s, "cart");
        vm.Services.Suggestions.Pending().Should().ContainSingle();
        await vm.Cleanup.UndoAsync(outcome.ActionId);
        vm.Services.Suggestions.Pending().Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task Undo_of_items_miu_called_safe_teaches_nothing()
    {
        var vm = await Shell();
        var s = vm.Analyze.Session!;
        var outcome = await vm.Cleanup.CleanAsync([new CartItem(P("proj/junk.bin"), "junk.bin", 900, Verdict.Safe, "r")], s, "quick-clean");
        await vm.Cleanup.UndoAsync(outcome.ActionId);
        vm.Services.Decisions.All().Should().BeEmpty();
    }

    [AvaloniaFact]
    public void Import_requires_a_verdict_and_a_match_caps_priority_and_leaves_safe_rules_off()
    {
        var s = AppServices.Create(":memory:", new CleanupServiceTests.FakeBin());
        var learning = new LearningService(s);
        var r = learning.ImportJson("""
            { "rules": [
              { "id": "z" },
              { "id": "all", "match": {}, "verdict": "Safe", "reason": "x" },
              { "id": "big", "match": { "path": "/home/**" }, "verdict": "Safe", "reason": "y", "source": "Learned", "priority": 99999 },
              { "id": "k", "match": { "under": "/home/u/Clients" }, "verdict": "Keep", "reason": "w", "priority": 5000 }
            ] }
            """);
        r.Imported.Should().Be(2);
        r.Skipped.Should().Be(2);
        var rules = s.RulesStore.All();
        var big = rules.Single(x => x.Id == "big");
        big.Priority.Should().BeLessThanOrEqualTo(700);
        big.Enabled.Should().BeFalse();
        var keep = rules.Single(x => x.Id == "k");
        keep.Priority.Should().BeLessThanOrEqualTo(900);
        keep.Enabled.Should().BeTrue();
    }

    [AvaloniaFact]
    public void Import_of_empty_or_null_rules_does_not_crash()
    {
        var learning = new LearningService(AppServices.Create(":memory:", new CleanupServiceTests.FakeBin()));
        learning.ImportJson("{}").Imported.Should().Be(0);
        learning.ImportJson("""{"rules":[null]}""").Imported.Should().Be(0);
    }

    [AvaloniaFact]
    public void Teaching_the_opposite_replaces_the_rule_regardless_of_path_spelling()
    {
        if (!OperatingSystem.IsWindows()) return;
        var s = AppServices.Create(":memory:", new CleanupServiceTests.FakeBin());
        var learning = new LearningService(s);
        learning.Teach(@"C:\Data\Proj", true, DecisionAction.Keep, null, null);
        learning.Teach(@"c:/data/proj/", true, DecisionAction.Delete, null, null);
        s.RulesStore.All().Should().ContainSingle();
    }

    [Fact]
    public void Describer_never_says_anywhere_for_scoped_rules()
    {
        var r = new Rule("x", new RuleMatch(ChildOf: "/home/u/Downloads", Ext: ["iso"], IsDir: false), new RuleConditions(), Verdict.Safe, "r");
        RuleDescriber.Describe(r).Should().Be(".iso files directly in /home/u/Downloads");
        var n = new Rule("y", new RuleMatch(Names: ["bin"], IsDir: true, Inside: "/home/u/code"), new RuleConditions(), Verdict.Safe, "r");
        RuleDescriber.Describe(n).Should().Be("Folders named bin inside /home/u/code");
    }
}
