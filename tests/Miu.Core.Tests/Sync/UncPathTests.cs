using Miu.Core.Sync;

namespace Miu.Core.Tests.Sync;

public class UncPathTests
{
    [Theory]
    [InlineData(@"\\nas\photos", "nas", "photos", @"\\nas\photos")]
    [InlineData(@"\\nas\photos\2024\", "nas", "photos", @"\\nas\photos\2024")]
    [InlineData("//nas/photos/2024", "nas", "photos", @"\\nas\photos\2024")]
    [InlineData("smb://192.168.1.20/Media/Movies", "192.168.1.20", "Media", @"\\192.168.1.20\Media\Movies")]
    [InlineData(@"  \\my-nas.local\C$  ", "my-nas.local", "C$", @"\\my-nas.local\C$")]
    public void Parses(string input, string server, string share, string unc)
    {
        UncPath.TryParse(input, out var p).Should().BeTrue();
        p!.Server.Should().Be(server);
        p.Share.Should().Be(share);
        p.ToString().Should().Be(unc);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"\\nas")]
    [InlineData(@"\\nas\")]
    [InlineData(@"\\\\nas\share")]
    [InlineData("smb://")]
    [InlineData(@"\\nas\share\..\other")]
    [InlineData(@"\\nas\sh*re")]
    [InlineData(@"\\n as\share")]
    [InlineData(@"C:\folder")]
    [InlineData(@"\\nas\share\\x")]
    public void Rejects(string input) => UncPath.TryParse(input, out _).Should().BeFalse();

    [Fact]
    public void Smb_url_for_other_systems()
    {
        UncPath.TryParse(@"\\nas\photos\2024", out var p).Should().BeTrue();
        p!.ToSmbUrl().Should().Be("smb://nas/photos/2024");
        p.ShareRoot.Should().Be(@"\\nas\photos");
    }
}
