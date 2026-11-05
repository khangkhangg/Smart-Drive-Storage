namespace Miu.Data.Tests;

public sealed class DatabaseCareTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "miu-dbcare-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "miu.db");

    public DatabaseCareTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    [Fact]
    public void Daily_backups_keep_the_newest_three()
    {
        using var db = new MiuDatabase(DbPath);
        db.Migrate();
        new SettingsRepository(db).Set("k", "v");
        var t = new DateTime(2026, 11, 1, 8, 0, 0);
        for (var i = 0; i < 5; i++)
        {
            var made = DatabaseCare.BackupIfDue(db, DbPath, t.AddDays(i));
            made.Should().NotBeNull();
            File.SetLastWriteTime(made!, t.AddDays(i));
            DatabaseCare.BackupIfDue(db, DbPath, t.AddDays(i).AddHours(3)).Should().BeNull(); // not twice a day
        }
        DatabaseCare.Backups(DbPath).Should().HaveCount(3);
        DatabaseCare.Backups(DbPath)[0].Should().EndWith("miu-20261105-080000.db");
        DatabaseCare.IsHealthy(DatabaseCare.Backups(DbPath)[0]).Should().BeTrue();
    }

    [Fact]
    public void A_damaged_database_is_set_aside_and_the_newest_good_backup_restored()
    {
        using (var db = new MiuDatabase(DbPath))
        {
            db.Migrate();
            new SettingsRepository(db).Set("theme", "dark");
            DatabaseCare.BackupIfDue(db, DbPath, DateTime.Now);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.WriteAllBytes(DbPath, "this is not a database at all, it is garbage"u8.ToArray());
        foreach (var f in Directory.GetFiles(_dir, "miu.db-*")) File.Delete(f);

        var (restored, from) = DatabaseCare.OpenWithRecovery(DbPath);
        using (restored)
        {
            from.Should().StartWith("miu-");
            new SettingsRepository(restored).Get("theme").Should().Be("dark");
        }
        Directory.GetFiles(_dir, "miu.db.damaged-*").Should().ContainSingle(); // kept aside, not deleted
    }

    [Fact]
    public void A_healthy_or_new_database_opens_as_is()
    {
        var (db, from) = DatabaseCare.OpenWithRecovery(DbPath);
        using (db) from.Should().BeNull();
    }
}
