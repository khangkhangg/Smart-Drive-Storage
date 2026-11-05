using Avalonia.Headless.XUnit;
using Miu.App.Services;
using Miu.Cloud;
using Miu.Core.Automation;
using Miu.Core.Backup;
using Miu.Core.Learning;
using Miu.Data;
using Miu.Platform.Abstractions;

namespace Miu.App.Tests;

/// <summary>A recycle bin that really moves files into a folder (and back), so "freed" files are gone from disk.</summary>
internal sealed class MovingBin(string dir) : IRecycleBin
{
    public bool IsSupported => true;

    public Task<RecycleResult> RecycleAsync(IReadOnlyList<string> paths, IProgress<int>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        var done = new List<RecycledItem>();
        foreach (var p in paths)
        {
            var to = Path.Combine(dir, Guid.NewGuid().ToString("N"));
            var size = new FileInfo(p).Length;
            File.Move(p, to);
            done.Add(new RecycledItem(p, to, size));
        }
        return Task.FromResult(new RecycleResult(done, []));
    }

    public Task<RestoreResult> RestoreAsync(IReadOnlyList<RecycledItem> items, CancellationToken ct)
    {
        foreach (var i in items) File.Move(i.TrashLocation, i.OriginalPath);
        return Task.FromResult(new RestoreResult(items.Select(i => i.OriginalPath).ToList(), []));
    }
}

