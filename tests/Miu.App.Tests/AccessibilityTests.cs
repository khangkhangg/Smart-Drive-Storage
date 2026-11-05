using System.Globalization;
using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Miu.App.Services;
using Miu.App.ViewModels;

namespace Miu.App.Tests;

public class AccessibilityTests
{
    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    private static double Luminance(string hex)
    {
        double C(int i)
        {
            var c = int.Parse(hex.Substring(1 + i * 2, 2), NumberStyles.HexNumber) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * C(0) + 0.7152 * C(1) + 0.0722 * C(2);
    }

    public static double Contrast(string a, string b)
    {
        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    [Fact]
    public void Text_colours_reach_4_5_to_1_in_light_and_dark()
    {
        var doc = XDocument.Load(Path.Combine(Repo(), "src", "Miu.App", "Styles", "Theme.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var theme in new[] { "Light", "Dark" })
        {
            var dict = doc.Descendants().First(e => e.Name.LocalName == "ResourceDictionary" && (string?)e.Attribute(x + "Key") == theme);
            var c = dict.Elements().ToDictionary(e => (string)e.Attribute(x + "Key")!, e => (string)e.Attribute("Color")!);
            foreach (var text in new[] { "MiuText", "MiuTextSecondary", "MiuSafeFg", "MiuDangerFg", "MiuWarnFg" })
                foreach (var bg in new[] { "MiuShellBg", "MiuContentBg", "MiuCardBg" })
                    Contrast(c[text], c[bg]).Should().BeGreaterThanOrEqualTo(4.5, $"{text} on {bg} ({theme})");
            Contrast(c["MiuSafeFg"], c["MiuSafeBg"]).Should().BeGreaterThanOrEqualTo(4.5, $"safe chip ({theme})");
        }
        Contrast("#8A4600", "#FDF0E1").Should().BeGreaterThanOrEqualTo(4.5); // the fixed warning banners
        Contrast("#FFFFFF", "#005FB8").Should().BeGreaterThanOrEqualTo(4.5); // primary buttons
        Contrast("#FFFFFF", "#0E700E").Should().BeGreaterThanOrEqualTo(4.5); // green buttons
    }

    [Fact]
    public void Views_use_theme_colours_for_red_text()
    {
        foreach (var f in Directory.GetFiles(Path.Combine(Repo(), "src", "Miu.App", "Views"), "*.axaml"))
            File.ReadAllText(f).Should().NotContain("Foreground=\"#B3261E\"", Path.GetFileName(f)).And.NotContain("Foreground=\"#A4262C\"", Path.GetFileName(f));
    }

    [AvaloniaFact]
    public void Ctrl_number_goes_to_pages()
    {
        var vm = new MainWindowViewModel(AppServices.Create(":memory:", new CleanupServiceTests.FakeBin(), secrets: new TelegramServiceTests.MemorySecrets()),
            new AutomationTests.FakeNotifier(), _ => new TelegramServiceTests.FakeTelegram()) { Onboarding = null };
        vm.GoToPageCommand.Execute("2");
        vm.CurrentPage.Should().Be(vm.Analyze);
        vm.GoToPageCommand.Execute("99");
        vm.CurrentPage.Should().Be(vm.Analyze);
    }
}
