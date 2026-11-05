using Miu.Core.Automation;
using Miu.Core.Sync;

namespace Miu.Data.Tests;

public class SyncRepositoriesTests
{
    [Fact]
    public void Shares_roundtrip_without_any_password()
    {
        var db = new MiuDatabase(":memory:");
        db.Migrate();
        var repo = new NetworkSharesRepository(db);
        repo.Save(new NetworkShare(0, @"\\nas\photos", "Z:", "khang", true));
        repo.Save(new NetworkShare(0, @"\\nas\photos", "Y:", "khang", false)); // same share: updated, not duplicated
        var s = repo.All().Single();
        s.Letter.Should().Be("Y:");
        s.Reconnect.Should().BeFalse();
        s.SecretKey.Should().Be(@"net.\\nas\photos");
        repo.Delete(@"\\nas\photos");
        repo.All().Should().BeEmpty();
    }

    [Fact]
    public void Sync_jobs_roundtrip_and_patch()
    {
        var db = new MiuDatabase(":memory:");
        db.Migrate();
        var repo = new SyncJobsRepository(db);
        var id = repo.Add(new SyncJob(0, "Photos to NAS", "/home/k/Pictures", @"\\nas\photos", SyncMode.Mirror,
            WatchSchedule.Daily(new TimeOnly(2, 0)), CompareByHash: true));
        var j = repo.Get(id)!;
        j.Name.Should().Be("Photos to NAS");
        j.Mode.Should().Be(SyncMode.Mirror);
        j.Schedule!.Kind.Should().Be(ScheduleKind.Daily);
        j.CompareByHash.Should().BeTrue();
        repo.Patch(id, x => x with { Enabled = false, LastResult = "Copied 3 files." });
        repo.Get(id)!.Enabled.Should().BeFalse();
        repo.Add(new SyncJob(0, "Manual", "/a", "/b", SyncMode.TwoWay));
        repo.All().Should().HaveCount(2);
        repo.All()[1].Schedule.Should().BeNull();
        repo.Delete(id);
        repo.All().Should().ContainSingle();
    }

    [Fact]
    public void Baseline_is_null_until_saved_then_replaced()
    {
        var db = new MiuDatabase(":memory:");
        db.Migrate();
        var repo = new SyncBaselineRepository(db);
        repo.Get(1).Should().BeNull();
        var t = new DateTime(2026, 11, 5, 1, 2, 3, DateTimeKind.Utc);
        repo.Save(1, [], t);
        repo.Get(1).Should().BeEmpty(); // saved, but empty: not the same as never synced
        repo.Save(1, [new SyncEntry("a/b.txt", 5, t), new SyncEntry("c", 1, t)], t);
        repo.Save(1, [new SyncEntry("a/b.txt", 6, t)], t);
        repo.Get(1).Should().Equal(new SyncEntry("a/b.txt", 6, t));
        new SyncJobsRepository(db).Delete(1);
        repo.Get(1).Should().BeNull();
    }

    [Fact]
    public void Sync_runs_are_logged_by_sync_id()
    {
        var db = new MiuDatabase(":memory:");
        db.Migrate();
        var runs = new JobRunsRepository(db);
        var t = DateTime.UtcNow;
        runs.AddSync(7, t, t, "ok", 100, "Copied 1 file.");
        runs.Add(7, t, t, "ok", 0, "watch run");
        runs.RecentSync(7, 5).Single().Summary.Should().Be("Copied 1 file.");
        runs.Recent(7, 5).Single().Summary.Should().Be("watch run");
    }
}
