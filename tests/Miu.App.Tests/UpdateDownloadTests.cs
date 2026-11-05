using System.Net;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Headless.XUnit;
using Miu.App.Services;
using Miu.App.ViewModels;

namespace Miu.App.Tests;

public sealed class UpdateDownloadTests : IDisposable
{
    private readonly string _dir = Path.Combine(AppContext.BaseDirectory, "miu-upd-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("pretend installer bytes");
    private static readonly string Asset = UpdateService.CurrentOs() switch
    {
        "windows" => "Miu-Smart-Storage-1.0.0-windows-x64-setup.exe",
        "macos" => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? "Miu-Smart-Storage-1.0.0-macos-arm64.dmg" : "Miu-Smart-Storage-1.0.0-macos-x64.dmg",
        _ => UpdateService.LinuxInstallKind() switch { "deb" => "miu-smart-storage_1.0.0_amd64.deb", "tar" => "Miu-Smart-Storage-1.0.0-linux-x64.tar.gz", _ => "Miu-Smart-Storage-1.0.0-x86_64.AppImage" },
    };

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    /// <summary>GitHub: a stable 1.0.0, a newer 1.1.0-beta.1 pre-release, the file and its checksum list.</summary>
    private sealed class Github(string sums) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var u = r.RequestUri!.ToString();
            HttpContent body = u.EndsWith("/releases/latest") ? new StringContent(Release("1.0.0", false))
                : u.Contains("/releases?") ? new StringContent($"[{Release("1.1.0-beta.1", true)},{Release("1.0.0", false)}]")
                : u.EndsWith("SHA256SUMS") ? new StringContent(sums)
                : new ByteArrayContent(Payload);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
        }

        private static string Release(string v, bool pre) => $$"""
            {"tag_name":"v{{v}}","html_url":"https://github.com/x/y/releases/tag/v{{v}}","draft":false,"prerelease":{{(pre ? "true" : "false")}},"body":"",
             "assets":[{"name":"{{Asset}}","browser_download_url":"https://dl/{{v}}/{{Asset}}"},{"name":"SHA256SUMS","browser_download_url":"https://dl/{{v}}/SHA256SUMS"}]}
            """;
    }

    private static string GoodSums => $"{Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant()}  {Asset}\n0000  other.zip\n";

    [Fact]
    public async Task Pre_releases_are_offered_only_when_asked()
    {
        var svc = new UpdateService(new HttpClient(new Github(GoodSums)));
        (await svc.CheckAsync("0.9.0"))!.Version.Should().Be("1.0.0");
        var beta = await svc.CheckAsync("0.9.0", default, includePrereleases: true);
        beta!.Version.Should().Be("1.1.0-beta.1");
        beta.IsPrerelease.Should().BeTrue();
        beta.ChecksumUrl.Should().EndWith("SHA256SUMS");
    }

    [Fact]
    public async Task A_download_is_kept_only_when_it_matches_the_checksum()
    {
        var u = (await new UpdateService(new HttpClient(new Github(GoodSums))).CheckAsync("0.9.0"))!;
        var progress = new List<double>();
        var path = await new UpdateService(new HttpClient(new Github(GoodSums))).DownloadAsync(u, _dir, new SyncProgress(progress));
        File.ReadAllBytes(path).Should().Equal(Payload);
        Path.GetFileName(path).Should().Be(Asset);

        var bad = new UpdateService(new HttpClient(new Github($"{new string('a', 64)}  {Asset}\n")));
        var act = () => bad.DownloadAsync(u, Path.Combine(_dir, "bad"), null);
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*doesn't match*");
        Directory.GetFiles(Path.Combine(_dir, "bad")).Should().BeEmpty(); // nothing left behind
    }

    [Fact]
    public void Checksum_lists_are_read_in_both_formats()
    {
        UpdateService.ExpectedHash("abc  a.exe\ndef *b.dmg\n", "b.dmg").Should().Be("def");
        UpdateService.ExpectedHash("abc  a.exe\n", "c.exe").Should().BeNull();
    }

    [AvaloniaFact]
    public async Task With_automatic_download_the_update_is_ready_to_install()
    {
        var s = AppServices.Create(":memory:", new CleanupServiceTests.FakeBin());
        var vm = new MainWindowViewModel(s, new AutomationTests.FakeNotifier(), _ => new TelegramServiceTests.FakeTelegram(),
            new UpdateService(new HttpClient(new Github(GoodSums)))) { Onboarding = null, UpdateFolder = _dir };
        s.Settings.SetBool(Miu.Data.SettingKeys.AutoDownloadUpdates, true);
        var status = await vm.CheckForUpdatesAsync(force: true);
        status.Should().Contain("ready to install");
        vm.IsUpdateReady.Should().BeTrue();
        vm.CanDownloadUpdate.Should().BeFalse();
        File.Exists(vm.DownloadedUpdate).Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task Without_it_you_press_download()
    {
        var s = AppServices.Create(":memory:", new CleanupServiceTests.FakeBin());
        var vm = new MainWindowViewModel(s, new AutomationTests.FakeNotifier(), _ => new TelegramServiceTests.FakeTelegram(),
            new UpdateService(new HttpClient(new Github(GoodSums)))) { Onboarding = null, UpdateFolder = _dir };
        await vm.CheckForUpdatesAsync(force: true);
        vm.CanDownloadUpdate.Should().BeTrue();
        vm.IsUpdateReady.Should().BeFalse();
        await vm.DownloadUpdateCommand.ExecuteAsync(null);
        vm.IsUpdateReady.Should().BeTrue();
        vm.Settings.Shell.Should().BeSameAs(vm);
    }

    private sealed class SyncProgress(List<double> list) : IProgress<double>
    {
        public void Report(double value) => list.Add(value);
    }
}
