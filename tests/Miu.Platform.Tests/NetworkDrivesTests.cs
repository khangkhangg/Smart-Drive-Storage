using Miu.Platform.Abstractions;
using Miu.Platform.Windows;

namespace Miu.Platform.Tests;

#pragma warning disable CA1416
public class NetworkDrivesTests
{
    [Fact]
    public void Errors_are_explained_without_secrets()
    {
        WindowsNetworkDrives.Explain(86).Should().Contain("password is wrong");
        WindowsNetworkDrives.Explain(85).Should().Contain("letter");
        WindowsNetworkDrives.Explain(53).Should().Contain("Couldn't find");
    }

    [Fact]
    public async Task Unsupported_drives_say_why()
    {
        var n = new UnsupportedNetworkDrives("Use your file manager.");
        n.IsSupported.Should().BeFalse();
        (await n.MapAsync(@"\\a\b", null, null, null, false, default)).Message.Should().Be("Use your file manager.");
        n.Mapped().Should().BeEmpty();
    }

    [Fact]
    public async Task Windows_free_letters_and_a_failed_map()
    {
        if (!OperatingSystem.IsWindows()) return;
        var n = new WindowsNetworkDrives();
        var free = n.FreeLetters();
        free.Should().NotBeEmpty();
        free.Should().NotContain("C:");
        var r = await n.MapAsync(@"\\miu-no-such-host.invalid\share", null, "user", "s3cret-pass", false, default);
        r.Ok.Should().BeFalse();
        r.Message.Should().NotContain("s3cret-pass");
        n.Mapped().Should().NotContain(m => m.Unc.Contains("miu-no-such-host"));
    }
}
