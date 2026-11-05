using Avalonia.Headless.XUnit;
using Miu.App.Services;
using Miu.App.ViewModels;
using Miu.App.Views;
using Miu.Platform.Portable;

namespace Miu.App.Tests;

public sealed class CleanUiTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "miu-ui-" + Guid.NewGuid().ToString("N"));
    private readonly string _data;
    private readonly string _trash;

    public CleanUiTests()
    {
        _data = Path.Combine(_root, "data");
        _trash = Path.Combine(_root, "Trash");
        void F(string rel, int size, int age)
        {
            var p = Path.Combine(_data, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, new byte[size]);
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-age));
        }
        F("code/old-app/package.json", 10, 60);
        F("code/old-app/package-lock.json", 10, 60);
        F("code/old-app/src/index.ts", 10, 60);
        F("code/old-app/node_modules/react/index.js", 8000, 60);
        F("Documents/taxes.pdf", 3000, 10);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private async Task<MainWindowViewModel> ScannedShell()
    {
        var vm = new MainWindowViewModel(AppServices.Create(":memory:", new FreedesktopTrash(_trash)));
        await vm.Analyze.ScanPathAsync(_data);
        return vm;
    }

    [AvaloniaFact]
    public async Task Add_to_cart_from_analyze_updates_bar()
    {
        var vm = await ScannedShell();
        vm.Analyze.Selected = vm.Analyze.Items.Single(i => i.Name == "Documents");
        vm.Analyze.AddToCartCommand.Execute(vm.Analyze.Selected);
        vm.Cart.Count.Should().Be(1);
        vm.Cart.HasItems.Should().BeTrue();
        vm.Cart.TotalText.Should().Be("2.9 KB");
        vm.Analyze.SelectedInCart.Should().BeTrue();
        vm.Cart.ClearCommand.Execute(null);
        vm.Cart.HasItems.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task Quick_clean_moves_safe_items_and_undo_restores()
    {
        var vm = await ScannedShell();
        var nm = Path.Combine(_data, "code", "old-app", "node_modules");
        vm.Home.QuickCleanCommand.CanExecute(null).Should().BeTrue();
        vm.Home.QuickCleanCommand.Execute(null);

        vm.IsReviewOpen.Should().BeTrue();
        var review = vm.Review!;
        review.Rows.Should().ContainSingle(r => r.Path == nm && r.IsChecked);
        await review.CleanCommand.ExecuteAsync(null);

        Directory.Exists(nm).Should().BeFalse();
        vm.IsReviewOpen.Should().BeFalse();
        vm.Notice!.Message.Should().Contain("Moved 1 item").And.Contain("7.8 KB");
        vm.Home.SafeTotalBytes.Should().Be(0);

        await vm.Notice.UndoCommand.ExecuteAsync(null);
        Directory.Exists(nm).Should().BeTrue();
        vm.Activity.Load();
        vm.Activity.Rows.Single().Undone.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task Review_rows_flag_non_safe_items_and_leave_them_unchecked()
    {
        var vm = await ScannedShell();
        vm.Analyze.Selected = vm.Analyze.Items.Single(i => i.Name == "Documents");
        vm.Analyze.AddToCartCommand.Execute(vm.Analyze.Selected);
        vm.Cart.ReviewCommand.Execute(null);
        var row = vm.Review!.Rows.Single();
        row.IsChecked.Should().BeFalse();
        row.NeedsAttention.Should().BeTrue();
        vm.Review.CleanCommand.CanExecute(null).Should().BeFalse(); // nothing checked
    }

    [AvaloniaFact]
    public async Task Dev_project_clean_offers_only_rebuildable_folders()
    {
        var vm = await ScannedShell();
        var row = vm.DevProjects.Projects.Single();
        vm.DevProjects.CleanProjectCommand.Execute(row);
        vm.Review!.Rows.Select(r => r.Name).Should().Equal("node_modules");
    }

    [AvaloniaFact]
    public async Task Protected_item_cannot_be_added()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = new MainWindowViewModel(AppServices.Create(":memory:", new FreedesktopTrash(_trash)));
        await vm.Analyze.ScanPathAsync("/etc");
        var item = vm.Analyze.Items.First();
        item.Verdict.Verdict.Should().Be(Miu.Core.Rules.Verdict.Protected);
        vm.Analyze.AddToCartCommand.CanExecute(item).Should().BeFalse();
    }

    [AvaloniaFact]
    public void Main_window_renders_with_cart_and_overlay()
    {
        var vm = new MainWindowViewModel(AppServices.Create(":memory:", new FreedesktopTrash(_trash)));
        var w = new MainWindow { DataContext = vm };
        w.Show();
        vm.Navigate(vm.Activity);
        vm.CurrentPage.Should().Be(vm.Activity);
        w.Close();
    }
}
