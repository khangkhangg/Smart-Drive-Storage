using Avalonia.Headless.XUnit;
using Miu.App.Services;
using Miu.App.ViewModels;
using Miu.Core.Sync;
using Miu.Platform.Abstractions;

namespace Miu.App.Tests;

public sealed class MirrorAndDriveBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "miu-mirror-" + Guid.NewGuid().ToString("N"));
    private readonly AppServices _s = AppServices.Create(":memory:", new CleanupServiceTests.FakeBin(), secrets: new TelegramServiceTests.MemorySecrets(),
        networkDrives: new UnsupportedNetworkDrives("n/a"));
    private string Src => Path.Combine(_root, "src");
    private string Dst => Path.Combine(_root, "dst");

    public MirrorAndDriveBackupTests()
    {
        Directory.CreateDirectory(Src);
        Directory.CreateDirectory(Dst);
    }

    public void Dispose()
    {
        _s.Dispose();
        Directory.Delete(_root, true);
    }

    private static void Write(string path, string text = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private SyncService Service => new(_s, () => DateTime.Now);

    [Fact]
    public async Task Skipped_paths_are_never_copied_and_never_set_aside()
    {
        Write(Path.Combine(Src, "docs", "a.txt"));
        Write(Path.Combine(Src, "web", "node_modules", "x.js"));
        Write(Path.Combine(Src, "tmp.tmp"));
        Write(Path.Combine(Dst, "old.tmp")); // on the target only: Mirror would set it aside, but it's skipped
        var job = new SyncJob(0, "t", Src, Dst, SyncMode.Mirror, Excludes: ["node_modules", "*.tmp"]);
        var outcome = await Service.RunAsync(job, "app");
        outcome.Status.Should().Be("ok");
        File.Exists(Path.Combine(Dst, "docs", "a.txt")).Should().BeTrue();
        Directory.Exists(Path.Combine(Dst, "web", "node_modules")).Should().BeFalse();
        File.Exists(Path.Combine(Dst, "tmp.tmp")).Should().BeFalse();
        File.Exists(Path.Combine(Dst, "old.tmp")).Should().BeTrue();
    }

    [Fact]
    public void A_drive_backup_may_read_a_protected_source_but_must_go_to_another_drive()
    {
        var root = OperatingSystem.IsWindows() ? Path.GetPathRoot(_root)! : "/";
        Service.Validate(root, Dst).Should().NotBeNull(); // a folder job never reads a whole drive
        Service.Validate(root, Dst, SyncKind.DriveBackup).Should().Contain("different drive");
        Service.Validate(Src, Dst, SyncKind.DriveBackup).Should().Contain("different drive");
    }

    [Fact]
    public void Not_enough_space_on_the_target_is_refused()
    {
        var job = new SyncJob(0, "t", Src, Dst, SyncMode.CopyOnly);
        var plan = new SyncPlan([new SyncOp(SyncOpKind.Copy, Side.Source, "big.iso", 5L << 30, "new")], null);
        SyncService.SpaceProblem(job, plan, _ => 4L << 30).Should().Contain("Not enough space");
        SyncService.SpaceProblem(job, plan, _ => 50L << 30).Should().BeNull();
        SyncService.SpaceProblem(job, new SyncPlan([new SyncOp(SyncOpKind.Archive, Side.Target, "x", 5L << 30, "gone")], null), _ => 0).Should().BeNull();
    }

    [Fact]
    public async Task Live_mirror_runs_once_after_changes_go_quiet()
    {
        var now = DateTime.UtcNow;
        using var w = new ChangeWatcher(() => now);
        var job = new SyncJob(7, "t", Src, Dst, SyncMode.Mirror, RunOnChange: true);
        w.Refresh([job]);
        w.Watched.Should().Equal(7);
        Write(Path.Combine(Src, "new.txt"));
        w.TakeDue().Should().BeEmpty(); // not quiet yet
        IReadOnlyList<long> due = [];
        for (var i = 0; i < 100 && due.Count == 0; i++) // file events can take a moment (macOS)
        {
            await Task.Delay(50);
            now += ChangeWatcher.Quiet + TimeSpan.FromSeconds(1);
            due = w.TakeDue();
        }
        due.Should().Equal(7);
        w.TakeDue().Should().BeEmpty(); // taken once
        w.Refresh([job with { RunOnChange = false }]);
        w.Watched.Should().BeEmpty();
    }

    [Fact]
    public void Changes_to_miu_own_files_dont_count()
    {
        var now = DateTime.UtcNow;
        using var w = new ChangeWatcher(() => now);
        w.Refresh([new SyncJob(1, "t", Src, Dst, SyncMode.Mirror, RunOnChange: true)]);
        Write(Path.Combine(Src, ".miu-sync-trash", "2026", "a.txt"));
        Thread.Sleep(300);
        now += TimeSpan.FromMinutes(1);
        w.TakeDue().Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task One_source_to_several_targets_makes_one_job_each()
    {
        var vm = new MainWindowViewModel(_s) { Onboarding = null };
        var cn = vm.CloudNetwork;
        cn.JobSource = Src;
        cn.JobTarget = Dst + "\n" + Path.Combine(_root, "dst2") + "\n";
        cn.Frequency = "Only when I press Run";
        cn.RunOnChange = true;
        await cn.SaveJobCommand.ExecuteAsync(null);
        var jobs = _s.SyncJobs.All();
        jobs.Should().HaveCount(2);
        jobs.Select(j => j.Target).Should().BeEquivalentTo(Dst, Path.Combine(_root, "dst2"));
        jobs.Should().OnlyContain(j => j.RunOnChange && j.Source == Src && j.Excludes!.Contains("node_modules"));
        jobs[0].ScheduleText.Should().Be("when files change");
    }

    [AvaloniaFact]
    public void Choosing_drive_backup_fills_the_skip_list()
    {
        var vm = new MainWindowViewModel(_s) { Onboarding = null };
        var cn = vm.CloudNetwork;
        cn.JobKind = "Back up a whole drive";
        cn.IsDriveBackup.Should().BeTrue();
        cn.JobExcludes.Should().Contain(OperatingSystem.IsWindows() ? "pagefile.sys" : OperatingSystem.IsMacOS() ? ".Spotlight-V100" : "proc");
        cn.KeepDeletedFiles.Should().BeTrue();
    }
}
