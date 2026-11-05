using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Miu.Core.Automation;
using Miu.Core.Rules;

namespace Miu.Data;

internal static class Iso
{
    public static string Of(DateTime t) => t.ToString("O", CultureInfo.InvariantCulture);
    public static DateTime Parse(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

/// <summary>Watched folders and their schedules.</summary>
public sealed class WatchesRepository(MiuDatabase db)
{
    public long Add(Watch w)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "INSERT INTO watches(path, json, enabled, next_run) VALUES ($p, '{}', $e, $n); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$p", w.Path);
        cmd.Parameters.AddWithValue("$e", w.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$n", (object?)(w.NextRunLocal is { } n ? Iso.Of(n) : null) ?? DBNull.Value);
        var id = (long)cmd.ExecuteScalar()!;
        Update(w with { Id = id });
        return id;
    }

    public void Update(Watch w)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE watches SET path = $p, json = $j, enabled = $e, next_run = $n WHERE id = $id";
        cmd.Parameters.AddWithValue("$p", w.Path);
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(w, RuleLoader.JsonOptions));
        cmd.Parameters.AddWithValue("$e", w.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$n", (object?)(w.NextRunLocal is { } n ? Iso.Of(n) : null) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", w.Id);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<Watch> All()
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT id, json, enabled FROM watches ORDER BY id";
        using var r = cmd.ExecuteReader();
        var list = new List<Watch>();
        while (r.Read())
            if (JsonSerializer.Deserialize<Watch>(r.GetString(1), RuleLoader.JsonOptions) is { } w)
                list.Add(w with { Id = r.GetInt64(0), Enabled = r.GetInt64(2) == 1, Filters = w.Filters ?? new WatchFilters() });
        return list;
    }

    public Watch? Get(long id) => All().FirstOrDefault(w => w.Id == id);

    /// <summary>
    /// Changes a watch based on what is stored now (not on a copy that may be minutes old), so a run finishing
    /// later never reverts an edit, a toggle or a newer next-run time.
    /// </summary>
    public Watch? Patch(long id, Func<Watch, Watch> change)
    {
        if (Get(id) is not { } current) return null;
        var updated = change(current) with { Id = id };
        Update(updated);
        return updated;
    }

    public void Delete(long id)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM watches WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }
}

public sealed record JobRun(long WatchId, DateTime StartedUtc, DateTime FinishedUtc, string Status, long FreedBytes, string Summary);

public sealed class JobRunsRepository(MiuDatabase db)
{
    public void Add(long watchId, DateTime startedUtc, DateTime finishedUtc, string status, long freedBytes, string summary)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "INSERT INTO job_runs(watch_id, started, finished, status, freed_bytes, summary_json) VALUES ($w, $s, $f, $st, $b, $sum)";
        cmd.Parameters.AddWithValue("$w", watchId);
        cmd.Parameters.AddWithValue("$s", Iso.Of(startedUtc));
        cmd.Parameters.AddWithValue("$f", Iso.Of(finishedUtc));
        cmd.Parameters.AddWithValue("$st", status);
        cmd.Parameters.AddWithValue("$b", freedBytes);
        cmd.Parameters.AddWithValue("$sum", summary);
        cmd.ExecuteNonQuery();
    }

    /// <summary>A sync job run (stored with sync_id, not watch_id).</summary>
    public void AddSync(long syncId, DateTime startedUtc, DateTime finishedUtc, string status, long bytes, string summary)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "INSERT INTO job_runs(sync_id, started, finished, status, freed_bytes, summary_json) VALUES ($w, $s, $f, $st, $b, $sum);" +
                          "DELETE FROM job_runs WHERE sync_id = $w AND id NOT IN (SELECT id FROM job_runs WHERE sync_id = $w ORDER BY id DESC LIMIT 50)";
        cmd.Parameters.AddWithValue("$w", syncId);
        cmd.Parameters.AddWithValue("$s", Iso.Of(startedUtc));
        cmd.Parameters.AddWithValue("$f", Iso.Of(finishedUtc));
        cmd.Parameters.AddWithValue("$st", status);
        cmd.Parameters.AddWithValue("$b", bytes);
        cmd.Parameters.AddWithValue("$sum", summary);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<JobRun> RecentSync(long syncId, int n)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT sync_id, started, finished, status, freed_bytes, summary_json FROM job_runs WHERE sync_id = $w ORDER BY id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$w", syncId);
        cmd.Parameters.AddWithValue("$n", n);
        using var r = cmd.ExecuteReader();
        var list = new List<JobRun>();
        while (r.Read())
            list.Add(new JobRun(r.GetInt64(0), Iso.Parse(r.GetString(1)), Iso.Parse(r.GetString(2)), r.GetString(3), r.GetInt64(4), r.IsDBNull(5) ? "" : r.GetString(5)));
        return list;
    }

    public void Prune(long watchId, int keep)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM job_runs WHERE watch_id = $w AND id NOT IN (SELECT id FROM job_runs WHERE watch_id = $w ORDER BY id DESC LIMIT $k)";
        cmd.Parameters.AddWithValue("$w", watchId);
        cmd.Parameters.AddWithValue("$k", keep);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<JobRun> Recent(long watchId, int n)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT watch_id, started, finished, status, freed_bytes, summary_json FROM job_runs WHERE watch_id = $w ORDER BY id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$w", watchId);
        cmd.Parameters.AddWithValue("$n", n);
        using var r = cmd.ExecuteReader();
        var list = new List<JobRun>();
        while (r.Read())
            list.Add(new JobRun(r.GetInt64(0), Iso.Parse(r.GetString(1)), Iso.Parse(r.GetString(2)), r.GetString(3), r.GetInt64(4), r.IsDBNull(5) ? "" : r.GetString(5)));
        return list;
    }
}

