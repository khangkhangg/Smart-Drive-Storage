using Miu.Core.Backup;

namespace Miu.Core.Tests.Backup;

public class BackupDiffTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\Users\k\Photos" : "/home/k/Photos";
    private static readonly BackupSource Src = new(Root, "Photos");

    private static string L(string rel) => Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
    private static LocalFile F(string rel, long size = 10, int daysOld = 1) => new(L(rel), size, Now.AddDays(-daysOld));
    private static ManifestEntry M(string rel, long size = 10, int daysOld = 1, bool freed = false) =>
        new(L(rel), size, Now.AddDays(-daysOld), "md5", "abc", "Backup/Photos/" + rel, Now.AddDays(-1), freed);

    private static BackupPlan Plan(BackupMode mode, int freeAfter = 30) => new(1, "Photos", 1, [Src], "Backup", mode, FreeAfterDays: freeAfter);

    private static BackupPlanResult Diff(BackupMode mode, IReadOnlyList<LocalFile>? local, ManifestEntry[] manifest, int freeAfter = 30) =>
        BackupDiff.Plan(Plan(mode, freeAfter), new Dictionary<BackupSource, IReadOnlyList<LocalFile>?> { [Src] = local }, [], manifest, Now, OperatingSystem.IsWindows());

    [Fact]
    public void Uploads_new_and_changed_files_to_their_remote_paths()
    {
        var r = Diff(BackupMode.CopyNew, [F("a.jpg"), F("2024/b.jpg", 20), F("same.jpg")], [M("2024/b.jpg", 10), M("same.jpg"), M("gone.jpg")]);
        r.Refusal.Should().BeNull();
        r.Ops.Should().BeEquivalentTo(new[]
        {
            new BackupOp(BackupOpKind.Upload, L("a.jpg"), "Backup/Photos/a.jpg", 10, "new"),
            new BackupOp(BackupOpKind.Upload, L("2024/b.jpg"), "Backup/Photos/2024/b.jpg", 20, "changed"),
        });
    }

    [Fact]
    public void Mirror_sets_aside_files_removed_locally_but_never_freed_ones()
    {
        var r = Diff(BackupMode.Mirror, [F("keep.jpg")], [M("keep.jpg"), M("deleted.jpg"), M("freed.jpg", freed: true)]);
        r.Ops.Should().BeEquivalentTo(new[] { new BackupOp(BackupOpKind.SetAside, L("deleted.jpg"), "Backup/Photos/deleted.jpg", 10, "deleted on this computer") });
    }

    [Fact]
    public void Backup_then_free_frees_only_old_files_that_are_or_will_be_backed_up()
    {
        var r = Diff(BackupMode.BackupThenFree, [F("old.jpg", daysOld: 40), F("old-new.jpg", daysOld: 60), F("recent.jpg", daysOld: 3), F("old-changed.jpg", 99, 45)],
            [M("old.jpg", daysOld: 40), M("recent.jpg", daysOld: 3), M("old-changed.jpg", 10, 45)]);
        r.Ops.Where(o => o.Kind == BackupOpKind.Upload).Select(o => o.LocalPath).Should().BeEquivalentTo(L("old-new.jpg"), L("old-changed.jpg"));
        r.Ops.Where(o => o.Kind == BackupOpKind.Free).Select(o => o.LocalPath).Should().BeEquivalentTo(L("old.jpg"), L("old-new.jpg"), L("old-changed.jpg"));
        r.FreeBytes.Should().Be(10 + 10 + 99);
    }

    [Fact]
    public void A_freed_file_that_comes_back_changed_is_uploaded_again()
    {
        Diff(BackupMode.CopyNew, [F("x.jpg", 50)], [M("x.jpg", 10, freed: true)]).Ops.Single().Reason.Should().Be("changed");
        Diff(BackupMode.CopyNew, [F("x.jpg")], [M("x.jpg", freed: true)]).Ops.Should().BeEmpty();
    }

    [Fact]
    public void A_missing_source_is_skipped_and_mirror_is_refused()
    {
        var r = Diff(BackupMode.CopyNew, null, [M("a.jpg")]);
        r.Ops.Should().BeEmpty();
        r.MissingSources.Should().Equal(Root);
        r.Refusal.Should().BeNull();
        var m = Diff(BackupMode.Mirror, null, [M("a.jpg")]);
        m.Refusal.Should().Contain("Photos");
        m.RefusalIsFinal.Should().BeTrue();
    }

    [Fact]
    public void Mirror_brake_for_an_empty_source_or_mass_removal()
    {
        var manifest = Enumerable.Range(0, 40).Select(i => M($"f{i}.jpg")).ToArray();
        var empty = Diff(BackupMode.Mirror, [], manifest);
        empty.Refusal.Should().Contain("empty");
        empty.RefusalIsFinal.Should().BeTrue();
        var mass = Diff(BackupMode.Mirror, manifest.Take(10).Select(e => new LocalFile(e.LocalPath, e.Size, e.MtimeUtc)).ToList(), manifest);
        mass.Refusal.Should().Contain("30 of 40");
        mass.RefusalIsFinal.Should().BeFalse();
    }

    [Fact]
    public void Every_source_is_braked_and_an_empty_one_makes_the_refusal_final()
    {
        var a = new BackupSource(Root, "Photos");
        var bRoot = Root + "-B";
        var b = new BackupSource(bRoot, "Other");
        var plan = new BackupPlan(1, "p", 1, [a, b], "Backup", BackupMode.Mirror);
        var manifest = Enumerable.Range(0, 30).Select(i => M($"f{i}.jpg"))
            .Concat(Enumerable.Range(0, 10).Select(i => new ManifestEntry(Path.Combine(bRoot, $"g{i}"), 1, Now, "md5", "x", $"Backup/Other/g{i}", Now))).ToArray();
        var local = new Dictionary<BackupSource, IReadOnlyList<LocalFile>?> { [a] = Enumerable.Range(0, 5).Select(i => F($"f{i}.jpg")).ToList(), [b] = [] };
        var r = BackupDiff.Plan(plan, local, [], manifest, Now, false);
        r.Refusal.Should().Contain("25 of 30").And.Contain("empty");
        r.RefusalIsFinal.Should().BeTrue();
    }

    [Fact]
    public void Files_under_unknown_folders_are_left_alone()
    {
        var r = BackupDiff.Plan(Plan(BackupMode.Mirror), new Dictionary<BackupSource, IReadOnlyList<LocalFile>?> { [Src] = [F("ok.jpg")] },
            [L("locked")], [M("ok.jpg"), M("locked/x.jpg")], Now, false);
        r.Ops.Should().BeEmpty();
    }

    [Fact]
    public void Remote_paths_are_safe_and_versions_have_a_stamp()
    {
        BackupPaths.Remote("Backup", "Photos", "2024/a b+c.jpg").Should().Be("Backup/Photos/2024/a b+c.jpg");
        BackupPaths.Version("Backup", "Backup/Photos/2024/a.jpg", new DateTime(2026, 11, 5, 14, 3, 9)).Should().Be("Backup/.miu-versions/2026-11-05 140309/Photos/2024/a.jpg");
        BackupPaths.IsSafeRelative("a/b.txt").Should().BeTrue();
        foreach (var bad in new[] { "../x", "a/../../x", "/etc/passwd", @"C:\x", "a\\..\\x", "" }) BackupPaths.IsSafeRelative(bad).Should().BeFalse(bad);
    }
}
