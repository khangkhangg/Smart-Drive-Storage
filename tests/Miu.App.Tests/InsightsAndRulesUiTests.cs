using Avalonia.Headless.XUnit;
using Miu.App.Services;
using Miu.App.ViewModels;
using Miu.Core.Rules;

namespace Miu.App.Tests;

public sealed class InsightsAndRulesUiTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "miu-m3-" + Guid.NewGuid().ToString("N"));

    public InsightsAndRulesUiTests()
    {
        var dup = new byte[5000];
        new Random(1).NextBytes(dup);
        void F(string rel, byte[] data, int age)
        {
            var p = Path.Combine(_root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, data);
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-age));
        }
        F("Pictures/beach.jpg", dup, 300);
        F("Downloads/beach (1).jpg", dup, 2);
        F("Videos/trip.mp4", new byte[20000], 10);
        F("code/app/package.json", new byte[10], 60);
        F("code/app/package-lock.json", new byte[10], 60);
        F("code/app/node_modules/x.js", new byte[3000], 60);
        var seed = 10;
        foreach (var n in new[] { "a", "b", "c" })
        {
            var iso = new byte[2000];
            new Random(seed++).NextBytes(iso);
            F($"Downloads/{n}.iso", iso, 5);
        }
    }

    public void Dispose() => Directory.Delete(_root, true);

    private async Task<MainWindowViewModel> Shell()
    {
        var vm = new MainWindowViewModel(AppServices.Create(":memory:", new CleanupServiceTests.FakeBin()));
        await vm.Analyze.ScanPathAsync(_root);
        return vm;
    }

    [AvaloniaFact]
    public async Task View_switch_shows_types_and_largest()
    {
        var vm = await Shell();
        vm.Analyze.SetViewCommand.Execute("Types");
        vm.Analyze.IsTypesView.Should().BeTrue();
        vm.Analyze.TypeRows.First().Name.Should().Be("Video");
        vm.Analyze.SetViewCommand.Execute("Largest");
        vm.Analyze.LargestRows.First().Name.Should().Be("trip.mp4");
        vm.Analyze.SetViewCommand.Execute("Folders");
        vm.Analyze.IsFoldersView.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task Duplicates_are_found_and_copies_go_to_cart_as_review()
    {
        var vm = await Shell();
        vm.Analyze.DuplicateMinBytes = 1000;
        vm.Analyze.SetViewCommand.Execute("Duplicates");
        await vm.Analyze.FindDuplicatesCommand.ExecuteAsync(null);
        var g = vm.Analyze.DuplicateGroups.Should().ContainSingle().Subject;
        g.Files.Single(f => f.IsKeep).Path.Should().EndWith(Path.Combine("Pictures", "beach.jpg"));
        vm.Analyze.AddGroupToCartCommand.Execute(g);
        var item = vm.Services.Cart.Items.Single();
        item.Path.Should().EndWith("beach (1).jpg");
        item.Verdict.Should().Be(Verdict.Review);
        item.Reason.Should().Contain("Duplicate of");
    }

    [AvaloniaFact]
    public async Task Teach_keep_from_details_changes_verdict()
    {
        var vm = await Shell();
        vm.Analyze.OpenCommand.Execute(vm.Analyze.Items.Single(i => i.Name == "code"));
        vm.Analyze.OpenCommand.Execute(vm.Analyze.Items.Single(i => i.Name == "app"));
        vm.Analyze.Selected = vm.Analyze.Items.Single(i => i.Name == "node_modules");
        vm.Analyze.Selected.Verdict.Verdict.Should().Be(Verdict.Safe);
        vm.Analyze.SelectedReason = "Active project";
        vm.Analyze.TeachKeepCommand.Execute(null);
        vm.Analyze.Selected!.Name.Should().Be("node_modules");
        vm.Analyze.Selected.Verdict.Verdict.Should().Be(Verdict.Keep);
        vm.Rules.YourCount.Should().Be(1);
    }

    [AvaloniaFact]
    public async Task Rules_page_shows_suggestion_and_turn_on_applies_it()
    {
        var vm = await Shell();
        var s = vm.Analyze.Session!;
        var isos = new[] { "a", "b", "c" }.Select(n => Path.Combine(_root, "Downloads", n + ".iso"))
            .Select(p => { var id = s.FindNode(p)!.Value; var v = s.VerdictFor(id); return new Miu.Core.Cleanup.CartItem(p, Path.GetFileName(p), 2000, v.Verdict, v.Reason); }).ToList();
        await vm.CleanAsync(isos, "cart");

        vm.Navigate(vm.Rules);
        var suggestion = vm.Rules.Suggestions.Should().ContainSingle().Subject;
        suggestion.Title.Should().Contain("iso").And.Contain("Downloads");
        suggestion.Evidence.Should().Contain("3 of 3");
        vm.Rules.BuiltInCount.Should().BeGreaterThan(20);

        suggestion.TurnOnCommand.Execute(null);
        vm.Rules.Suggestions.Should().BeEmpty();
        vm.Rules.LearnedCount.Should().Be(1);
        vm.Rules.Filter = "Learned";
        vm.Rules.Rows.Should().ContainSingle(r => r.SourceText == "Learned");
        vm.Home.LearnedRuleCount.Should().Be(1);
    }

    [AvaloniaFact]
    public async Task Rule_toggle_turns_rule_off()
    {
        var vm = await Shell();
        vm.Analyze.OpenCommand.Execute(vm.Analyze.Items.Single(i => i.Name == "code"));
        vm.Analyze.OpenCommand.Execute(vm.Analyze.Items.Single(i => i.Name == "app"));
        vm.Analyze.Selected = vm.Analyze.Items.Single(i => i.Name == "node_modules");
        vm.Analyze.TeachKeepCommand.Execute(null);
        vm.Navigate(vm.Rules);
        vm.Rules.Filter = "Yours";
        var row = vm.Rules.Rows.Single();
        row.Enabled = false;
        vm.Analyze.Session!.VerdictFor(vm.Analyze.Selected!.Id).Verdict.Should().Be(Verdict.Safe);
    }
}
