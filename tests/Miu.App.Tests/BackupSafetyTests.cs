using System.Net;
using System.Text;
using Miu.App.Services;
using Miu.Cloud;
using Miu.Core.Backup;
using Miu.Platform.Abstractions;

namespace Miu.App.Tests;

/// <summary>The in-memory cloud with hooks for failures and races.</summary>
internal sealed class HookedCloud(InMemoryCloudStorage inner) : ICloudStorage
{
    public Func<string, string, bool> FailMove { get; set; } = (_, _) => false;
    public Action<string>? OnGet { get; set; }
    public string ProviderName => inner.ProviderName;
    public string? ExpectedHashKind(long size) => inner.ExpectedHashKind(size);

    public async Task<CloudItem?> GetAsync(string path, CancellationToken ct)
    {
        var r = await inner.GetAsync(path, ct);
        OnGet?.Invoke(path);
        return r;
    }

    public Task<IReadOnlyList<CloudItem>> ListAsync(string folder, CancellationToken ct) => inner.ListAsync(folder, ct);
    public Task<CloudItem> UploadAsync(string path, Stream content, long size, IProgress<long>? progress, CancellationToken ct) => inner.UploadAsync(path, content, size, progress, ct);
    public Task DownloadAsync(string path, Stream destination, CancellationToken ct) => inner.DownloadAsync(path, destination, ct);
    public Task MoveAsync(string from, string to, CancellationToken ct) =>
        FailMove(from, to) ? throw new CloudException("SlowDown (test)", HttpStatusCode.ServiceUnavailable) : inner.MoveAsync(from, to, ct);
    public Task RemoveAsync(string path, CancellationToken ct) => inner.RemoveAsync(path, ct);
}

