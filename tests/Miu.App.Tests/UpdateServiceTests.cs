using System.Net;
using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using Miu.App.Services;
using Miu.App.ViewModels;

namespace Miu.App.Tests;

public class UpdateServiceTests
{
    private sealed class Stub(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Last = request;
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
        }
    }

    private const string Release = """
        {"tag_name":"v0.9.0","html_url":"https://github.com/x/y/releases/tag/v0.9.0","draft":false,"prerelease":false,"body":"New: things",
         "assets":[
           {"name":"Miu-Smart-Storage-0.9.0-windows-x64-setup.exe","browser_download_url":"https://dl/win.exe"},
           {"name":"Miu-Smart-Storage-0.9.0-macos-arm64.dmg","browser_download_url":"https://dl/arm.dmg"},
           {"name":"Miu-Smart-Storage-0.9.0-macos-x64.dmg","browser_download_url":"https://dl/x64.dmg"},
           {"name":"Miu-Smart-Storage-0.9.0-x86_64.AppImage","browser_download_url":"https://dl/app.AppImage"},
           {"name":"miu-smart-storage_0.9.0_amd64.deb","browser_download_url":"https://dl/x.deb"}]}
        """;

    [Theory]
    [InlineData("1.2.10", "1.2.9", 1)]
    [InlineData("1.0.0", "1.0.0-beta.2", 1)]
    [InlineData("1.0.0-beta.10", "1.0.0-beta.2", 1)]
    [InlineData("v0.9.0", "0.9.0", 0)]
    [InlineData("0.9", "0.9.0", 0)]
    [InlineData("0.1.0+abc", "0.2.0", -1)]
    public void Versions_compare_like_semver(string a, string b, int expected) => UpdateService.Compare(a, b).Should().Be(expected);

    [Fact]
    public void Picks_the_file_for_each_system()
    {
        var names = new[] { "Miu-Smart-Storage-0.9.0-windows-x64-setup.exe", "Miu-Smart-Storage-0.9.0-macos-arm64.dmg", "Miu-Smart-Storage-0.9.0-macos-x64.dmg", "miu-smart-storage_0.9.0_amd64.deb" };
        UpdateService.AssetFor(names, "windows", Architecture.X64).Should().EndWith("setup.exe");
        UpdateService.AssetFor(names, "macos", Architecture.Arm64).Should().EndWith("arm64.dmg");
        UpdateService.AssetFor(names, "macos", Architecture.X64).Should().EndWith("x64.dmg");
        UpdateService.AssetFor(names, "linux", Architecture.X64).Should().EndWith(".deb"); // no AppImage in this list
        UpdateService.AssetFor([], "linux", Architecture.X64).Should().BeNull();
        var linux = new[] { "Miu-Smart-Storage-0.9.0-x86_64.AppImage", "miu-smart-storage_0.9.0_amd64.deb", "miu-smart-storage-0.9.0-linux-x64.tar.gz" };
        UpdateService.AssetFor(linux, "linux", Architecture.X64, "deb").Should().EndWith(".deb");     // installed with apt
        UpdateService.AssetFor(linux, "linux", Architecture.X64, "tar").Should().EndWith(".tar.gz");
        UpdateService.AssetFor(linux, "linux", Architecture.X64, "appimage").Should().EndWith(".AppImage");
    }

    [Fact]
    public async Task Reports_a_newer_release_and_nothing_otherwise()
    {
        var stub = new Stub(HttpStatusCode.OK, Release);
        var u = await new UpdateService(new HttpClient(stub)).CheckAsync("0.8.0");
        u!.Version.Should().Be("0.9.0");
        u.DownloadUrl.Should().StartWith("https://dl/");
        stub.Last!.Headers.UserAgent.ToString().Should().Be("MiuSmartStorage/0.8.0");
        (await new UpdateService(new HttpClient(stub)).CheckAsync("0.9.0")).Should().BeNull();
        (await new UpdateService(new HttpClient(new Stub(HttpStatusCode.NotFound, "{}"))).CheckAsync("0.8.0")).Should().BeNull();
        (await new UpdateService(new HttpClient(new Stub(HttpStatusCode.OK, Release.Replace("\"prerelease\":false", "\"prerelease\":true")))).CheckAsync("0.8.0")).Should().BeNull();
    }

    [AvaloniaFact]
    public async Task The_app_checks_once_a_day_shows_a_banner_and_can_skip_a_version()
    {
        var stub = new Stub(HttpStatusCode.OK, Release.Replace("v0.9.0", "v99.0.0"));
        var vm = new MainWindowViewModel(AppServices.Create(":memory:", new CleanupServiceTests.FakeBin(), secrets: new TelegramServiceTests.MemorySecrets()),
            new AutomationTests.FakeNotifier(), _ => new TelegramServiceTests.FakeTelegram(), new UpdateService(new HttpClient(stub)));
        (await vm.CheckForUpdatesAsync(force: false)).Should().Contain("99.0.0");
        vm.HasUpdate.Should().BeTrue();
        vm.UpdateText.Should().Contain("99.0.0");
        await vm.CheckForUpdatesAsync(force: false); // not again today
        stub.Calls.Should().Be(1);
        vm.SkipUpdateCommand.Execute(null);
        vm.HasUpdate.Should().BeFalse();
        vm.Services.Settings.Set(Miu.Data.SettingKeys.LastUpdateCheck, "");
        await vm.CheckForUpdatesAsync(force: false);
        vm.HasUpdate.Should().BeFalse(); // skipped
        (await vm.CheckForUpdatesAsync(force: true)).Should().Contain("available"); // asking yourself always shows it

        var offline = new MainWindowViewModel(AppServices.Create(":memory:", new CleanupServiceTests.FakeBin(), secrets: new TelegramServiceTests.MemorySecrets()),
            new AutomationTests.FakeNotifier(), _ => new TelegramServiceTests.FakeTelegram(), new UpdateService(new HttpClient(new Stub(HttpStatusCode.BadGateway, ""))));
        (await offline.CheckForUpdatesAsync(force: true)).Should().Contain("Couldn't check");
        (await offline.CheckForUpdatesAsync(force: false)).Should().BeEmpty(); // offline: no retry every minute
    }
}
