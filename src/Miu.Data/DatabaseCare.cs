using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Miu.Data;

/// <summary>
/// Keeps Miu's database safe: a daily copy (the newest 3 are kept) and, if the database is damaged when Miu starts,
/// an automatic restore of the newest copy that passes its check. The damaged file is moved aside, never deleted.
/// </summary>
public static class DatabaseCare
{
    public const int Keep = 3;

    public static string BackupDir(string dbPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "backups");

    /// <summary>Opens the database, restoring the newest good backup first when it is damaged. <c>Recovered</c> names the backup used.</summary>
    public static (MiuDatabase Db, string? Recovered) OpenWithRecovery(string dbPath)
    {
        if (!File.Exists(dbPath) || IsHealthy(dbPath)) return (new MiuDatabase(dbPath), null);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(dbPath + suffix)) File.Move(dbPath + suffix, $"{dbPath}.damaged-{stamp}{suffix}");
        var good = Backups(dbPath).FirstOrDefault(IsHealthy);
        if (good is not null) File.Copy(good, dbPath);
        return (new MiuDatabase(dbPath), good is null ? "(no backup: starting fresh)" : Path.GetFileName(good));
    }

    /// <summary>Makes today's copy if the newest one is older than 20 hours. Returns the new file, or null.</summary>
    public static string? BackupIfDue(MiuDatabase db, string dbPath, DateTime nowLocal)
    {
        var dir = BackupDir(dbPath);
        var newest = Backups(dbPath).FirstOrDefault();
        if (newest is not null && nowLocal - File.GetLastWriteTime(newest) < TimeSpan.FromHours(20)) return null;
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"miu-{nowLocal.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.db");
        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText = "VACUUM INTO $t"; // a consistent copy, even while Miu is writing
            cmd.Parameters.AddWithValue("$t", target);
            cmd.ExecuteNonQuery();
        }
        foreach (var old in Backups(dbPath).Skip(Keep)) File.Delete(old);
        return target;
    }

    /// <summary>Backups, newest first.</summary>
    public static IReadOnlyList<string> Backups(string dbPath)
    {
        var dir = BackupDir(dbPath);
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "miu-*.db").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).ToList() : [];
    }

    public static bool IsHealthy(string path)
    {
        try
        {
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check";
            return cmd.ExecuteScalar() as string == "ok";
        }
        catch (SqliteException) { return false; }
    }
}
