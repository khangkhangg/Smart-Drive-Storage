using Avalonia.Headless.XUnit;
using Miu.App.Services;
using Miu.App.ViewModels;
using Miu.Core.Imaging;
using Miu.Core.Sync;
using Miu.Platform.Abstractions;

namespace Miu.App.Tests;

public sealed class DiskImageAndRestoreTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "miu-image-" + Guid.NewGuid().ToString("N"));
    private readonly AppServices _s = AppServices.Create(":memory:", new CleanupServiceTests.FakeBin(), secrets: new TelegramServiceTests.MemorySecrets(),
        networkDrives: new UnsupportedNetworkDrives("n/a"));
    private string Src => Path.Combine(_root, "src");
    private string Dst => Path.Combine(_root, "dst");
    private static readonly DateTime Now = new(2026, 11, 5, 9, 30, 0);

    public DiskImageAndRestoreTests()
    {
        Directory.CreateDirectory(Src);
        Directory.CreateDirectory(Dst);
    }

    public void Dispose()
    {
        _s.Dispose();
        Directory.Delete(_root, true);
    }

    private static void Write(string path, string text, DateTime? mtimeUtc = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, mtimeUtc ?? new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    private static (long, long, string, string)? Drive(string p) => p switch
    {
        "/data" => (40L << 30, 10L << 30, "data", "/data"),
        "/media/k/usb" => (1L << 30, 100L << 30, "usb", "/media/k/usb"),
        "/media/k/small" => (1L << 30, 5L << 30, "small", "/media/k/small"),
        _ => null,
    };

    [Fact]
    public async Task Disk_image_shows_the_command_runs_it_and_logs_it()
    {
        DiskImageCommand? ran = null;
        var svc = new DiskImageService(_s, (cmd, progress, _) =>
        {
            ran = cmd;
            progress?.Report("1073741824 bytes copied");
            return Task.FromResult((0, "done"));
        }, () => Now);
        var preview = await svc.PreviewAsync("/data", "/media/k/usb", "/dev/sdb1", p => Drive(p), ImageOs.Linux);
        preview.Problem.Should().BeNull();
        preview.Command!.Display.Should().Contain("dd if='/dev/sdb1'").And.Contain("/media/k/usb/data-2026-11-05-0930.img.gz");
        var lines = new List<string>();
        var outcome = await svc.RunAsync(preview, new SyncProgress<string>(lines));
        outcome.Ok.Should().BeTrue();
        outcome.Message.Should().Contain("To restore");
        ran.Should().BeSameAs(preview.Command);
        lines.Should().Equal("1073741824 bytes copied");
        var log = _s.Actions.Recent(5).Single();
        (log.Kind, log.Origin).Should().Be(("backup", "disk-image"));
    }

    [Fact]
    public async Task Disk_image_is_refused_without_room_or_on_the_same_drive_and_failures_are_logged()
    {
        var svc = new DiskImageService(_s, (_, _, _) => Task.FromResult((1, "dd: error reading '/dev/sdb1': Input/output error")), () => Now);
        (await svc.PreviewAsync("/data", "/media/k/small", "/dev/sdb1", p => Drive(p), ImageOs.Linux)).Problem.Should().Contain("Not enough space");
        (await svc.PreviewAsync("/data", "/data", "/dev/sdb1", p => Drive(p), ImageOs.Linux)).Problem.Should().Contain("different drive");
        var ok = await svc.PreviewAsync("/data", "/media/k/usb", "/dev/sdb1", p => Drive(p), ImageOs.Linux);
        var outcome = await svc.RunAsync(ok, null);
        outcome.Ok.Should().BeFalse();
        outcome.Message.Should().Contain("Input/output error");
        _s.Actions.Recent(5).Single().Kind.Should().Be("failed");
    }

    [Fact]
    public async Task Restore_puts_files_back_and_never_overwrites_a_different_file()
    {
        var job = new SyncJob(1, "t", Src, Dst, SyncMode.CopyOnly);
        Write(Path.Combine(Dst, "docs", "a.txt"), "backup a");
        Write(Path.Combine(Dst, "docs", "b.txt"), "backup b");
        Write(Path.Combine(Dst, "docs", "same.txt"), "same");
        Write(Path.Combine(Src, "docs", "b.txt"), "local b changed", new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc));
        Write(Path.Combine(Src, "docs", "same.txt"), "same");
        var r = new RestoreService(() => Now);
        r.List(job, "").Select(e => e.Name).Should().Equal("docs");
        var outcome = await r.RestoreAsync(job, "docs", Src);
        (outcome.Restored, outcome.Renamed, outcome.Skipped).Should().Be((2, 1, 1));
        File.ReadAllText(Path.Combine(Src, "docs", "a.txt")).Should().Be("backup a");
        File.ReadAllText(Path.Combine(Src, "docs", "b.txt")).Should().Be("local b changed"); // untouched
        File.ReadAllText(Path.Combine(Src, "docs", "b (restored 2026-11-05).txt")).Should().Be("backup b");
        Directory.GetFiles(Src, "*.miu-partial", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task Older_versions_come_from_the_set_aside_folder()
    {
        var job = new SyncJob(1, "t", Src, Dst, SyncMode.Mirror);
        Write(Path.Combine(Dst, "report.txt"), "v3");
        Write(Path.Combine(Dst, ".miu-sync-trash", "2026-11-01 090000", "report.txt"), "v1");
        Write(Path.Combine(Dst, ".miu-sync-trash", "2026-11-03 090000", "report.txt"), "v2");
        var r = new RestoreService(() => Now);
        r.List(job, "").Select(e => e.Name).Should().Equal("report.txt"); // the set-aside folder is hidden
        var versions = r.Versions(job, "report.txt");
        versions.Select(v => v.Label).Should().Equal("Current backup", "Set aside 2026-11-03 090000", "Set aside 2026-11-01 090000");
        await r.RestoreAsync(job, "report.txt", Path.Combine(_root, "elsewhere"), versions[2]);
        File.ReadAllText(Path.Combine(_root, "elsewhere", "report.txt")).Should().Be("v1");
    }

    [Fact]
    public async Task Restore_refuses_paths_outside_the_backup()
    {
        var job = new SyncJob(1, "t", Src, Dst, SyncMode.Mirror);
        Write(Path.Combine(_root, "secret.txt"), "x");
        var outcome = await new RestoreService(() => Now).RestoreAsync(job, "../secret.txt", Src);
        outcome.Restored.Should().Be(0);
        Assert.Throws<IOException>(() => RestoreService.Full(Dst, "../x"));
    }

    [AvaloniaFact]
    public async Task Browse_and_restore_from_a_job_row()
    {
        var vm = new MainWindowViewModel(_s) { Onboarding = null };
        var cn = vm.CloudNetwork;
        Write(Path.Combine(Dst, "photos", "cat.jpg"), "meow");
        _s.SyncJobs.Add(new SyncJob(0, "Photos", Src, Dst, SyncMode.Mirror));
        cn.LoadJobs();
        cn.Jobs.Single().BrowseCommand.Execute(null);
        cn.IsRestoreOpen.Should().BeTrue();
        cn.RestoreTo.Should().Be(Src);
        cn.RestoreRows.Single().OpenCommand.Execute(null);
        cn.RestoreFolder.Should().Be("photos");
        await cn.RestoreRows.Single().RestoreCommand.ExecuteAsync(null);
        cn.RestoreMessage.Should().StartWith("Restored 1 file.");
        File.ReadAllText(Path.Combine(Src, "photos", "cat.jpg")).Should().Be("meow");
        cn.BackupUpCommand.Execute(null);
        cn.RestoreFolder.Should().Be("");
    }

    private sealed class SyncProgress<T>(List<T> list) : IProgress<T>
    {
        public void Report(T value) => list.Add(value);
    }
}
