using Miu.Data;

namespace Miu.Data.Tests;

public class MigrationTests
{
    private static readonly string[] Tables =
    [
        "settings", "volumes", "scans", "folder_snapshots", "rules", "decisions", "rule_suggestions", "watches",
        "job_runs", "actions_log", "cloud_accounts", "backup_plans", "backup_manifest", "network_shares",
        "sync_jobs", "telegram", "pending_remote_actions",
    ];

    private static List<string> TableNames(MiuDatabase db)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        using var r = cmd.ExecuteReader();
        var names = new List<string>();
        while (r.Read()) names.Add(r.GetString(0));
        return names;
    }

    [Fact]
    public void Migrate_on_empty_db_creates_all_tables()
    {
        using var db = new MiuDatabase(":memory:");
        db.Migrate();
        TableNames(db).Should().Contain(Tables);
        db.SchemaVersion.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void Migrate_is_idempotent()
    {
        var file = Path.Combine(Path.GetTempPath(), $"miu-{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new MiuDatabase(file)) db.Migrate();
            using (var db = new MiuDatabase(file))
            {
                db.Migrate();
                db.SchemaVersion.Should().Be(3);
                TableNames(db).Should().Contain(Tables);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(file) + "*")) File.Delete(f);
            File.Delete(file);
        }
    }
}
