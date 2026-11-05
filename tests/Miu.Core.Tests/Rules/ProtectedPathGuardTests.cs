using Miu.Core.Rules;

namespace Miu.Core.Tests.Rules;

public class ProtectedPathGuardTests
{
    private static readonly ProtectedPathGuard Win = ProtectedPathGuard.ForWindows(
        systemDrive: @"C:\", windir: @"C:\Windows", programFiles: @"C:\Program Files",
        programFilesX86: @"C:\Program Files (x86)", programData: @"C:\ProgramData",
        usersRoot: @"C:\Users", userProfile: @"C:\Users\Khang", extraDriveRoots: [@"D:\"],
        localAppData: @"C:\Users\Khang\AppData\Local");

    private static readonly ProtectedPathGuard Unix = ProtectedPathGuard.ForUnix("/home/u");

    [Theory]
    [InlineData(@"C:\Windows\System32\x.dll", true)]
    [InlineData(@"c:\WINDOWS", true)]
    [InlineData(@"C:\", true)]
    [InlineData(@"C:", true)]
    [InlineData(@"C:\Users\Khang", true)]
    [InlineData(@"C:\Users\Khang\", true)]
    [InlineData(@"C:\Users", true)]
    [InlineData(@"C:\Users\Khang\Downloads", false)]
    [InlineData(@"C:\Users\Khang\Code\app\node_modules", false)]
    [InlineData(@"\\?\C:\Program Files\App", true)]
    [InlineData(@"C:/Program Files (x86)/Steam", true)]
    [InlineData(@"C:\ProgramData\Microsoft\Windows", true)]
    [InlineData(@"C:\ProgramData\Docker", true)]
    [InlineData(@"C:\Windows.", true)]
    [InlineData(@"C:\Windows \x", true)]
    [InlineData(@"C:\PROGRA~1\App", true)]
    [InlineData(@"C:\Windows::$INDEX_ALLOCATION", true)]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume3\Windows", true)]
    [InlineData(@"\\?\Volume{0b1c2d3e}\Windows", true)]
    [InlineData(@"\\localhost\C$\Windows\x", true)]
    [InlineData(@"\\nas\share\videos", false)]
    [InlineData(@"C:\Windows.old\Users\x.log", true)]
    [InlineData(@"C:\$WINDOWS.~BT\x", true)]
    [InlineData(@"D:\Program Files\Game", true)]
    [InlineData(@"D:\WindowsApps\x", true)]
    [InlineData(@"D:\Projects\app", false)]
    [InlineData(@"C:\Users\Khang\AppData\Local\Programs\Microsoft VS Code", true)]
    [InlineData(@"C:\Windowsold\x", false)]
    [InlineData(@"C:\pagefile.sys", true)]
    [InlineData(@"C:\Users\Khang\..\..\Windows\x", true)]
    public void Windows_paths(string path, bool expected) => Win.IsProtected(path).Should().Be(expected);

    [Theory]
    [InlineData("/usr/lib/x", true)]
    [InlineData("/home/u", true)]
    [InlineData("/home/u/", true)]
    [InlineData("/home", true)]
    [InlineData("/", true)]
    [InlineData("/home/u/code/node_modules", false)]
    [InlineData("/USR/lib", false)]
    [InlineData("/usrx/lib", false)]
    [InlineData("/etc", true)]
    [InlineData("/private/etc/hosts", true)]
    [InlineData("/nix/store/abc", true)]
    [InlineData("/srv/www", true)]
    [InlineData("/var/log/syslog.log", true)]
    [InlineData("/var/www/site", true)]
    public void Unix_paths(string path, bool expected) => Unix.IsProtected(path).Should().Be(expected);

    [Fact]
    public void Drive_relative_path_fails_closed_off_windows()
    {
        if (OperatingSystem.IsWindows()) return; // on Windows it resolves against the drive's current folder
        Win.IsProtected(@"C:System32").Should().BeTrue();
    }

    [Fact]
    public void Explain_gives_reason_only_for_protected()
    {
        Win.Explain(@"C:\Windows\x").Should().NotBeNullOrWhiteSpace();
        Win.Explain(@"C:\Users\Khang\Downloads").Should().BeNull();
    }

    [Fact]
    public void Symlink_into_protected_tree_is_protected()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "miu-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var link = Path.Combine(dir, "sneaky");
            Directory.CreateSymbolicLink(link, "/usr");
            var guard = ProtectedPathGuard.ForUnix("/home/u");
            guard.IsProtected(link).Should().BeTrue();
            guard.Explain(link, checkLink: false).Should().BeNull(); // callers that know it is not a link skip the file system
            guard.IsProtected(Path.Combine(dir, "plain")).Should().BeFalse();
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class ProtectionKindTests
{
    private static readonly ProtectedPathGuard Unix = ProtectedPathGuard.ForUnix("/home/u");

    [Theory]
    [InlineData("/usr/lib", ProtectionKind.InsideProtectedTree)]
    [InlineData("/home/u", ProtectionKind.ProtectedExact)]
    [InlineData("/", ProtectionKind.ProtectedExact)]
    [InlineData("/var", ProtectionKind.InsideProtectedTree)]
    [InlineData("/var/lib/x", ProtectionKind.InsideProtectedTree)]
    [InlineData("/home/u/code", ProtectionKind.None)]
    public void Classify(string path, ProtectionKind expected) => Unix.Classify(path).Should().Be(expected);

    [Fact]
    public void Ancestor_only()
    {
        var g = new ProtectedPathGuard(["/a/b/c"], [], PathStyle.Unix);
        g.Classify("/a").Should().Be(ProtectionKind.AncestorOfProtected);
    }
}
