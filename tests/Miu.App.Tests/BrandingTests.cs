using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Miu.App.Services;
using Miu.App.ViewModels;
using Miu.App.Views;

namespace Miu.App.Tests;

public class BrandingTests
{
    [Fact]
    public void Version_comes_from_the_assembly()
    {
        AppInfo.Version.Should().MatchRegex(@"^\d+\.\d+\.\d+");
        AppInfo.Version.Should().NotContain("+");
    }

    [AvaloniaFact]
    public void Home_shows_version()
    {
        var vm = new MainWindowViewModel(AppServices.Create(":memory:"));
        vm.Home.VersionText.Should().Be($"Miu Smart Storage v{AppInfo.Version}");
    }

    [AvaloniaFact]
    public void About_page_has_author_and_links()
    {
        var vm = new MainWindowViewModel(AppServices.Create(":memory:"));
        vm.NavItems.Select(n => n.Title).Should().Contain("About");
        vm.About.MadeBy.Should().Be("Made with love by Khang");
        vm.About.Links.Select(l => l.Url).Should().Equal(
            "https://vietnamcos.com", "https://duykhang.com", "https://www.threads.com/@dkhang");
        vm.About.Links.Select(l => l.Title).Should().Equal("vietnamcos.com", "duykhang.com", "threads.com/@dkhang");
    }

    [AvaloniaFact]
    public void Splash_shows_logo_name_and_websites()
    {
        var splash = new SplashWindow();
        splash.Show();
        var texts = splash.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        texts.Should().Contain(t => t.Contains("Miu"));
        texts.Should().Contain(t => t.Contains("Khang"));
        texts.Should().Contain(t => t.Contains("vietnamcos.com") && t.Contains("duykhang.com"));
        texts.Should().Contain(t => t.Contains(AppInfo.Version));
        splash.GetLogicalDescendants().OfType<Miu.App.Controls.MiuLogo>().Should().ContainSingle();
        splash.Close();
    }
}
