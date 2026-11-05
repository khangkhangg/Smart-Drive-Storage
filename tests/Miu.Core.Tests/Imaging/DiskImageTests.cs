using FluentAssertions;
using Miu.Core.Imaging;
using Miu.Core.Sync;

namespace Miu.Core.Tests.Imaging;

public class DiskImageTests
{
    private static string? Root(string p) => p.Length >= 2 && p[1] == ':' ? p[..2].ToUpperInvariant() : p.StartsWith('/') ? "/" + p.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() : null;

    [Fact]
    public void An_image_must_go_to_another_drive()
    {
        DiskImage.Validate(ImageOs.Windows, @"C:\", @"c:\Images", Root).Should().Contain("different drive");
        DiskImage.Validate(ImageOs.Windows, @"C:\", @"E:\", Root).Should().BeNull();
        DiskImage.Validate(ImageOs.Windows, "", @"E:\", Root).Should().NotBeNull();
        DiskImage.Validate(ImageOs.Windows, @"C:\", @"Q:\", _ => null).Should().Contain("connected");
    }

    [Fact]
    public void Windows_uses_wbadmin_with_the_system_parts_for_the_system_drive()
    {
        var c = DiskImage.Windows(@"C:\", @"E:\", system: true);
        c.Program.Should().Be("wbadmin");
        c.Arguments.Should().Equal("start", "backup", "-backupTarget:E:", "-include:C:", "-allCritical", "-quiet");
        c.ImagePath.Should().Be(@"E:\WindowsImageBackup");
        c.RestoreHow.Should().Contain("System Image Recovery");
        DiskImage.Windows(@"D:\", @"E:\", system: false).Arguments.Should().NotContain("-allCritical");
    }

    [Fact]
    public void Linux_quotes_everything_it_hands_to_the_shell()
    {
        var c = DiskImage.Linux("/dev/sdb1", "/media/k/Back up's", "data", new DateTime(2026, 11, 5, 9, 30, 0));
        c.Program.Should().Be("pkexec");
        c.Arguments[0].Should().Be("sh");
        c.Arguments[2].Should().Be("dd if='/dev/sdb1' bs=4M status=progress conv=noerror,sync | gzip -1 > '/media/k/Back up'\\''s/data-2026-11-05-0930.img.gz'");
        c.RestoreHow.Should().Contain("gunzip");
    }

    [Fact]
    public void Mac_uses_hdiutil_and_a_safe_file_name()
    {
        var c = DiskImage.MacOS("/dev/disk4", "/Volumes/Backup", "My: Disk/2", new DateTime(2026, 11, 5, 9, 30, 0));
        c.Arguments.Should().Equal("create", "-srcdevice", "/dev/disk4", "-format", "UDZO", "/Volumes/Backup/My- Disk-2 2026-11-05 0930.dmg");
    }

    [Fact]
    public void Restored_copies_get_their_own_name() =>
        ConflictNames.For("docs/report.docx", new DateOnly(2026, 11, 5), _ => false, "restored").Should().Be("docs/report (restored 2026-11-05).docx");
}
