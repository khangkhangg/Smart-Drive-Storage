using Avalonia.Headless.XUnit;
using Miu.App.Services;
using Miu.App.ViewModels;

namespace Miu.App.Tests;

public class OnboardingTests
{
    private static MainWindowViewModel Shell(AppServices? s = null) =>
        new(s ?? AppServices.Create(":memory:", new CleanupServiceTests.FakeBin(), secrets: new TelegramServiceTests.MemorySecrets()),
            new AutomationTests.FakeNotifier(), _ => new TelegramServiceTests.FakeTelegram());

    [AvaloniaFact]
    public async Task First_run_walks_three_steps_then_never_again()
    {
        var s = AppServices.Create(":memory:", new CleanupServiceTests.FakeBin(), secrets: new TelegramServiceTests.MemorySecrets());
        var vm = Shell(s);
        var o = vm.Onboarding!;
        o.Locations.First().Path.Should().Be(vm.Home.HomeFolder);
        o.Locations.First().IsChecked.Should().BeTrue();
        o.IsStep1.Should().BeTrue();
        foreach (var l in o.Locations) l.IsChecked = false; // don't scan the real home folder in a test
        await o.NextCommand.ExecuteAsync(null);
        o.IsStep2.Should().BeTrue();
        o.BackCommand.Execute(null);
        o.IsStep1.Should().BeTrue();
        await o.NextCommand.ExecuteAsync(null);
        await o.NextCommand.ExecuteAsync(null);
        o.IsStep3.Should().BeTrue();
        o.NextText.Should().Be("Start my first scan");
        await o.NextCommand.ExecuteAsync(null);
        vm.HasOnboarding.Should().BeFalse();
        Shell(s).HasOnboarding.Should().BeFalse(); // remembered
    }

    [AvaloniaFact]
    public async Task Extras_open_the_right_page()
    {
        var vm = Shell();
        await vm.Onboarding!.SetUpCloudCommand.ExecuteAsync(null);
        vm.CurrentPage.Should().Be(vm.CloudNetwork);
        vm.HasOnboarding.Should().BeFalse();
    }
}
