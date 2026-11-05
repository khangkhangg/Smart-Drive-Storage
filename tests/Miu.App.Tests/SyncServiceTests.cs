using Miu.App.Services;
using Miu.Core.Automation;
using Miu.Core.Sync;
using Miu.Data;
using Miu.Platform.Abstractions;

namespace Miu.App.Tests;

public sealed class SyncServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "miu-syncsvc-" + Guid.NewGuid().ToString("N"));
    private readonly AppServices _s = AppServices.Create(":memory:", new CleanupServiceTests.FakeBin(), secrets: new TelegramServiceTests.MemorySecrets(),
        networkDrives: new UnsupportedNetworkDrives("n/a"));
    private DateTime _now = new(2026, 11, 5, 9, 0, 0, DateTimeKind.Local);
    private string Src => Path.Combine(_root, "src");
    private string Dst => Path.Combine(_root, "dst");

    public SyncServiceTests()
    {
        Directory.CreateDirectory(Src);
        Directory.CreateDirectory(Dst);
    }

    public void Dispose()
    {
        _s.Dispose();
        Directory.Delete(_root, true);
    }

    private static void Write(string path, string text, int minutesAgo = 60)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesAgo));
    }

    private SyncService Service => new(_s, () => _now);

    private SyncJob AddJob(SyncMode mode, WatchSchedule? schedule = null)
    {
        var id = _s.SyncJobs.Add(new SyncJob(0, "Test", Src, Dst, mode, schedule));
        return _s.SyncJobs.Get(id)!;
    }

    [Fact]
    public void Folders_inside_each_other_or_protected_are_rejected()
    {
        Service.Validate(Src, Src).Should().Contain("same");
        Service.Validate(_root, Src).Should().Contain("inside");
        Service.Validate(Src, _root).Should().Contain("inside");
        var system = OperatingSystem.IsWindows() ? @"C:\Windows" : "/usr";
        Service.Validate(system, Dst).Should().Contain("protected");
        Service.Validate(Src, Dst).Should().BeNull();
    }

    [Fact]
    public async Task Mirror_run_logs_activity_and_undo_puts_files_back()
    {
        Write(Path.Combine(Src, "a.txt"), "a");
        Write(Path.Combine(Dst, "old.txt"), "old");
        var job = AddJob(SyncMode.Mirror);
        var preview = await Service.PreviewAsync(job);
        preview.CanRun.Should().BeTrue();
        preview.Text.Should().Contain("Copy 1 file").And.Contain("set aside 1");

        var outcome = await Service.RunAsync(job, "app");
        outcome.Status.Should().Be("ok");
        File.Exists(Path.Combine(Dst, "a.txt")).Should().BeTrue();
        File.Exists(Path.Combine(Dst, "old.txt")).Should().BeFalse();
        _s.SyncJobs.Get(job.Id)!.LastResult.Should().Contain("Copied 1 file");
        _s.JobRuns.RecentSync(job.Id, 1).Single().Status.Should().Be("ok");
        var action = _s.Actions.Get(outcome.ActionId)!;
        action.Kind.Should().Be("sync");

        var (restored, skipped) = Service.Undo(outcome.ActionId);
        restored.Should().Be(1);
        skipped.Should().BeEmpty();
        File.ReadAllText(Path.Combine(Dst, "old.txt")).Should().Be("old");
        _s.Actions.Get(outcome.ActionId)!.Undone.Should().BeTrue();
    }

    [Fact]
    public async Task Undo_never_overwrites_a_newer_file()
    {
        Write(Path.Combine(Src, "a.txt"), "new");
        Write(Path.Combine(Dst, "a.txt"), "old", 300);
        var outcome = await Service.RunAsync(AddJob(SyncMode.Mirror), "app");
        var (restored, skipped) = Service.Undo(outcome.ActionId);
        restored.Should().Be(0);
        skipped.Single().Should().Contain("newer");
        File.ReadAllText(Path.Combine(Dst, "a.txt")).Should().Be("new");
        _s.Actions.Get(outcome.ActionId)!.Undone.Should().BeFalse();
    }

    [Fact]
    public async Task A_missing_source_or_empty_source_never_empties_the_target()
    {
        for (var i = 0; i < 5; i++) Write(Path.Combine(Dst, $"f{i}.txt"), "x");
        var job = AddJob(SyncMode.Mirror);
        (await Service.RunAsync(job, "auto")).Status.Should().Be("refused");
        Directory.Delete(Src);
        (await Service.RunAsync(job, "auto")).Status.Should().Be("failed");
        Directory.GetFiles(Dst).Should().HaveCount(5);
    }

    [Fact]
    public async Task Two_way_remembers_a_baseline_so_deletions_follow()
    {
        Write(Path.Combine(Src, "a.txt"), "a");
        Write(Path.Combine(Dst, "b.txt"), "b");
        var job = AddJob(SyncMode.TwoWay);
        (await Service.RunAsync(job, "app")).Status.Should().Be("ok");
        File.Exists(Path.Combine(Src, "b.txt")).Should().BeTrue();
        File.Exists(Path.Combine(Dst, "a.txt")).Should().BeTrue();
        _s.SyncBaseline.Get(job.Id)!.Should().HaveCount(2);

        File.Delete(Path.Combine(Src, "a.txt"));
        var outcome = await Service.RunAsync(job, "app");
        File.Exists(Path.Combine(Dst, "a.txt")).Should().BeFalse();
        _s.Actions.Get(outcome.ActionId)!.Items.Single().OriginalPath.Should().Be(Path.Combine(Dst, "a.txt"));
    }

    [Fact]
    public async Task Two_way_skips_files_in_folders_it_cannot_read()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return; // chmod doesn't stop root
        Write(Path.Combine(Src, "locked", "x.txt"), "x");
        Write(Path.Combine(Dst, "locked", "x.txt"), "x");
        var job = AddJob(SyncMode.TwoWay);
        await Service.RunAsync(job, "app");
        File.SetUnixFileMode(Path.Combine(Src, "locked"), UnixFileMode.None);
        try
        {
            var preview = await Service.PreviewAsync(job);
            preview.Plan.Ops.Should().BeEmpty(); // not "deleted in the source"
        }
        finally { File.SetUnixFileMode(Path.Combine(Src, "locked"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact]
    public async Task Compare_by_hash_skips_touched_but_identical_files()
    {
        Write(Path.Combine(Src, "same.bin"), "identical", 10);
        Write(Path.Combine(Dst, "same.bin"), "identical", 500);
        var id = _s.SyncJobs.Add(new SyncJob(0, "Hash", Src, Dst, SyncMode.Mirror, CompareByHash: true));
        (await Service.PreviewAsync(_s.SyncJobs.Get(id)!)).Plan.IsEmpty.Should().BeTrue();
        (await Service.PreviewAsync(_s.SyncJobs.Get(id)! with { CompareByHash = false })).Plan.Copies.Should().Be(1);
    }

    [Fact]
    public async Task Scheduled_sync_runs_when_due_and_notifies()
    {
        var notes = new AutomationTests.FakeNotifier();
        var automation = new AutomationService(_s, notes, new AlwaysPlugged(), () => _now, _ => { });
        Write(Path.Combine(Src, "a.txt"), "a");
        var job = AddJob(SyncMode.CopyOnly, WatchSchedule.Daily(new TimeOnly(10, 0)));
        await automation.TickAsync();
        _s.SyncJobs.Get(job.Id)!.NextRunLocal.Should().Be(_now.Date.AddHours(10));
        File.Exists(Path.Combine(Dst, "a.txt")).Should().BeFalse();
        _now = _now.AddHours(2);
        await automation.TickAsync();
        File.Exists(Path.Combine(Dst, "a.txt")).Should().BeTrue();
        notes.Shown.Single().Title.Should().Contain("Test");
        _s.SyncJobs.Get(job.Id)!.NextRunLocal.Should().Be(_now.Date.AddDays(1).AddHours(10));
        await automation.TickAsync(); // nothing due, nothing to say
        notes.Shown.Should().ContainSingle();
    }
}

public sealed class NetworkServiceTests
{
    private sealed class FakeDrives : INetworkDrives
    {
        public List<MappedDrive> Current { get; } = [];
        public List<(string Unc, string? Letter, string? User, string? Password, bool Reconnect)> Calls { get; } = [];
        public bool Fail { get; set; }
        public bool IsSupported => true;
        public string UnsupportedReason => "";
        public IReadOnlyList<MappedDrive> Mapped() => Current;
        public IReadOnlyList<string> FreeLetters() => ["Z:", "Y:"];

        public Task<NetResult> MapAsync(string unc, string? letter, string? user, string? password, bool reconnect, CancellationToken ct)
        {
            Calls.Add((unc, letter, user, password, reconnect));
            if (Fail) return Task.FromResult(NetResult.Fail("The user name or password is wrong."));
            Current.Add(new MappedDrive(letter, unc, true));
            return Task.FromResult(NetResult.Success("ok"));
        }

        public Task<NetResult> UnmapAsync(string letterOrUnc, CancellationToken ct)
        {
            Current.RemoveAll(m => m.Letter == letterOrUnc || m.Unc == letterOrUnc);
            return Task.FromResult(NetResult.Success("ok"));
        }

        public Task<IReadOnlyList<string>> ListSharesAsync(string server, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["photos"]);
    }

    private readonly FakeDrives _drives = new();
    private readonly TelegramServiceTests.MemorySecrets _secrets = new();

    private AppServices Create() => AppServices.Create(":memory:", new CleanupServiceTests.FakeBin(), secrets: _secrets, networkDrives: _drives);

    [Fact]
    public async Task Map_saves_the_share_and_keeps_the_password_only_in_the_secret_store()
    {
        using var s = Create();
        var n = new NetworkService(s);
        (await n.MapAsync("smb://nas/photos/2024", "z:", "khang", "pw", reconnect: true)).Ok.Should().BeTrue();
        _drives.Calls.Single().Should().Be((@"\\nas\photos", "Z:", "khang", "pw", true));
        var share = s.NetworkShares.All().Single();
        share.Letter.Should().Be("Z:");
        _secrets.Get(share.SecretKey).Should().Be("pw");

        using (var cmd = s.Database.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT group_concat(unc || ifnull(letter,'') || ifnull(cred_ref,'') || ifnull(user_name,'')) FROM network_shares";
            ((string)cmd.ExecuteScalar()!).Should().NotContain("pw");
        }

        await n.UnmapAsync(_drives.Current.Single());
        s.NetworkShares.All().Should().BeEmpty();
        _secrets.Get(share.SecretKey).Should().BeNull();
    }

    [Fact]
    public async Task Bad_input_and_failures_are_explained()
    {
        using var s = Create();
        var n = new NetworkService(s);
        (await n.MapAsync(@"\\nas", null, null, null, false)).Message.Should().Contain(@"\\computer\folder");
        _drives.Fail = true;
        var r = await n.MapAsync(@"\\nas\photos", null, "u", "pw", true);
        r.Ok.Should().BeFalse();
        s.NetworkShares.All().Should().BeEmpty();
        _secrets.Get(NetworkShare.SecretKeyFor(@"\\nas\photos")).Should().BeNull();
    }

    [Fact]
    public async Task Without_reconnect_the_password_is_not_kept()
    {
        using var s = Create();
        await new NetworkService(s).MapAsync(@"\\nas\photos", "Z:", "u", "pw", reconnect: false);
        _secrets.Get(NetworkShare.SecretKeyFor(@"\\nas\photos")).Should().BeNull();
    }

    [Fact]
    public async Task Saved_shares_reconnect_at_start_up()
    {
        using var s = Create();
        var n = new NetworkService(s);
        await n.MapAsync(@"\\nas\photos", "Z:", "u", "pw", reconnect: true);
        await n.MapAsync(@"\\nas\music", "Y:", null, null, reconnect: false);
        _drives.Current.Clear(); // after a restart nothing is connected
        _drives.Calls.Clear();
        (await n.ReconnectSavedAsync()).Should().Be(1);
        _drives.Calls.Single().Should().Be((@"\\nas\photos", "Z:", "u", "pw", true));
        (await n.ReconnectSavedAsync()).Should().Be(0); // already connected
    }
}
