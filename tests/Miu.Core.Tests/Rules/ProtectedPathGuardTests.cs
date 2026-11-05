using Miu.Core.Rules;

namespace Miu.Core.Tests.Rules;

public class ProtectedPathGuardTests
{
    private static readonly ProtectedPathGuard Win = ProtectedPathGuard.ForWindows(
        systemDrive: @"C:\", windir: @"C:\Windows", programFiles: @"C:\Program Files",
        programFilesX86: @"C:\Program Files (x86)", programData: @"C:\ProgramData",
        usersRoot: @"C:\Users", userProfile: @"C:\Users\Khang");

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
    [InlineData(@"C:\ProgramData\Docker", false)]
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
    public void Unix_paths(string path, bool expected) => Unix.IsProtected(path).Should().Be(expected);

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
            guard.IsProtected(Path.Combine(dir, "plain")).Should().BeFalse();
        }
        finally { Directory.Delete(dir, true); }
    }
}
