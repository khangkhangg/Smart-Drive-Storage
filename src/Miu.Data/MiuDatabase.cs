using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Miu.Data;

/// <summary>The local SQLite database (one file in the app data folder) and its migrations.</summary>
public sealed class MiuDatabase : IDisposable
{
    public MiuDatabase(string filePath)
    {
        if (filePath != ":memory:" && Path.GetDirectoryName(Path.GetFullPath(filePath)) is { } dir) Directory.CreateDirectory(dir);
        Connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = filePath, ForeignKeys = true }.ToString());
        Connection.Open();
        if (filePath != ":memory:") Exec("PRAGMA journal_mode=WAL;");
    }

    public SqliteConnection Connection { get; }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Miu", "miu.db");

    public int SchemaVersion
    {
        get
        {
            Exec("CREATE TABLE IF NOT EXISTS schema_version(version INTEGER PRIMARY KEY, applied TEXT NOT NULL);");
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    /// <summary>Applies embedded Migrations/NNN_*.sql scripts newer than the current version, each in a transaction.</summary>
    public void Migrate()
    {
        var current = SchemaVersion;
        var asm = typeof(MiuDatabase).Assembly;
        var scripts = asm.GetManifestResourceNames()
            .Where(n => n.Contains(".Migrations.") && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n => (Name: n, Version: int.Parse(n.Split(".Migrations.")[1][..3])))
            .OrderBy(s => s.Version);

        foreach (var (name, version) in scripts)
        {
            if (version <= current) continue;
            using var tx = Connection.BeginTransaction();
            Exec(Read(asm, name), tx);
            using var cmd = Connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO schema_version(version, applied) VALUES ($v, $t)";
            cmd.Parameters.AddWithValue("$v", version);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    private static string Read(Assembly asm, string name)
    {
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private void Exec(string sql, SqliteTransaction? tx = null)
    {
        using var cmd = Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => Connection.Dispose();
}
