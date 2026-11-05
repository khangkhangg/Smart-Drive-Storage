using System.Text;
using Miu.Platform.Windows;

namespace Miu.Platform.Tests;

public class WindowsRecycleBinTests
{
    private static readonly DateTime When = new(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Parse_v2()
    {
        const string path = @"C:\Users\k\Code\app\node_modules";
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(2L));
        bytes.AddRange(BitConverter.GetBytes(123_456L));
        bytes.AddRange(BitConverter.GetBytes(When.ToFileTimeUtc()));
        bytes.AddRange(BitConverter.GetBytes(path.Length + 1));
        bytes.AddRange(Encoding.Unicode.GetBytes(path + "\0"));
        var e = RecycleBinIndex.Parse(bytes.ToArray())!;
        e.OriginalPath.Should().Be(path);
        e.Size.Should().Be(123_456);
        e.DeletedUtc.Should().Be(When);
    }

    [Fact]
    public void Parse_v1()
    {
        const string path = @"D:\old\setup.exe";
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(1L));
        bytes.AddRange(BitConverter.GetBytes(42L));
        bytes.AddRange(BitConverter.GetBytes(When.ToFileTimeUtc()));
        var name = new byte[520];
        Encoding.Unicode.GetBytes(path).CopyTo(name, 0);
        bytes.AddRange(name);
        RecycleBinIndex.Parse(bytes.ToArray())!.OriginalPath.Should().Be(path);
    }

    [Fact]
    public void Parse_garbage_returns_null()
    {
        RecycleBinIndex.Parse([1, 2, 3]).Should().BeNull();
        RecycleBinIndex.Parse(new byte[40]).Should().BeNull();
    }

    [Fact]
    public async Task Recycle_and_restore_roundtrip()
    {
        if (!OperatingSystem.IsWindows()) return; // runs on the Windows CI runner
        var dir = Path.Combine(Path.GetTempPath(), "miu-rb-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "build", "out.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, new byte[2048]);
        try
        {
            var bin = new WindowsRecycleBin();
            var r = await bin.RecycleAsync([Path.Combine(dir, "build")], null, CancellationToken.None);
            r.Failed.Should().BeEmpty();
            Directory.Exists(Path.Combine(dir, "build")).Should().BeFalse();
            r.Recycled.Single().Bytes.Should().Be(2048);
            var back = await bin.RestoreAsync(r.Recycled, CancellationToken.None);
            back.Failed.Should().BeEmpty();
            File.Exists(file).Should().BeTrue();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Network_paths_have_no_recycle_bin()
    {
        if (!OperatingSystem.IsWindows()) return;
        WindowsRecycleBin.HasRecycleBin(@"\\server\share\file.txt").Should().BeFalse();
        WindowsRecycleBin.HasRecycleBin(Path.GetTempPath()).Should().BeTrue();
    }
}
