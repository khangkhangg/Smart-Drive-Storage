using Miu.Core.Automation;
using Miu.Core.Backup;

namespace Miu.Data.Tests;

public class BackupRepositoriesTests
{
    private static MiuDatabase Db()
    {
        var db = new MiuDatabase(":memory:");
        db.Migrate();
        return db;
    }

    [Fact]
    public void Accounts_keep_only_a_secret_reference()
    {
        var repo = new CloudAccountsRepository(Db());
        var id = repo.Add("s3", "Backblaze B2 · photos");
        var a = repo.Get(id)!;
        a.Provider.Should().Be("s3");
        a.SecretKey.Should().Be("cloud." + id);
        repo.Delete(id);
        repo.All().Should().BeEmpty();
    }

    [Fact]
    public void Plans_roundtrip_and_delete_their_manifest()
    {
        var db = Db();
        var plans = new BackupPlansRepository(db);
        var account = new CloudAccountsRepository(db).Add("gdrive", "Google Drive");
        var id = plans.Add(new BackupPlan(0, "Photos", account, [new BackupSource("/home/k/Photos", "Photos")], "Miu Backup", BackupMode.BackupThenFree,
            WatchSchedule.Weekly(DayOfWeek.Sunday, new TimeOnly(3, 0)), KeepVersions: 3, FreeAfterDays: 60, BandwidthKBps: 500));
        var p = plans.Get(id)!;
        p.Sources.Single().RemoteName.Should().Be("Photos");
        p.Mode.Should().Be(BackupMode.BackupThenFree);
        p.FreeAfterDays.Should().Be(60);
        p.Schedule!.Day.Should().Be(DayOfWeek.Sunday);
        plans.Patch(id, x => x with { LastResult = "Uploaded 3 files." })!.LastResult.Should().Be("Uploaded 3 files.");

        var manifest = new BackupManifestRepository(db);
        var t = new DateTime(2026, 11, 1, 1, 2, 3, DateTimeKind.Utc);
        manifest.Save(id, new ManifestEntry("/home/k/Photos/a.jpg", 10, t, "md5", "h1", "Miu Backup/Photos/a.jpg", t));
        manifest.Save(id, new ManifestEntry("/home/k/Photos/a.jpg", 11, t, "md5", "h2", "Miu Backup/Photos/a.jpg", t, Freed: true));
        manifest.For(id).Single().Should().Be(new ManifestEntry("/home/k/Photos/a.jpg", 11, t, "md5", "h2", "Miu Backup/Photos/a.jpg", t, true));
        manifest.AddVersion(id, "/home/k/Photos/a.jpg", "v1", t, "changed");
        manifest.AddVersion(id, "/home/k/Photos/a.jpg", "v2", t.AddDays(1), "changed");
        manifest.Versions(id, "/home/k/Photos/a.jpg").Select(v => v.RemotePath).Should().Equal("v2", "v1");

        plans.Delete(id);
        manifest.For(id).Should().BeEmpty();
        manifest.Versions(id, "/home/k/Photos/a.jpg").Should().BeEmpty();
    }
}
