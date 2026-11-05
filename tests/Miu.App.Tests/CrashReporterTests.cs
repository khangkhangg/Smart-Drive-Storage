using Miu.App.Services;

namespace Miu.App.Tests;

public sealed class CrashReporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "miu-crash-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Reports_are_local_shown_once_and_the_newest_ten_kept()
    {
        CrashReporter.Pending(_dir).Should().BeNull();
        string? last = null;
        for (var i = 0; i < 12; i++)
        {
            last = CrashReporter.Write(_dir, new InvalidOperationException("boom " + i), "test");
            Thread.Sleep(2);
        }
        Directory.GetFiles(CrashReporter.Folder(_dir), "crash-*.txt").Should().HaveCount(10);
        var text = File.ReadAllText(last!);
        text.Should().Contain("Miu Smart Storage").And.Contain("boom 11").And.Contain("System:");
        CrashReporter.Pending(_dir).Should().Be(last);
        CrashReporter.MarkSeen(last!);
        CrashReporter.Pending(_dir).Should().NotBe(last);

        // An error Miu recovered from is never announced as "Miu closed unexpectedly".
        var dir2 = _dir + "-2";
        CrashReporter.Write(dir2, new InvalidOperationException("recovered"), "screen", fatal: false);
        CrashReporter.Pending(dir2).Should().BeNull();
        Directory.Delete(dir2, true);
    }
}
