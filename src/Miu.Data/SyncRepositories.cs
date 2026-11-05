using System.Text.Json;
using Miu.Core.Rules;
using Miu.Core.Sync;

namespace Miu.Data;

/// <summary>A share Miu connected. The password is never here: it lives in the secret store under <see cref="SecretKey"/>.</summary>
public sealed record NetworkShare(long Id, string Unc, string? Letter, string? UserName, bool Reconnect)
{
    public string SecretKey => SecretKeyFor(Unc);
    public static string SecretKeyFor(string unc) => "net." + unc.ToLowerInvariant();
}

public sealed class NetworkSharesRepository(MiuDatabase db)
{
    /// <summary>Adds or updates the share with this UNC path.</summary>
    public long Save(NetworkShare s)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO network_shares(unc, letter, cred_ref, reconnect, user_name) VALUES ($u, $l, $c, $r, $n)
            ON CONFLICT(unc) DO UPDATE SET letter = excluded.letter, cred_ref = excluded.cred_ref, reconnect = excluded.reconnect, user_name = excluded.user_name;
            SELECT id FROM network_shares WHERE unc = $u;
            """;
        cmd.Parameters.AddWithValue("$u", s.Unc);
        cmd.Parameters.AddWithValue("$l", (object?)s.Letter ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$c", s.SecretKey);
        cmd.Parameters.AddWithValue("$r", s.Reconnect ? 1 : 0);
        cmd.Parameters.AddWithValue("$n", (object?)s.UserName ?? DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }

    public IReadOnlyList<NetworkShare> All()
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT id, unc, letter, user_name, reconnect FROM network_shares ORDER BY unc";
        using var r = cmd.ExecuteReader();
        var list = new List<NetworkShare>();
        while (r.Read())
            list.Add(new NetworkShare(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4) == 1));
        return list;
    }

    public void Delete(string unc)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM network_shares WHERE unc = $u";
        cmd.Parameters.AddWithValue("$u", unc);
        cmd.ExecuteNonQuery();
    }
}

public sealed class SyncJobsRepository(MiuDatabase db)
{
    private static string ModeText(SyncMode m) => m switch { SyncMode.Mirror => "mirror", SyncMode.CopyOnly => "copy", _ => "twoway" };

    public long Add(SyncJob j)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "INSERT INTO sync_jobs(source, target, mode, json, enabled) VALUES ($s, $t, $m, '{}', 1); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$s", j.Source);
        cmd.Parameters.AddWithValue("$t", j.Target);
        cmd.Parameters.AddWithValue("$m", ModeText(j.Mode));
        var id = (long)cmd.ExecuteScalar()!;
        Update(j with { Id = id });
        return id;
    }

    public void Update(SyncJob j)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE sync_jobs SET source = $s, target = $t, mode = $m, json = $j, enabled = $e, next_run = $n WHERE id = $id";
        cmd.Parameters.AddWithValue("$s", j.Source);
        cmd.Parameters.AddWithValue("$t", j.Target);
        cmd.Parameters.AddWithValue("$m", ModeText(j.Mode));
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(j, RuleLoader.JsonOptions));
        cmd.Parameters.AddWithValue("$e", j.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$n", (object?)(j.NextRunLocal is { } n ? Iso.Of(n) : null) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", j.Id);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<SyncJob> All()
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT id, json, enabled FROM sync_jobs ORDER BY id";
        using var r = cmd.ExecuteReader();
        var list = new List<SyncJob>();
        while (r.Read())
            if (JsonSerializer.Deserialize<SyncJob>(r.GetString(1), RuleLoader.JsonOptions) is { } j)
                list.Add(j with { Id = r.GetInt64(0), Enabled = r.GetInt64(2) == 1 });
        return list;
    }

    public SyncJob? Get(long id) => All().FirstOrDefault(j => j.Id == id);

    /// <summary>Changes the stored job (not a stale copy), like <see cref="WatchesRepository.Patch"/>.</summary>
    public SyncJob? Patch(long id, Func<SyncJob, SyncJob> change)
    {
        if (Get(id) is not { } current) return null;
        var updated = change(current) with { Id = id };
        Update(updated);
        return updated;
    }

    public void Delete(long id)
    {
        using var tx = db.Connection.BeginTransaction();
        foreach (var sql in new[] { "DELETE FROM sync_jobs WHERE id = $id", "DELETE FROM sync_baseline WHERE job_id = $id", "DELETE FROM sync_baseline_saved WHERE job_id = $id" })
        {
            using var cmd = db.Connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
}

/// <summary>What both sides of a two-way job agreed on after its last run. Without it, nothing is treated as deleted.</summary>
public sealed class SyncBaselineRepository(MiuDatabase db)
{
    /// <summary>null when the job has never finished a run (first run: Miu only adds, never removes).</summary>
    public IReadOnlyList<SyncEntry>? Get(long jobId)
    {
        using (var check = db.Connection.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM sync_baseline_saved WHERE job_id = $j";
            check.Parameters.AddWithValue("$j", jobId);
            if (check.ExecuteScalar() is null) return null;
        }
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT rel_path, size, mtime FROM sync_baseline WHERE job_id = $j";
        cmd.Parameters.AddWithValue("$j", jobId);
        using var r = cmd.ExecuteReader();
        var list = new List<SyncEntry>();
        while (r.Read()) list.Add(new SyncEntry(r.GetString(0), r.GetInt64(1), Iso.Parse(r.GetString(2))));
        return list;
    }

    public void Save(long jobId, IReadOnlyCollection<SyncEntry> entries, DateTime savedUtc)
    {
        using var tx = db.Connection.BeginTransaction();
        using (var del = db.Connection.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM sync_baseline WHERE job_id = $j";
            del.Parameters.AddWithValue("$j", jobId);
            del.ExecuteNonQuery();
        }
        using (var ins = db.Connection.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR REPLACE INTO sync_baseline(job_id, rel_path, size, mtime) VALUES ($j, $p, $s, $m)";
            var p = ins.Parameters.AddWithValue("$p", "");
            var s = ins.Parameters.AddWithValue("$s", 0L);
            var m = ins.Parameters.AddWithValue("$m", "");
            ins.Parameters.AddWithValue("$j", jobId);
            foreach (var e in entries)
            {
                p.Value = e.RelPath;
                s.Value = e.Size;
                m.Value = Iso.Of(e.MtimeUtc);
                ins.ExecuteNonQuery();
            }
        }
        using (var mark = db.Connection.CreateCommand())
        {
            mark.Transaction = tx;
            mark.CommandText = "INSERT OR REPLACE INTO sync_baseline_saved(job_id, saved_at) VALUES ($j, $t)";
            mark.Parameters.AddWithValue("$j", jobId);
            mark.Parameters.AddWithValue("$t", Iso.Of(savedUtc));
            mark.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
