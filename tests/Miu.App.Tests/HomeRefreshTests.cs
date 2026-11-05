using Avalonia.Headless.XUnit;
using Miu.App.Services;
using Miu.App.ViewModels;

namespace Miu.App.Tests;

public sealed class HomeRefreshTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "miu-home-" + Guid.NewGuid().ToString("N"));

    public HomeRefreshTests()
    {
        void F(string rel, int bytes, int ageDays)
        {
            var p = Path.Combine(_root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, new byte[bytes]);
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-ageDays));
        }
        foreach (var app in new[] { "a", "b" })
        {
            F($"code/{app}/package.json", 10, 60);
            F($"code/{app}/package-lock.json", 10, 60);
            F($"code/{app}/node_modules/x.js", 3000, 60);
        }
    }

    public void Dispose() => Directory.Delete(_root, true);

    [AvaloniaFact]
    public async Task Safe_to_remove_and_developer_numbers_follow_cleans_and_rescans()
    {
        var vm = new MainWindowViewModel(AppServices.Create(":memory:", new CleanupServiceTests.FakeBin())) { Onboarding = null };
        await vm.Analyze.ScanPathAsync(_root);
        var before = vm.Home.SafeTotalBytes;
        before.Should().BeGreaterThan(0);
        vm.Home.DevSummary.Should().Contain("2 projects");

        // Deleted outside Miu, then scanned again: Home shows the new numbers.
        Directory.Delete(Path.Combine(_root, "code", "a", "node_modules"), true);
        await vm.Analyze.ScanPathAsync(_root);
        vm.Home.SafeTotalBytes.Should().BeLessThan(before);

        // Cleaned in Miu (no new scan): Home updates straight away.
        var safe = vm.Analyze.Session!.SafeItems.Select(i => vm.Analyze.Session.Tree.GetFullPath(i.NodeId)).ToList();
        await vm.CleanAsync(safe.Select(p => new Miu.Core.Cleanup.CartItem(p, Path.GetFileName(p), 3000, Miu.Core.Rules.Verdict.Safe, "t")).ToList(), "quick-clean");
        vm.Home.SafeTotalBytes.Should().Be(0);
        vm.Home.DevRebuildableText.Should().Be("0 B");
    }

    [AvaloniaFact]
    public async Task Sidebar_shows_the_donation_links()
    {
        var vm = new MainWindowViewModel(AppServices.Create(":memory:", new CleanupServiceTests.FakeBin())) { Onboarding = null };
        vm.OpenDonationCommand.CanExecute("paypal").Should().BeTrue();
        MainWindowViewModel.PayPalUrl.Should().StartWith("https://www.paypal.com/donate/");
        MainWindowViewModel.BuyMeACoffeeUrl.Should().Be("https://buymeacoffee.com/dkhang");
        if (Environment.GetEnvironmentVariable("MIU_SCREENSHOT_DIR") is not { Length: > 0 } outDir) return;
        await vm.Analyze.ScanPathAsync(_root);
        vm.GoToPageCommand.Execute("1");
        var window = new Miu.App.Views.MainWindow { DataContext = vm, Width = 1360, Height = 900 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)!.Save(Path.Combine(outDir, "sidebar-donate.png"));
        window.Close();
    }
}
