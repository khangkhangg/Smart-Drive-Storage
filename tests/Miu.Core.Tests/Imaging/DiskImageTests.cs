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

    [Theory]
    [InlineData(@"D:\", "exFAT", true)]
    [InlineData(@"D:\", "FAT32", true)]
    [InlineData(@"E:\", "NTFS", false)]
    [InlineData(@"E:\", "ReFS", false)]
    [InlineData(@"\\nas\backups", null, false)]
    public void Windows_needs_ntfs_refs_or_a_network_folder(string target, string? format, bool refused) =>
        (DiskImage.WindowsTargetProblem(target, format) is not null).Should().Be(refused);

    private static DiskImageSchedule S(string target = @"E:\", ImageFrequency f = ImageFrequency.Weekly) =>
        new("ab12cd34", @"C:\", target, f, DayOfWeek.Saturday, 15, 2, System: true, Device: "/dev/sdb1", Label: "data");

    [Fact]
    public void Windows_schedules_run_wbadmin_as_system_weekly_or_monthly()
    {
        var weekly = DiskImage.WindowsScheduleCreate(S());
        weekly.Arguments.Should().Equal("/Create", "/F", "/TN", @"Miu\Disk image ab12cd34", "/SC", "WEEKLY", "/D", "SAT", "/ST", "02:00",
            "/RU", "SYSTEM", "/RL", "HIGHEST", "/TR", "wbadmin start backup -backupTarget:E: -include:C: -allCritical -quiet");
        DiskImage.WindowsScheduleCreate(S(f: ImageFrequency.Monthly)).Arguments.Should().ContainInOrder("/SC", "MONTHLY", "/D", "15");
        // A network folder with a space is quoted inside /TR.
        DiskImage.WindowsScheduleCreate(S(@"\\nas\my backups")).Arguments[^1]
            .Should().Be("wbadmin start backup \\\"-backupTarget:\\\\nas\\my backups\\\" -include:C: -allCritical -quiet");
    }

    [Fact]
    public void Linux_schedules_are_systemd_timers_with_escaped_dates()
    {
        var script = DiskImage.LinuxScheduleCreate(S("/media/k/usb", ImageFrequency.Monthly)).Arguments[2];
        script.Should().Contain("OnCalendar=*-*-15 02:00:00").And.Contain("Persistent=true")
            .And.Contain("$$(date +%%Y-%%m-%%d-%%H%%M)").And.Contain("dd if='/dev/sdb1'").And.Contain("systemctl enable --now miu-disk-image-ab12cd34.timer");
        DiskImage.LinuxScheduleCreate(S("/media/k/usb")).Arguments[2].Should().Contain("OnCalendar=Sat *-*-* 02:00:00");
        DiskImage.LinuxScheduleDelete("ab12cd34").Arguments[2].Should().Contain("disable --now miu-disk-image-ab12cd34.timer");
    }

    [Fact]
    public void Odd_paths_and_times_cant_be_scheduled()
    {
        DiskImage.ScheduleProblem(S()).Should().BeNull();
        DiskImage.ScheduleProblem(S("/media/k/it's")).Should().NotBeNull();
        DiskImage.ScheduleProblem(S("/media/k/50%")).Should().NotBeNull();
        DiskImage.ScheduleProblem(S() with { Id = "../x" }).Should().NotBeNull();
        DiskImage.ScheduleProblem(S() with { DayOfMonth = 31 }).Should().NotBeNull();
    }
}