/// <summary>Folder sizes saved after each scan, so Miu can tell what grew.</summary>
public sealed class SnapshotsRepository(MiuDatabase db)
{
    public void Save(string root, DateTime tsUtc, IReadOnlyDictionary<string, long> folders)
    {
        using var tx = db.Connection.BeginTransaction();
        long scanId;
        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO scans(root, started, finished, method, total_size, file_count) VALUES ($r, $t, $t, 'managed', $s, 0); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$r", root);
            cmd.Parameters.AddWithValue("$t", Iso.Of(tsUtc));
            cmd.Parameters.AddWithValue("$s", folders.Values.Sum());
            scanId = (long)cmd.ExecuteScalar()!;
        }
        foreach (var (path, size) in folders)
        {
            using var cmd = db.Connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO folder_snapshots(scan_id, path_hash, path, size, file_count) VALUES ($s, $h, $p, $z, 0)";
            cmd.Parameters.AddWithValue("$s", scanId);
            cmd.Parameters.AddWithValue("$h", BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(path)), 0));
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$z", size);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Keeps only the newest snapshots of a scan root.</summary>
    public void Prune(string root, int keep)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM folder_snapshots WHERE scan_id IN (
              SELECT id FROM scans WHERE root = $r AND id NOT IN (SELECT id FROM scans WHERE root = $r ORDER BY started DESC LIMIT $k));
            DELETE FROM scans WHERE root = $r AND id NOT IN (SELECT id FROM scans WHERE root = $r ORDER BY started DESC LIMIT $k);
            """;
        cmd.Parameters.AddWithValue("$r", root);
        cmd.Parameters.AddWithValue("$k", keep);
        cmd.ExecuteNonQuery();
    }

    /// <summary>The newest snapshot of this scan root taken before the given time.</summary>
    public (DateTime Ts, Dictionary<string, long> Folders)? LatestBefore(string root, DateTime beforeUtc)
    {
        long id;
        DateTime ts;
        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id, started FROM scans WHERE root = $r AND started < $b ORDER BY started DESC LIMIT 1";
            cmd.Parameters.AddWithValue("$r", root);
            cmd.Parameters.AddWithValue("$b", Iso.Of(beforeUtc));
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            id = r.GetInt64(0);
            ts = Iso.Parse(r.GetString(1));
        }
        using var c2 = db.Connection.CreateCommand();
        c2.CommandText = "SELECT path, size FROM folder_snapshots WHERE scan_id = $s";
        c2.Parameters.AddWithValue("$s", id);
        using var r2 = c2.ExecuteReader();
        var folders = new Dictionary<string, long>();
        while (r2.Read()) folders[r2.GetString(0)] = r2.GetInt64(1);
        return (ts, folders);
    }
}

/// <summary>How full each drive was over time (for the "full in ~N weeks" forecast).</summary>
public sealed class SpaceSamplesRepository(MiuDatabase db)
{
    public void Add(string root, DateTime tsUtc, long used, long total)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "INSERT INTO space_samples(root, ts, used, total) VALUES ($r, $t, $u, $T)";
        cmd.Parameters.AddWithValue("$r", root);
        cmd.Parameters.AddWithValue("$t", Iso.Of(tsUtc));
        cmd.Parameters.AddWithValue("$u", used);
        cmd.Parameters.AddWithValue("$T", total);
        cmd.ExecuteNonQuery();
    }

    public void Prune(int days)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM space_samples WHERE ts < $t";
        cmd.Parameters.AddWithValue("$t", Iso.Of(DateTime.UtcNow.AddDays(-days)));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(DateTime Ts, long Used, long Total)> For(string root, int days)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT ts, used, total FROM space_samples WHERE root = $r AND ts >= $since ORDER BY ts";
        cmd.Parameters.AddWithValue("$r", root);
        cmd.Parameters.AddWithValue("$since", Iso.Of(DateTime.UtcNow.AddDays(-days)));
        using var r = cmd.ExecuteReader();
        var list = new List<(DateTime, long, long)>();
        while (r.Read()) list.Add((Iso.Parse(r.GetString(0)), r.GetInt64(1), r.GetInt64(2)));
        return list;
    }
}
