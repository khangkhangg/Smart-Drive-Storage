using Miu.Core.Automation;
using Miu.Data;

namespace Miu.Data.Tests;

public class AutomationRepositoriesTests
{
    private static MiuDatabase Db()
    {
        var db = new MiuDatabase(":memory:");
        db.Migrate();
        return db;
    }

    [Fact]
    public void Watches_roundtrip_update_delete()
    {
        var repo = new WatchesRepository(Db());
        var w = new Watch(0, "Downloads tidy", "/home/u/Downloads", WatchMode.Suggest,
            WatchSchedule.Weekly(DayOfWeek.Sunday, new TimeOnly(10, 0)), new WatchFilters(OlderThanDays: 30),
            NextRunLocal: new DateTime(2026, 11, 8, 10, 0, 0, DateTimeKind.Local));
        var id = repo.Add(w);
        var back = repo.All().Single();
        back.Id.Should().Be(id);
        back.Schedule.Should().Be(w.Schedule);
        back.Filters.OlderThanDays.Should().Be(30);
        back.NextRunLocal.Should().Be(w.NextRunLocal);
        repo.Update(back with { Enabled = false, LastResult = "Found 2.1 GB", LastRunUtc = DateTime.UtcNow });
        repo.All().Single().Enabled.Should().BeFalse();
        repo.All().Single().LastResult.Should().Be("Found 2.1 GB");
        repo.Delete(id);
        repo.All().Should().BeEmpty();
    }

    [Fact]
    public void Job_runs_are_recorded()
    {
        var db = Db();
        var watches = new WatchesRepository(db);
        var id = watches.Add(new Watch(0, "x", "/x", WatchMode.NotifyOnly, WatchSchedule.Daily(new TimeOnly(9, 0)), new WatchFilters()));
        var runs = new JobRunsRepository(db);
        runs.Add(id, DateTime.UtcNow.AddSeconds(-3), DateTime.UtcNow, "ok", 1234, "Found 1 item");
        runs.Recent(id, 5).Single().FreedBytes.Should().Be(1234);
    }

    [Fact]
    public void Snapshots_keep_folder_sizes_per_scan_root()
    {
        var repo = new SnapshotsRepository(Db());
        var t1 = DateTime.UtcNow.AddDays(-8);
        repo.Save("/home/u", t1, new Dictionary<string, long> { ["/home/u/Downloads"] = 10 });
        repo.Save("/home/u", DateTime.UtcNow, new Dictionary<string, long> { ["/home/u/Downloads"] = 30 });
        var older = repo.LatestBefore("/home/u", DateTime.UtcNow.AddDays(-5))!;
        older.Value.Folders["/home/u/Downloads"].Should().Be(10);
        repo.LatestBefore("/other", DateTime.UtcNow).Should().BeNull();
    }

    [Fact]
    public void Space_samples_by_volume()
    {
        var repo = new SpaceSamplesRepository(Db());
        repo.Add("C:\\", DateTime.UtcNow.AddDays(-3), 100, 500);
        repo.Add("C:\\", DateTime.UtcNow, 120, 500);
        repo.Add("D:\\", DateTime.UtcNow, 1, 2);
        repo.For("C:\\", 30).Should().HaveCount(2);
    }
}
