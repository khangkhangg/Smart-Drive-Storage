using Avalonia.Headless.XUnit;
using Miu.App.Services;
using Miu.App.ViewModels;
using Miu.Cloud;

namespace Miu.App.Tests;

public sealed class CloudAppKeysTests
{
    private static MainWindowViewModel Shell(out AppServices s)
    {
        s = AppServices.Create(":memory:", new CleanupServiceTests.FakeBin(), secrets: new TelegramServiceTests.MemorySecrets());
        return new MainWindowViewModel(s, new AutomationTests.FakeNotifier(), _ => new TelegramServiceTests.FakeTelegram()) { Onboarding = null };
    }

    [Fact]
    public void Your_keys_win_over_the_builds_and_a_secret_stays_with_its_own_id()
    {
        var build = new CloudClientIds("build.apps.googleusercontent.com", "build-secret", "build-ms", "build-dbx");
        var yours = new CloudClientIds("mine.apps.googleusercontent.com", "my-secret", null, null);
        var m = CloudClientIds.Merge(yours, build);
        (m.GoogleClientId, m.GoogleClientSecret, m.MicrosoftClientId, m.DropboxClientId)
            .Should().Be(("mine.apps.googleusercontent.com", "my-secret", "build-ms", "build-dbx"));
        CloudClientIds.Merge(new CloudClientIds(null, null, null, null), build).GoogleClientSecret.Should().Be("build-secret");
    }

    [AvaloniaFact]
    public void Saving_your_own_app_makes_the_provider_available_and_keeps_the_secret_out_of_settings()
    {
        var vm = Shell(out var s);
        var google = vm.Settings.CloudApps.Single(a => a.Provider == "gdrive");
        google.Status.Should().Be("Not set up");
        vm.CloudNetwork.Backup.Providers.Single(p => p.Key == "gdrive").Available.Should().BeFalse();

        google.ClientId = "not-a-google-id";
        google.SaveCommand.Execute(null);
        google.MessageIsError.Should().BeTrue();

        google.ClientId = "123-abc.apps.googleusercontent.com";
        google.SaveCommand.Execute(null);
        google.Message.Should().Contain("client secret"); // Google desktop apps need it

        google.ClientSecret = "GOCSPX-test";
        google.SaveCommand.Execute(null);
        google.MessageIsError.Should().BeFalse();
        google.Status.Should().StartWith("Using your own app");
        google.ClientSecret.Should().BeEmpty();
        s.Settings.Get(CloudAccountsService.GoogleSecretKey).Should().BeNull(); // the secret isn't in the database
        s.Secrets.Get(CloudAccountsService.GoogleSecretKey).Should().Be("GOCSPX-test");
        vm.CloudNetwork.Backup.Providers.Single(p => p.Key == "gdrive").Available.Should().BeTrue();
        vm.Automation.Accounts.ClientIds.Google!.ClientSecret.Should().Be("GOCSPX-test");

        google.RemoveCommand.Execute(null);
        s.Secrets.Get(CloudAccountsService.GoogleSecretKey).Should().BeNull();
        vm.CloudNetwork.Backup.Providers.Single(p => p.Key == "gdrive").Available.Should().BeFalse();
    }

    [AvaloniaFact]
    public void Microsoft_ids_are_checked_and_dropbox_shows_its_redirect()
    {
        var vm = Shell(out _);
        var ms = vm.Settings.CloudApps.Single(a => a.Provider == "onedrive");
        ms.ClientId = "nope";
        ms.SaveCommand.Execute(null);
        ms.MessageIsError.Should().BeTrue();
        ms.ClientId = Guid.NewGuid().ToString();
        ms.SaveCommand.Execute(null);
        ms.StatusIsGood.Should().BeTrue();
        vm.Settings.CloudApps.Single(a => a.Provider == "dropbox").RedirectUri.Should().Be("http://localhost:53682/");
        vm.Settings.CloudApps.Should().OnlyContain(a => a.Steps.Count >= 5);
    }

    [AvaloniaFact]
    public async Task Connecting_a_provider_that_isnt_set_up_opens_the_guide()
    {
        var vm = Shell(out _);
        var dropbox = vm.CloudNetwork.Backup.Providers.Single(p => p.Key == "dropbox");
        await vm.CloudNetwork.Backup.ConnectOAuthCommand.ExecuteAsync(dropbox);
        vm.CurrentPage.Should().BeSameAs(vm.Settings);
        vm.CloudNetwork.Backup.AccountMessageIsError.Should().BeFalse();
    }
}
