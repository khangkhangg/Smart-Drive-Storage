using Miu.Core.Sync;

namespace Miu.Core.Tests.Sync;

public class SyncDiffTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 11, 5);

    private static SyncEntry E(string rel, long size = 10, int minutes = 0) => new(rel, size, T0.AddMinutes(minutes));

    private static SyncPlan Plan(SyncMode mode, SyncEntry[] s, SyncEntry[]? t, SyncEntry[]? b = null, bool ci = false, ISet<string>? same = null) =>
        SyncDiff.Plan(s, t, b, mode, ci, Today, same);

    [Fact]
    public void Mirror_copies_new_and_changed_and_archives_extra()
    {
        var p = Plan(SyncMode.Mirror,
            [E("a.txt"), E("b.txt", 20), E("sub/c.txt")],
            [E("b.txt", 10), E("old.txt"), E("sub/c.txt")]);
        p.Refusal.Should().BeNull();
        p.Ops.Should().BeEquivalentTo(new[]
        {
            new SyncOp(SyncOpKind.Copy, Side.Source, "a.txt", 10, "new"),
            new SyncOp(SyncOpKind.Copy, Side.Source, "b.txt", 20, "changed"),
            new SyncOp(SyncOpKind.Archive, Side.Target, "old.txt", 10, "not in the source"),
        });
    }

    [Fact]
    public void Mtime_within_two_seconds_counts_as_same()
    {
        var p = Plan(SyncMode.Mirror, [new SyncEntry("a", 5, T0)], [new SyncEntry("a", 5, T0.AddSeconds(2))]);
        p.Ops.Should().BeEmpty();
        Plan(SyncMode.Mirror, [new SyncEntry("a", 5, T0)], [new SyncEntry("a", 5, T0.AddSeconds(3))]).Ops.Should().ContainSingle();
    }

    [Fact]
    public void Same_content_by_hash_is_not_copied()
    {
        Plan(SyncMode.Mirror, [E("a", 5, 0)], [E("a", 5, 60)], same: new HashSet<string> { "a" }).Ops.Should().BeEmpty();
    }

    [Fact]
    public void Copy_only_never_archives_and_keeps_a_newer_target()
    {
        var p = Plan(SyncMode.CopyOnly, [E("new"), E("newer", 5, 10), E("older", 5, 0)], [E("newer", 6, 0), E("older", 6, 10), E("extra")]);
        p.Ops.Should().BeEquivalentTo(new[]
        {
            new SyncOp(SyncOpKind.Copy, Side.Source, "new", 10, "new"),
            new SyncOp(SyncOpKind.Copy, Side.Source, "newer", 5, "newer in the source"),
        });
        p.KeptNewerOnTarget.Should().Be(1);
    }

    [Fact]
    public void Mirror_with_an_empty_source_is_refused()
    {
        var p = Plan(SyncMode.Mirror, [], [E("a"), E("b")]);
        p.Refusal.Should().Contain("empty");
    }

    [Fact]
    public void Unreadable_target_is_refused()
    {
        Plan(SyncMode.Mirror, [E("a")], null).Refusal.Should().Contain("read");
    }

    [Fact]
    public void Mass_archive_is_refused_but_small_cleanups_are_fine()
    {
        var target = Enumerable.Range(0, 40).Select(i => E($"f{i}")).ToArray();
        var keep = target.Take(10).ToArray(); // 30 of 40 would be archived
        Plan(SyncMode.Mirror, keep, target).Refusal.Should().Contain("30");
        Plan(SyncMode.Mirror, target.Take(30).ToArray(), target).Refusal.Should().BeNull(); // 10 of 40
        Plan(SyncMode.Mirror, [E("x")], [E("x"), E("y"), E("z")]).Refusal.Should().BeNull(); // few files: fine
    }

    [Fact]
    public void Two_way_first_run_unions_and_never_archives()
    {
        var p = Plan(SyncMode.TwoWay, [E("s-only"), E("both"), E("differ", 1, 5)], [E("t-only"), E("both"), E("differ", 2, 0)]);
        p.Ops.Should().BeEquivalentTo(new[]
        {
            new SyncOp(SyncOpKind.Copy, Side.Source, "s-only", 10, "new"),
            new SyncOp(SyncOpKind.Copy, Side.Target, "t-only", 10, "new"),
            new SyncOp(SyncOpKind.Conflict, Side.Source, "differ", 1, "changed on both sides", "differ (conflict 2026-11-05)"),
        });
    }

    [Fact]
    public void Two_way_with_baseline_follows_edits_and_deletes()
    {
        SyncEntry[] baseline = [E("edited-s"), E("edited-t"), E("gone-s"), E("gone-t"), E("del-s-edit-t"), E("both", 10)];
        var p = Plan(SyncMode.TwoWay,
            [E("edited-s", 11), E("edited-t"), E("gone-t"), E("both", 12, 5)],
            [E("edited-s"), E("edited-t", 12), E("gone-s"), E("del-s-edit-t", 13), E("both", 13, 1)],
            baseline);
        p.Ops.Should().BeEquivalentTo(new[]
        {
            new SyncOp(SyncOpKind.Copy, Side.Source, "edited-s", 11, "changed"),
            new SyncOp(SyncOpKind.Copy, Side.Target, "edited-t", 12, "changed"),
            new SyncOp(SyncOpKind.Archive, Side.Target, "gone-s", 10, "deleted in the source"),
            new SyncOp(SyncOpKind.Archive, Side.Source, "gone-t", 10, "deleted in the target"),
            new SyncOp(SyncOpKind.Copy, Side.Target, "del-s-edit-t", 13, "edited in the target after it was deleted in the source"),
            new SyncOp(SyncOpKind.Conflict, Side.Source, "both", 12, "changed on both sides", "both (conflict 2026-11-05)"),
        });
    }

    [Fact]
    public void Two_way_side_that_vanished_is_refused()
    {
        SyncEntry[] baseline = Enumerable.Range(0, 30).Select(i => E($"f{i}")).ToArray();
        Plan(SyncMode.TwoWay, baseline, [], baseline).Refusal.Should().NotBeNull();
    }

    [Fact]
    public void Conflict_names_avoid_existing_files()
    {
        var p = Plan(SyncMode.TwoWay, [E("dir/a.txt", 1, 5), E("dir/a (conflict 2026-11-05).txt")], [E("dir/a.txt", 2)]);
        p.Ops.Single(o => o.Kind == SyncOpKind.Conflict).ConflictName.Should().Be("dir/a (conflict 2026-11-05 2).txt");
    }

    [Fact]
    public void Case_insensitive_paths_match()
    {
        Plan(SyncMode.Mirror, [E("Docs/A.txt")], [E("docs/a.txt")], ci: true).Ops.Should().BeEmpty();
        Plan(SyncMode.Mirror, [E("Docs/A.txt")], [E("docs/a.txt")], ci: false).Ops.Should().HaveCount(2);
    }
}
