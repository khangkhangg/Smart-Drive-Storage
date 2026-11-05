using Miu.Core.Sync;
using Miu.Platform.Portable;

namespace Miu.Platform.Tests;

public sealed class SyncRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "miu-sync-" + Guid.NewGuid().ToString("N"));
    private string Src => Path.Combine(_root, "src");
    private string Dst => Path.Combine(_root, "dst");
    private static readonly DateTime Now = new(2026, 11, 5, 14, 30, 0, DateTimeKind.Local);
    private static readonly bool Ci = OperatingSystem.IsWindows();

    public SyncRunnerTests()
    {
        Directory.CreateDirectory(Src);
        Directory.CreateDirectory(Dst);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private static void Write(string path, string text, int minutesAgo = 60)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesAgo));
    }

    private async Task<(SyncPlan Plan, SyncRunResult Result, SyncRunner Runner)> SyncAsync(SyncMode mode, IReadOnlyList<SyncEntry>? baseline = null,
        Action<SyncListing, SyncListing>? beforeRun = null)
    {
        var s = SyncLister.List(Src, default);
        var t = SyncLister.List(Dst, default);
        var plan = SyncDiff.Plan(s.Entries, t.Entries, baseline, mode, Ci, DateOnly.FromDateTime(Now));
        beforeRun?.Invoke(s, t);
        var runner = new SyncRunner(Src, Dst, s.Entries!, t.Entries!, Ci, Now);
        return (plan, await runner.RunAsync(plan, null, default), runner);
    }

    [Fact]
    public void Lister_skips_links_trash_and_partials_and_reports_missing_roots()
    {
        Write(Path.Combine(Src, "a.txt"), "a");
        Write(Path.Combine(Src, "sub", "b.txt"), "bb");
        Write(Path.Combine(Src, ".miu-sync-trash", "x", "old.txt"), "old");
        Write(Path.Combine(Src, "c.txt.1-2.miu-partial"), "partial");
        Write(Path.Combine(Src, "Thumbs.db"), "junk");
        if (!OperatingSystem.IsWindows()) File.CreateSymbolicLink(Path.Combine(Src, "link"), _root);
        var l = SyncLister.List(Src, default);
        l.Entries!.Select(e => e.RelPath).Should().BeEquivalentTo("a.txt", "sub/b.txt");
        l.Entries!.Single(e => e.RelPath == "sub/b.txt").Size.Should().Be(2);
        SyncLister.List(Path.Combine(_root, "nope"), default).Entries.Should().BeNull();
    }

    [Fact]
    public async Task Mirror_copies_keeps_mtime_and_sets_aside_instead_of_deleting()
    {
        Write(Path.Combine(Src, "a.txt"), "new a", 120);
        Write(Path.Combine(Src, "sub", "b.txt"), "b");
        Write(Path.Combine(Dst, "a.txt"), "old a", 300);
        Write(Path.Combine(Dst, "extra.txt"), "extra");

        var (plan, r, runner) = await SyncAsync(SyncMode.Mirror);
        plan.Ops.Should().HaveCount(3);
        r.Copied.Should().Be(2);
        r.Archived.Should().Be(2); // the overwritten a.txt and extra.txt
        File.ReadAllText(Path.Combine(Dst, "a.txt")).Should().Be("new a");
        File.ReadAllText(Path.Combine(Dst, "sub", "b.txt")).Should().Be("b");
        File.Exists(Path.Combine(Dst, "extra.txt")).Should().BeFalse();
        File.GetLastWriteTimeUtc(Path.Combine(Dst, "a.txt")).Should().BeCloseTo(File.GetLastWriteTimeUtc(Path.Combine(Src, "a.txt")), TimeSpan.FromSeconds(1));
        var trash = runner.TrashPath(Side.Target);
        File.ReadAllText(Path.Combine(trash, "a.txt")).Should().Be("old a");
        File.ReadAllText(Path.Combine(trash, "extra.txt")).Should().Be("extra");
        r.ArchivedFiles.Select(a => a.OriginalPath).Should().BeEquivalentTo(Path.Combine(Dst, "a.txt"), Path.Combine(Dst, "extra.txt"));

        (await SyncAsync(SyncMode.Mirror)).Plan.IsEmpty.Should().BeTrue(); // the trash folder isn't seen as content
    }

    [Fact]
    public async Task A_file_changed_after_the_preview_is_left_alone()
    {
        Write(Path.Combine(Src, "a.txt"), "v1");
        Write(Path.Combine(Dst, "gone.txt"), "keep me");
        var (_, r, _) = await SyncAsync(SyncMode.Mirror, beforeRun: (_, _) =>
        {
            Write(Path.Combine(Src, "a.txt"), "v2 longer", 1);
            Write(Path.Combine(Dst, "gone.txt"), "edited since", 1);
        });
        r.ChangedDuringSync.Should().Be(2);
        File.Exists(Path.Combine(Dst, "a.txt")).Should().BeFalse();
        File.ReadAllText(Path.Combine(Dst, "gone.txt")).Should().Be("edited since");
    }

    [Fact]
    public async Task A_cancelled_copy_leaves_no_half_file_and_resumes_the_same_version()
    {
        var big = new byte[3 << 20];
        new Random(1).NextBytes(big);
        File.WriteAllBytes(Path.Combine(Src, "big.bin"), big);
        File.SetLastWriteTimeUtc(Path.Combine(Src, "big.bin"), DateTime.UtcNow.AddHours(-1));
        var s = SyncLister.List(Src, default);
        var t = SyncLister.List(Dst, default);
        var plan = SyncDiff.Plan(s.Entries, t.Entries, null, SyncMode.Mirror, Ci, DateOnly.FromDateTime(Now));

        // Stopped before it finished (cancelled up front, so a fast machine can't finish the copy first).
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var runner = new SyncRunner(Src, Dst, s.Entries!, t.Entries!, Ci, Now);
        await FluentActions.Awaiting(() => runner.RunAsync(plan, null, cts.Token)).Should().ThrowAsync<OperationCanceledException>();
        File.Exists(Path.Combine(Dst, "big.bin")).Should().BeFalse();

        // A partial from this exact version is resumed; one from another version is thrown away.
        var partial = Directory.GetFiles(Dst, "*.miu-partial").SingleOrDefault();
        if (partial is null)
        {
            var fp = $"{big.Length:x}-{s.Entries!.Single().MtimeUtc.Ticks:x}";
            partial = Path.Combine(Dst, $"big.bin.{fp}.miu-partial");
            File.WriteAllBytes(partial, big.AsSpan(0, 1 << 20).ToArray());
        }
        var already = new FileInfo(partial).Length;
        File.WriteAllText(Path.Combine(Dst, "big.bin.1-2.miu-partial"), "other version");
        var r = await new SyncRunner(Src, Dst, s.Entries!, t.Entries!, Ci, Now).RunAsync(plan, null, default);
        r.Copied.Should().Be(1);
        r.BytesCopied.Should().Be(big.Length - already); // resumed, not started again
        File.ReadAllBytes(Path.Combine(Dst, "big.bin")).Should().Equal(big);
        Directory.GetFiles(Dst, "*.miu-partial").Should().BeEmpty();
    }

    [Fact]
    public async Task Two_way_conflict_keeps_both_versions_on_both_sides()
    {
        Write(Path.Combine(Src, "notes.txt"), "source edit", 5);
        Write(Path.Combine(Dst, "notes.txt"), "target edit, older", 50);
        var (plan, r, _) = await SyncAsync(SyncMode.TwoWay);
        plan.Conflicts.Should().Be(1);
        r.Conflicts.Should().Be(1);
        r.Failed.Should().BeEmpty();
        var c = "notes (conflict 2026-11-05).txt";
        File.ReadAllText(Path.Combine(Src, "notes.txt")).Should().Be("source edit");
        File.ReadAllText(Path.Combine(Dst, "notes.txt")).Should().Be("source edit");
        File.ReadAllText(Path.Combine(Src, c)).Should().Be("target edit, older");
        File.ReadAllText(Path.Combine(Dst, c)).Should().Be("target edit, older");
        (await SyncAsync(SyncMode.TwoWay)).Plan.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task Refused_plans_do_not_run()
    {
        Write(Path.Combine(Dst, "a"), "a");
        var s = SyncLister.List(Src, default);
        var t = SyncLister.List(Dst, default);
        var plan = SyncDiff.Plan(s.Entries, t.Entries, null, SyncMode.Mirror, Ci, DateOnly.FromDateTime(Now));
        plan.Refusal.Should().NotBeNull();
        await FluentActions.Awaiting(() => new SyncRunner(Src, Dst, s.Entries!, t.Entries!, Ci, Now).RunAsync(plan, null, default))
            .Should().ThrowAsync<InvalidOperationException>();
        File.Exists(Path.Combine(Dst, "a")).Should().BeTrue();
    }

    private sealed class SyncInlineProgress(Action<SyncProgress> on) : IProgress<SyncProgress>
    {
        public void Report(SyncProgress value) => on(value);
    }
}
