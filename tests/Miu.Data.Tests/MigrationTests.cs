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
                db.SchemaVersion.Should().Be(7);
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

    [Fact]
    public void Activity_kinds_migration_keeps_every_row_and_allows_the_new_kinds()
    {
        using var db = new MiuDatabase(":memory:");
        db.Migrate();
        var actions = new ActionsRepository(db);
        var id = actions.Add("recycle", [new ActionItem("/a", "/trash/a", 5)], "cart");
        actions.MarkUndone(id);
        // Run the rebuild again on a table that has data: rows, ids and the undone flag survive.
        var asm = typeof(MiuDatabase).Assembly;
        using (var s = asm.GetManifestResourceStream(asm.GetManifestResourceNames().Single(n => n.EndsWith("007_activity_kinds.sql")))!)
        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText = new StreamReader(s).ReadToEnd();
            cmd.ExecuteNonQuery();
        }
        var row = actions.Get(id)!;
        row.Kind.Should().Be("recycle");
        row.Undone.Should().BeTrue();
        row.Items.Single().TrashLocation.Should().Be("/trash/a");
        foreach (var kind in new[] { "removed", "failed", "rule-added", "rule-changed" })
            actions.Add(kind, [new ActionItem("/x", "why", 0)], "cart").Should().BeGreaterThan(id);
    }
}