/// <summary>Regression tests from the Milestone 7 review: no path may lose the only cloud copy, or free a changed file.</summary>
public sealed class BackupSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "miu-bksafe-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryCloudStorage _mem = new();
    private readonly HookedCloud _cloud;
    private readonly AppServices _s;
    private readonly long _account;
    private DateTime _now = new(2026, 11, 5, 9, 0, 0, DateTimeKind.Local);
    private string Photos => Path.Combine(_root, "Photos");

    public BackupSafetyTests()
    {
        _cloud = new HookedCloud(_mem);
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

    private BackupPlan Plan(BackupMode mode, int keepVersions = 5, string? folder = null) =>
        _s.BackupPlans.Get(_s.BackupPlans.Add(new BackupPlan(0, "Photos", _account, BackupService.SourcesFor([folder ?? Photos]), "Miu Backup", mode, KeepVersions: keepVersions, FreeAfterDays: 30)))!;

    private IEnumerable<string> CloudTexts => _mem.Files.Values.Select(f => Encoding.UTF8.GetString(f.Data));

    [Fact]
    public async Task A_hiccup_while_keeping_the_old_copy_never_overwrites_it()
    {
        var a = Path.Combine(Photos, "a.txt");
        Write(a, "ORIGINAL");
        var plan = Plan(BackupMode.CopyNew);
        await Service.RunAsync(plan, "app");
        Write(a, "CHANGED!!", daysOld: 0);
        var fails = 1;
        _cloud.FailMove = (_, _) => fails-- > 0; // retried
        (await Service.RunAsync(plan, "app")).Status.Should().Be("ok");
        CloudTexts.Should().Contain(["ORIGINAL", "CHANGED!!"]);

        Write(a, "THIRD", daysOld: 0);
        _cloud.FailMove = (_, _) => true; // keeps failing: don't upload at all
        var o = await Service.RunAsync(plan, "app");
        o.Status.Should().Be("partial");
        o.Summary.Should().Contain("older copy");
        CloudTexts.Should().Contain(["ORIGINAL", "CHANGED!!"]).And.NotContain("THIRD");
    }

    [Fact]
    public void Two_plans_cannot_share_cloud_folders()
    {
        var d = Path.Combine(_root, "D", "Photos");
        var e = Path.Combine(_root, "E", "Photos");
        Directory.CreateDirectory(d);
        Directory.CreateDirectory(e);
        Plan(BackupMode.BackupThenFree, folder: d);
        var p2 = new BackupPlan(0, "E", _account, BackupService.SourcesFor([e]), "Miu Backup", BackupMode.CopyNew);
        Service.Validate(p2).Should().Contain("already backs up to miu backup/photos");
        Service.Validate(p2 with { RemoteFolder = "MIU BACKUP/photos" }).Should().NotBeNull(); // case doesn't matter (Dropbox, OneDrive)
        Service.Validate(p2 with { RemoteFolder = "Laptop" }).Should().BeNull();
        Service.Validate(p2 with { RemoteFolder = "Miu Backup/Before cleaning" }).Should().Contain("Back up first");
    }

    [Fact]
    public async Task A_freed_files_only_copy_is_pinned_never_pruned_and_restorable()
    {
        var a = Path.Combine(Photos, "doc.txt");
        Write(a, "PRECIOUS", daysOld: 100);
        var plan = Plan(BackupMode.BackupThenFree, keepVersions: 1);
        (await Service.RunAsync(plan, "app")).Freed.Should().Be(1);
        for (var i = 0; i < 3; i++)
        {
            Write(a, "new-" + i, daysOld: 0);
            File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddHours(-10 + i));
            _now = _now.AddDays(1);
            await Service.RunAsync(plan, "app");
        }
        CloudTexts.Should().Contain("PRECIOUS");
        var choices = Service.RestoreChoices(plan);
        var pinned = choices.Single(c => c.IsVersion && c.Entry.Freed);
        pinned.Note.Should().Contain("freed");
        choices.Count(c => c.IsVersion && !c.Entry.Freed).Should().Be(1); // ordinary versions are still pruned to 1

        var r = await Service.RestoreAsync(plan, [pinned], null);
        r.Restored.Should().Be(1);
        File.ReadAllText(r.RenamedTo.Single()).Should().Be("PRECIOUS"); // next to the new file, which stays
        File.ReadAllText(a).Should().Be("new-2");
    }

    [Fact]
    public async Task A_file_changed_after_it_was_checked_is_not_freed()
    {
        var b = Path.Combine(Photos, "old2.txt");
        Write(b, "v1-content", daysOld: 100);
        var plan = Plan(BackupMode.CopyNew);
        await Service.RunAsync(plan, "app");
        var mtime = File.GetLastWriteTimeUtc(b);
        _cloud.OnGet = p =>
        {
            if (!p.EndsWith("old2.txt")) return;
            File.WriteAllText(b, "NEW DATA!!"); // same size, same date: only the content shows it
            File.SetLastWriteTimeUtc(b, mtime);
        };
        var o = await Service.RunAsync(_s.BackupPlans.Patch(plan.Id, x => x with { Mode = BackupMode.BackupThenFree })!, "app");
        File.Exists(b).Should().BeTrue(o.Summary);
        File.ReadAllText(b).Should().Be("NEW DATA!!");
    }

    [Fact]
    public async Task Back_up_first_keeps_items_with_the_same_name_apart()
    {
        Write(Path.Combine(_root, "x", "log.txt"), "first");
        Write(Path.Combine(_root, "y", "log.txt"), "second");
        (await Service.BackUpBeforeCleaningAsync(_s.CloudAccounts.Get(_account)!, [Path.Combine(_root, "x", "log.txt"), Path.Combine(_root, "y", "log.txt")], null, default)).Should().BeNull();
        _mem.Files.Keys.Should().HaveCount(2).And.OnlyContain(k => !k.Contains(".miu-versions"));
        CloudTexts.Should().BeEquivalentTo("first", "second");
    }

    [Fact]
    public async Task Back_up_first_needs_a_checksum()
    {
        Write(Path.Combine(_root, "x", "a.txt"), "a");
        _mem.NoHash = true;
        (await Service.BackUpBeforeCleaningAsync(_s.CloudAccounts.Get(_account)!, [Path.Combine(_root, "x", "a.txt")], null, default)).Should().Contain("checksum");
    }

    [Fact]
    public async Task A_garbled_reply_fails_one_file_not_the_whole_backup()
    {
        Write(Path.Combine(Photos, "a.txt"), "a");
        Write(Path.Combine(Photos, "b.txt"), "b");
        _mem.FailUpload = p => p.EndsWith("a.txt") ? throw new System.Text.Json.JsonException("<html>login</html>") : false;
        var o = await Service.RunAsync(Plan(BackupMode.CopyNew), "app");
        o.Status.Should().Be("partial");
        o.Uploaded.Should().Be(1);
        o.Summary.Should().Contain("didn't understand");
    }
}
