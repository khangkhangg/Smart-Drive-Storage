using Miu.App.Services;
using Miu.Core.Sync;
using Miu.Platform.Abstractions;
using Miu.Platform.Portable;

namespace Miu.App.Tests;

/// <summary>Regression tests from the Milestone 6 review: links, cancel, Run anyway, edits during a run, baselines.</summary>
public sealed class SyncSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "miu-syncsafe-" + Guid.NewGuid().ToString("N"));
    private readonly AppServices _s = AppServices.Create(":memory:", new CleanupServiceTests.FakeBin(), secrets: new TelegramServiceTests.MemorySecrets(),
        networkDrives: new UnsupportedNetworkDrives("n/a"));
    private readonly DateTime _now = new(2026, 11, 5, 9, 0, 0, DateTimeKind.Local);
    private string Src => Path.Combine(_root, "src");
    private string Dst => Path.Combine(_root, "dst");
    private static bool CanSymlink => !OperatingSystem.IsWindows();

    public SyncSafetyTests()
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
    private SyncJob Job(SyncMode mode, string? target = null) => _s.SyncJobs.Get(_s.SyncJobs.Add(new SyncJob(0, "t", Src, target ?? Dst, mode)))!;

    private sealed class CancelAt(CancellationTokenSource cts, int done) : IProgress<SyncProgress>
    {
        public void Report(SyncProgress p) { if (p.Done == done) cts.Cancel(); }
    }

    [Fact]
    public async Task A_cancelled_run_still_records_what_it_set_aside()
    {
        Write(Path.Combine(Src, "keep.txt"), "k");
        for (var i = 0; i < 3; i++) Write(Path.Combine(Dst, $"old{i}.txt"), "old");
        var job = Job(SyncMode.Mirror);
        using var cts = new CancellationTokenSource();
        await FluentActions.Awaiting(() => Service.RunAsync(job, "app", null, new CancelAt(cts, 2), cts.Token)).Should().ThrowAsync<OperationCanceledException>();
        var action = _s.Actions.Recent(10).Single(a => a.Kind == "sync");
        action.Items.Should().HaveCount(Directory.GetFiles(Path.Combine(Dst, ".miu-sync-trash"), "*", SearchOption.AllDirectories).Length);
        _s.JobRuns.RecentSync(job.Id, 5).Single().Status.Should().Be("cancelled");
        (await Service.UndoAsync(action.Id)).Restored.Should().Be(action.Items.Count);
    }

    [Fact]
    public void A_target_reached_through_a_link_inside_the_source_is_nested()
    {
        if (!CanSymlink) return;
        Directory.CreateDirectory(Path.Combine(Src, "sub"));
        var link = Path.Combine(_root, "link-to-sub");
        Directory.CreateSymbolicLink(link, Path.Combine(Src, "sub"));
        Service.Validate(Src, link).Should().Contain("inside");
        Service.Validate(link, Src).Should().Contain("inside");
    }

    [Fact]
    public void A_link_to_a_protected_folder_is_refused()
    {
        if (!CanSymlink) return;
        var link = Path.Combine(_root, "usr-link");
        Directory.CreateSymbolicLink(link, "/usr");
        Service.Validate(link, Dst).Should().Contain("protected");
    }

    [Fact]
    public async Task Links_inside_a_root_are_left_alone_on_both_sides()
    {
        if (!CanSymlink) return;
        var outside = Path.Combine(_root, "outside");
        Write(Path.Combine(outside, "a.txt"), "precious outside file");
        Directory.CreateSymbolicLink(Path.Combine(Dst, "docs"), outside);
        Write(Path.Combine(Src, "docs", "a.txt"), "source version");
        Write(Path.Combine(Src, "b.txt"), "b");
        var outcome = await Service.RunAsync(Job(SyncMode.Mirror), "app");
        outcome.Status.Should().Be("ok");
        File.ReadAllText(Path.Combine(outside, "a.txt")).Should().Be("precious outside file");
        File.Exists(Path.Combine(Dst, "b.txt")).Should().BeTrue();
        (await Service.PreviewAsync(Job(SyncMode.Mirror))).Text.Should().Contain("link");
    }

    [Fact]
    public async Task Run_anyway_only_overrides_the_refusal_you_saw()
    {
        Write(Path.Combine(Src, "one.txt"), "1");
        for (var i = 0; i < 30; i++) Write(Path.Combine(Dst, $"f{i}.txt"), "x");
        var job = Job(SyncMode.Mirror);
        var preview = await Service.PreviewAsync(job);
        var seen = preview.Plan.Refusal!;
        File.Delete(Path.Combine(Src, "one.txt")); // meanwhile the source goes empty
        (await Service.RunAsync(job, "app", seen)).Status.Should().Be("refused");
        Directory.GetFiles(Dst).Should().HaveCount(30);
        // The "source is empty" refusal can never be overridden.
        var empty = (await Service.PreviewAsync(job)).Plan.Refusal!;
        (await Service.RunAsync(job, "app", empty)).Status.Should().Be("refused");
        Directory.GetFiles(Dst).Should().HaveCount(30);
        // The same refusal you saw does run.
        Write(Path.Combine(Src, "one.txt"), "1");
        seen = (await Service.PreviewAsync(job)).Plan.Refusal!;
        (await Service.RunAsync(job, "app", seen)).Result!.Archived.Should().Be(30);
    }

    [Fact]
    public async Task A_destination_edited_during_the_run_is_skipped_not_replaced()
    {
        Write(Path.Combine(Src, "doc.txt"), "base", 120);
        Write(Path.Combine(Dst, "doc.txt"), "base", 120);
        var baseline = SyncLister.List(Src, default).Entries!;
        Write(Path.Combine(Src, "doc.txt"), "source edit", 30);
        var s = SyncLister.List(Src, default);
        var t = SyncLister.List(Dst, default);
        var plan = SyncDiff.Plan(s.Entries, t.Entries, baseline, SyncMode.TwoWay, false, DateOnly.FromDateTime(_now));
        Write(Path.Combine(Dst, "doc.txt"), "target edit made during the run", 0);
        var r = await new SyncRunner(Src, Dst, s.Entries!, t.Entries!, false, _now).RunAsync(plan, null, default);
        r.ChangedDuringSync.Should().Be(1);
        File.ReadAllText(Path.Combine(Dst, "doc.txt")).Should().Be("target edit made during the run");
        r.ChangedPaths.Should().Equal("doc.txt");
    }

    [Fact]
    public async Task Files_that_merely_end_in_miu_partial_are_user_files()
    {
        Write(Path.Combine(Src, "a.txt"), "a");
        Write(Path.Combine(Src, "notes.miu-partial"), "mine");
        Write(Path.Combine(Dst, "a.txt.v2.miu-partial"), "user's own file, not Miu's");
        await Service.RunAsync(Job(SyncMode.Mirror), "app");
        File.Exists(Path.Combine(Dst, "notes.miu-partial")).Should().BeTrue();
        Directory.GetFiles(Path.Combine(Dst, ".miu-sync-trash"), "a.txt.v2.miu-partial", SearchOption.AllDirectories).Should().ContainSingle(); // set aside, not deleted
    }

    [Fact]
    public async Task A_failed_step_keeps_its_baseline_so_deletions_are_not_undone()
    {
        if (OperatingSystem.IsWindows()) return;
        Write(Path.Combine(Src, "x.txt"), "x", 120);
        Write(Path.Combine(Dst, "x.txt"), "x", 120);
        Write(Path.Combine(Src, "y.txt"), "y", 120);
        Write(Path.Combine(Dst, "y.txt"), "y", 120);
        var job = Job(SyncMode.TwoWay);
        (await Service.RunAsync(job, "app")).Status.Should().Be("nothing");
        File.Delete(Path.Combine(Dst, "x.txt"));
        File.WriteAllText(Path.Combine(Src, ".miu-sync-trash"), "blocker"); // the archive will fail
        (await Service.RunAsync(job, "app")).Status.Should().Be("partial");
        File.Delete(Path.Combine(Src, ".miu-sync-trash"));
        var next = await Service.PreviewAsync(job);
        next.Plan.Ops.Should().ContainSingle(op => op.RelPath == "x.txt" && op.Kind == SyncOpKind.Archive && op.From == Side.Source);
    }

    [Fact]
    public async Task A_target_that_cannot_be_created_fails_cleanly()
    {
        Write(Path.Combine(Src, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_root, "afile"), "x");
        var outcome = await Service.RunAsync(Job(SyncMode.Mirror, Path.Combine(_root, "afile", "sub")), "app");
        outcome.Status.Should().Be("failed");
    }

    [Fact]
    public void Names_that_differ_only_by_case_are_found()
    {
        SyncService.CaseTwins([new SyncEntry("A.txt", 1, _now), new SyncEntry("a.txt", 1, _now), new SyncEntry("dir/B", 1, _now), new SyncEntry("c", 1, _now)])
            .Should().BeEquivalentTo("A.txt", "a.txt");
        SyncLister.IsCaseInsensitive(Src).Should().Be(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());
        Directory.GetFiles(Src).Should().BeEmpty(); // the probe cleans up after itself
    }

    [Fact]
    public async Task Hash_equal_files_get_matching_dates_so_they_are_not_hashed_again()
    {
        Write(Path.Combine(Src, "same.bin"), "identical", 10);
        Write(Path.Combine(Dst, "same.bin"), "identical", 500);
        var id = _s.SyncJobs.Add(new SyncJob(0, "Hash", Src, Dst, SyncMode.TwoWay, CompareByHash: true));
        (await Service.RunAsync(_s.SyncJobs.Get(id)!, "app")).Status.Should().Be("nothing");
        File.GetLastWriteTimeUtc(Path.Combine(Dst, "same.bin")).Should().BeCloseTo(File.GetLastWriteTimeUtc(Path.Combine(Src, "same.bin")), TimeSpan.FromSeconds(1));
        _s.SyncBaseline.Get(id)!.Should().ContainSingle(e => e.RelPath == "same.bin");
    }
}