public sealed class BackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "miu-backup-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryCloudStorage _cloud = new();
    private readonly AppServices _s;
    private readonly long _account;
    private DateTime _now = new(2026, 11, 5, 9, 0, 0, DateTimeKind.Local);
    private string Photos => Path.Combine(_root, "Photos");

    public BackupServiceTests()
    {
        _s = AppServices.Create(":memory:", new MovingBin(Path.Combine(_root, ".bin")), secrets: new TelegramServiceTests.MemorySecrets(), networkDrives: new UnsupportedNetworkDrives("n/a"));
        _s.CloudStorageOverride = _ => _cloud;
        _account = _s.CloudAccounts.Add("s3", "Test cloud");
        Directory.CreateDirectory(Photos);
    }

    public void Dispose()
    {
        _s.Dispose();
        Directory.Delete(_root, true);
    }

    private static void Write(string path, string text, int daysOld = 1)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-daysOld));
    }

    private BackupService Service => new(_s, new CloudAccountsService(_s), () => _now) { RetryDelay = _ => TimeSpan.Zero };

    private BackupPlan Plan(BackupMode mode, int keepVersions = 5, int freeAfter = 30)
    {
        var id = _s.BackupPlans.Add(new BackupPlan(0, "Photos", _account, BackupService.SourcesFor([Photos]), "Miu Backup", mode, KeepVersions: keepVersions, FreeAfterDays: freeAfter));
        return _s.BackupPlans.Get(id)!;
    }

    [Fact]
    public async Task First_run_uploads_and_verifies_then_nothing_to_do()
    {
        Write(Path.Combine(Photos, "a.jpg"), "aaa");
        Write(Path.Combine(Photos, "2024", "b.jpg"), "bbbb");
        var plan = Plan(BackupMode.CopyNew);
        var o = await Service.RunAsync(plan, "app");
        o.Status.Should().Be("ok");
        o.Uploaded.Should().Be(2);
        _cloud.Files.Keys.Should().BeEquivalentTo("Miu Backup/Photos/a.jpg", "Miu Backup/Photos/2024/b.jpg");
        var m = _s.BackupManifest.For(plan.Id);
        m.Should().OnlyContain(e => e.HashKind == CloudHashes.Md5 && e.Hash != null);
        (await Service.RunAsync(plan, "app")).Status.Should().Be("nothing");
        _s.BackupPlans.Get(plan.Id)!.LastResult.Should().Contain("already backed up");
    }

    [Fact]
    public async Task A_changed_file_keeps_the_old_copy_as_a_version_and_prunes_to_the_newest_n()
    {
        var plan = Plan(BackupMode.CopyNew, keepVersions: 2);
        for (var i = 0; i < 4; i++)
        {
            Write(Path.Combine(Photos, "doc.txt"), "version " + i + new string('x', i), daysOld: 10 - i);
            _now = _now.AddMinutes(1);
            (await Service.RunAsync(plan, "app")).Status.Should().Be("ok");
        }
        Encoding(_cloud.Files["Miu Backup/Photos/doc.txt"].Data).Should().StartWith("version 3");
        var versions = _cloud.Files.Keys.Where(k => k.Contains(".miu-versions")).ToList();
        versions.Should().HaveCount(2);
        versions.Select(k => Encoding(_cloud.Files[k].Data)).Should().BeEquivalentTo("version 1x", "version 2xx");
        _s.BackupManifest.Versions(plan.Id, Path.Combine(Photos, "doc.txt")).Should().HaveCount(2);
    }

    [Fact]
    public async Task Mirror_keeps_removed_files_as_versions_and_brakes_for_an_unplugged_folder()
    {
        Write(Path.Combine(Photos, "keep.jpg"), "k");
        Write(Path.Combine(Photos, "gone.jpg"), "g");
        var plan = Plan(BackupMode.Mirror);
        await Service.RunAsync(plan, "app");
        File.Delete(Path.Combine(Photos, "gone.jpg"));
        (await Service.RunAsync(plan, "app")).Status.Should().Be("ok");
        _cloud.Files.Keys.Should().Contain(k => k.Contains(".miu-versions") && k.EndsWith("Photos/gone.jpg"));
        _cloud.Files.Keys.Should().NotContain("Miu Backup/Photos/gone.jpg");

        Directory.Delete(Photos, true); // unplugged
        var o = await Service.RunAsync(plan, "app");
        o.Status.Should().Be("refused");
        _cloud.Files.Keys.Should().Contain("Miu Backup/Photos/keep.jpg");
    }

    [Fact]
    public async Task Back_up_then_free_only_frees_verified_unchanged_old_files_into_the_recycle_bin()
    {
        Write(Path.Combine(Photos, "old.jpg"), "old photo", daysOld: 40);
        Write(Path.Combine(Photos, "corrupt.jpg"), "will be damaged", daysOld: 40);
        Write(Path.Combine(Photos, "recent.jpg"), "new photo", daysOld: 2);
        _cloud.Corrupt = p => p.EndsWith("corrupt.jpg");
        var plan = Plan(BackupMode.BackupThenFree);
        var o = await Service.RunAsync(plan, "app");
        o.Status.Should().Be("partial");
        File.Exists(Path.Combine(Photos, "old.jpg")).Should().BeFalse();       // freed
        File.Exists(Path.Combine(Photos, "corrupt.jpg")).Should().BeTrue();    // its upload never verified
        File.Exists(Path.Combine(Photos, "recent.jpg")).Should().BeTrue();     // too recent
        o.Freed.Should().Be(1);
        o.CleanActionId.Should().BeGreaterThan(0);
        _s.Actions.Get(o.CleanActionId)!.Origin.Should().Be("backup");
        _s.BackupManifest.For(plan.Id).Single(e => e.LocalPath.EndsWith("old.jpg")).Freed.Should().BeTrue();

        // Freed files are not re-uploaded, and Mirror-style removal never touches them.
        _cloud.Corrupt = _ => false;
        var again = await Service.PreviewAsync(plan);
        again.Result.Ops.Should().NotContain(op => op.LocalPath.EndsWith("old.jpg"));
    }

    [Fact]
    public async Task Nothing_is_freed_without_a_provider_checksum_or_after_a_change_or_under_a_keep_rule()
    {
        Write(Path.Combine(Photos, "nohash.jpg"), "x", daysOld: 40);
        var plan = Plan(BackupMode.BackupThenFree);
        _cloud.NoHash = true;
        var o = await Service.RunAsync(plan, "app");
        File.Exists(Path.Combine(Photos, "nohash.jpg")).Should().BeTrue();
        o.Summary.Should().Contain("checksum");

        _cloud.NoHash = false;
        Write(Path.Combine(Photos, "kept.jpg"), "precious", daysOld: 40);
        new LearningService(_s).Teach(Path.Combine(Photos, "kept.jpg"), false, DecisionAction.Keep, "Wedding", null);
        await Service.RunAsync(plan, "app");
        File.Exists(Path.Combine(Photos, "kept.jpg")).Should().BeTrue();
    }

    [Fact]
    public async Task A_file_changed_after_its_upload_is_not_freed()
    {
        Write(Path.Combine(Photos, "a.jpg"), "v1", daysOld: 40);
        var plan = Plan(BackupMode.CopyNew);
        await Service.RunAsync(plan, "app");
        File.WriteAllText(Path.Combine(Photos, "a.jpg"), "v2 edited");
        File.SetLastWriteTimeUtc(Path.Combine(Photos, "a.jpg"), DateTime.UtcNow.AddDays(-35));
        _cloud.FailUpload = _ => true; // the new version can't be uploaded
        var free = _s.BackupPlans.Patch(plan.Id, p => p with { Mode = BackupMode.BackupThenFree })!;
        var o = await Service.RunAsync(free, "app");
        o.Status.Should().Be("partial");
        File.ReadAllText(Path.Combine(Photos, "a.jpg")).Should().Be("v2 edited");
    }

    [Fact]
    public async Task Transient_upload_errors_are_retried()
    {
        Write(Path.Combine(Photos, "a.jpg"), "a");
        var fails = 1;
        _cloud.FailUpload = _ => fails-- > 0;
        (await Service.RunAsync(Plan(BackupMode.CopyNew), "app")).Status.Should().Be("ok");
        _cloud.Files.Should().ContainKey("Miu Backup/Photos/a.jpg");
    }

    [Fact]
    public async Task A_stopped_run_continues_where_it_left_off()
    {
        for (var i = 0; i < 4; i++) Write(Path.Combine(Photos, $"f{i}.jpg"), "data" + i);
        var plan = Plan(BackupMode.CopyNew);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgressAt(cts, 2);
        await FluentActions.Awaiting(() => Service.RunAsync(plan, "app", null, progress, cts.Token)).Should().ThrowAsync<OperationCanceledException>();
        var first = _cloud.Uploads;
        first.Should().BeInRange(1, 3);
        _s.JobRuns.RecentBackup(plan.Id, 1).Single().Status.Should().Be("cancelled");
        (await Service.RunAsync(plan, "app")).Uploaded.Should().Be(4 - first);
    }

    private sealed class SyncProgressAt(CancellationTokenSource cts, int done) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress p) { if (p.Done == done) cts.Cancel(); }
    }

    [Fact]
    public async Task Restore_puts_freed_files_back_and_never_overwrites_a_different_file()
    {
        Write(Path.Combine(Photos, "old.jpg"), "old photo", daysOld: 40);
        Write(Path.Combine(Photos, "doc.txt"), "backed up text", daysOld: 40);
        var plan = Plan(BackupMode.BackupThenFree);
        await Service.RunAsync(plan, "app");
        File.Exists(Path.Combine(Photos, "old.jpg")).Should().BeFalse();
        Write(Path.Combine(Photos, "doc.txt"), "a NEW local file with the same name", daysOld: 0);

        var entries = _s.BackupManifest.For(plan.Id);
        var r = await Service.RestoreAsync(plan, entries, null);
        r.Restored.Should().Be(2);
        File.ReadAllText(Path.Combine(Photos, "old.jpg")).Should().Be("old photo");
        File.ReadAllText(Path.Combine(Photos, "doc.txt")).Should().Be("a NEW local file with the same name");
        r.RenamedTo.Single().Should().Contain("doc (restored ");
        File.ReadAllText(r.RenamedTo.Single()).Should().Be("backed up text");
        _s.BackupManifest.For(plan.Id).Single(e => e.LocalPath.EndsWith("old.jpg")).Freed.Should().BeFalse();

        var elsewhere = Path.Combine(_root, "Restored");
        (await Service.RestoreAsync(plan, entries, elsewhere)).Restored.Should().Be(2);
        File.ReadAllText(Path.Combine(elsewhere, "Photos", "old.jpg")).Should().Be("old photo");
    }

    [Fact]
    public async Task Restore_rejects_paths_that_climb_out_of_the_folder()
    {
        var plan = Plan(BackupMode.CopyNew);
        _cloud.Put("Miu Backup/../../evil.txt", "x"u8.ToArray());
        var evil = new ManifestEntry(Path.Combine(Photos, "evil.txt"), 1, DateTime.UtcNow, null, null, "Miu Backup/Photos/../../../evil.txt", DateTime.UtcNow);
        var r = await Service.RestoreAsync(plan, [evil], Path.Combine(_root, "Restored"));
        r.Failed.Single().Reason.Should().Contain("isn't safe");
        File.Exists(Path.Combine(_root, "evil.txt")).Should().BeFalse();
    }

    [Fact]
    public void Plans_are_validated()
    {
        var s = Service;
        s.Validate(new BackupPlan(0, "x", _account, [], "Miu Backup", BackupMode.CopyNew)).Should().Contain("at least one");
        s.Validate(new BackupPlan(0, "x", _account, BackupService.SourcesFor([Photos, Path.Combine(Photos, "sub")]), "Miu Backup", BackupMode.CopyNew)).Should().Contain("inside");
        s.Validate(new BackupPlan(0, "x", _account, BackupService.SourcesFor([Photos]), "../up", BackupMode.CopyNew)).Should().Contain("cloud folder");
        s.Validate(new BackupPlan(0, "x", 999, BackupService.SourcesFor([Photos]), "Miu Backup", BackupMode.CopyNew)).Should().Contain("account");
        BackupService.SourcesFor(["/a/Photos", "/b/Photos"]).Select(x => x.RemoteName).Should().Equal("Photos", "Photos (2)");
    }

    [Fact]
    public async Task Back_up_before_cleaning_uploads_everything_inside_or_says_why_not()
    {
        Write(Path.Combine(Photos, "x", "1.txt"), "1");
        Write(Path.Combine(Photos, "x", "2.txt"), "2");
        var account = _s.CloudAccounts.Get(_account)!;
        (await Service.BackUpBeforeCleaningAsync(account, [Path.Combine(Photos, "x")], null, default)).Should().BeNull();
        _cloud.Files.Keys.Should().HaveCount(2).And.OnlyContain(k => k.StartsWith("Miu Backup/Before cleaning/2026-11-05 0900/x/"));
        _cloud.Corrupt = _ => true;
        (await Service.BackUpBeforeCleaningAsync(account, [Path.Combine(Photos, "x", "1.txt")], null, default)).Should().Contain("nothing was cleaned");
    }

    [AvaloniaFact]
    public async Task Scheduled_backup_runs_when_due()
    {
        var notes = new AutomationTests.FakeNotifier();
        var automation = new AutomationService(_s, notes, new AlwaysPlugged(), () => _now, _ => { });
        automation.Backup.RetryDelay = _ => TimeSpan.Zero;
        Write(Path.Combine(Photos, "a.jpg"), "a");
        var id = _s.BackupPlans.Add(new BackupPlan(0, "Photos", _account, BackupService.SourcesFor([Photos]), "Miu Backup", BackupMode.CopyNew, WatchSchedule.Daily(new TimeOnly(10, 0))));
        await automation.TickAsync();
        _cloud.Files.Should().BeEmpty();
        _now = _now.AddHours(2);
        await automation.TickAsync();
        _cloud.Files.Should().ContainKey("Miu Backup/Photos/a.jpg");
        notes.Shown.Single().Title.Should().Contain("Backup");
        _s.BackupPlans.Get(id)!.NextRunLocal.Should().Be(_now.Date.AddDays(1).AddHours(10));
    }

    private static string Encoding(byte[] b) => System.Text.Encoding.UTF8.GetString(b);
}
