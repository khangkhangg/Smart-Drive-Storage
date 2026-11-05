using Miu.Core.Scanning;

namespace Miu.Core.Tests.Scanning;

public class CategorizerTests
{
    [Theory]
    [InlineData("movie.MKV", "/x/movie.MKV", FileCategory.Video)]
    [InlineData("setup.msi", "/x/setup.msi", FileCategory.Installer)]
    [InlineData("a.zip", "/x/a.zip", FileCategory.Archive)]
    [InlineData("x.ts", "/x/x.ts", FileCategory.Code)]
    [InlineData("photo.HEIC", "/x/photo.HEIC", FileCategory.Image)]
    [InlineData("song.flac", "/x/song.flac", FileCategory.Audio)]
    [InlineData("report.pdf", "/x/report.pdf", FileCategory.Document)]
    [InlineData("a.tmp", @"C:\Users\k\AppData\Local\Temp\a.tmp", FileCategory.AppDataCache)]
    [InlineData("b.bin", "/home/k/.cache/b.bin", FileCategory.AppDataCache)]
    [InlineData("pagefile.sys", @"C:\pagefile.sys", FileCategory.System)]
    [InlineData("x.unknown", "/x/x.unknown", FileCategory.Other)]
    public void Categorizes(string name, string path, FileCategory expected) =>
        Categorizer.Categorize(name, false, path).Should().Be(expected);
}
