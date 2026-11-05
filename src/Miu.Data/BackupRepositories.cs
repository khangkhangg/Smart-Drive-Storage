using System.Text.Json;
using Miu.Core.Backup;
using Miu.Core.Rules;

namespace Miu.Data;

/// <summary>A connected cloud account. Its tokens or keys are never here: they live in the secret store under <see cref="SecretKey"/>.</summary>
public sealed record CloudAccount(long Id, string Provider, string DisplayName)
{
    public string SecretKey => SecretKeyFor(Id);
    public static string SecretKeyFor(long id) => "cloud." + id;
}

public sealed class CloudAccountsRepository(MiuDatabase db)
{
    public long Add(string provider, string displayName)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "INSERT INTO cloud_accounts(provider, display_name, cred_ref) VALUES ($p, $n, ''); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$p", provider);
        cmd.Parameters.AddWithValue("$n", displayName);
        var id = (long)cmd.ExecuteScalar()!;
        using var upd = db.Connection.CreateCommand();
        upd.CommandText = "UPDATE cloud_accounts SET cred_ref = $c WHERE id = $id";
        upd.Parameters.AddWithValue("$c", CloudAccount.SecretKeyFor(id));
        upd.Parameters.AddWithValue("$id", id);
        upd.ExecuteNonQuery();
        return id;
    }

    public IReadOnlyList<CloudAccount> All()
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT id, provider, display_name FROM cloud_accounts ORDER BY id";
        using var r = cmd.ExecuteReader();
        var list = new List<CloudAccount>();
        while (r.Read()) list.Add(new CloudAccount(r.GetInt64(0), r.GetString(1), r.GetString(2)));
        return list;
    }

    public CloudAccount? Get(long id) => All().FirstOrDefault(a => a.Id == id);

    public void Delete(long id)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM cloud_accounts WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }
}

public sealed class BackupPlansRepository(MiuDatabase db)
{
    public long Add(BackupPlan p)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "INSERT INTO backup_plans(account_id, json, enabled) VALUES ($a, '{}', 1); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$a", p.AccountId);
        var id = (long)cmd.ExecuteScalar()!;
        Update(p with { Id = id });
        return id;
    }

    public void Update(BackupPlan p)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE backup_plans SET account_id = $a, json = $j, enabled = $e, next_run = $n WHERE id = $id";
        cmd.Parameters.AddWithValue("$a", p.AccountId);
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(p, RuleLoader.JsonOptions));
        cmd.Parameters.AddWithValue("$e", p.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$n", (object?)(p.NextRunLocal is { } n ? Iso.Of(n) : null) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", p.Id);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<BackupPlan> All()
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT id, json, enabled FROM backup_plans ORDER BY id";
        using var r = cmd.ExecuteReader();
        var list = new List<BackupPlan>();
        while (r.Read())
            if (JsonSerializer.Deserialize<BackupPlan>(r.GetString(1), RuleLoader.JsonOptions) is { } p)
                list.Add(p with { Id = r.GetInt64(0), Enabled = r.GetInt64(2) == 1 });
        return list;
    }

    public BackupPlan? Get(long id) => All().FirstOrDefault(p => p.Id == id);

    public BackupPlan? Patch(long id, Func<BackupPlan, BackupPlan> change)
    {
        if (Get(id) is not { } current) return null;
        var updated = change(current) with { Id = id };
        Update(updated);
        return updated;
    }

    public void Delete(long id)
    {
        using var tx = db.Connection.BeginTransaction();
        foreach (var sql in new[] { "DELETE FROM backup_manifest WHERE plan_id = $id", "DELETE FROM backup_versions WHERE plan_id = $id", "DELETE FROM backup_plans WHERE id = $id" })
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

public sealed record BackupVersion(long Id, string LocalPath, string RemotePath, DateTime CreatedUtc, string Reason);

/// <summary>What each plan has in the cloud (written after every file, so a stopped run continues where it left off).</summary>
public sealed class BackupManifestRepository(MiuDatabase db)
{
    public IReadOnlyList<ManifestEntry> For(long planId)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT local_path, size, mtime, hash_kind, hash, ifnull(remote_path, remote_id), uploaded_at, freed FROM backup_manifest WHERE plan_id = $p";
        cmd.Parameters.AddWithValue("$p", planId);
        using var r = cmd.ExecuteReader();
        var list = new List<ManifestEntry>();
        while (r.Read())
            list.Add(new ManifestEntry(r.GetString(0), r.GetInt64(1), Iso.Parse(r.GetString(2)), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? "" : r.GetString(5), r.IsDBNull(6) ? DateTime.MinValue : Iso.Parse(r.GetString(6)), r.GetInt64(7) == 1));
        return list;
    }

    public void Save(long planId, ManifestEntry e)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO backup_manifest(plan_id, local_path, size, mtime, hash_kind, hash, remote_id, remote_path, uploaded_at, freed)
            VALUES ($p, $l, $s, $m, $k, $h, $r, $r, $u, $f)
            ON CONFLICT(plan_id, local_path) DO UPDATE SET size = excluded.size, mtime = excluded.mtime, hash_kind = excluded.hash_kind, hash = excluded.hash,
                remote_id = excluded.remote_id, remote_path = excluded.remote_path, uploaded_at = excluded.uploaded_at, freed = excluded.freed
            """;
        cmd.Parameters.AddWithValue("$p", planId);
        cmd.Parameters.AddWithValue("$l", e.LocalPath);
        cmd.Parameters.AddWithValue("$s", e.Size);
        cmd.Parameters.AddWithValue("$m", Iso.Of(e.MtimeUtc));
        cmd.Parameters.AddWithValue("$k", (object?)e.HashKind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$h", (object?)e.Hash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$r", e.RemotePath);
        cmd.Parameters.AddWithValue("$u", Iso.Of(e.UploadedUtc));
        cmd.Parameters.AddWithValue("$f", e.Freed ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public void Remove(long planId, string localPath)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM backup_manifest WHERE plan_id = $p AND local_path = $l";
        cmd.Parameters.AddWithValue("$p", planId);
        cmd.Parameters.AddWithValue("$l", localPath);
        cmd.ExecuteNonQuery();
    }

    public void AddVersion(long planId, string localPath, string remotePath, DateTime createdUtc, string reason)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "INSERT INTO backup_versions(plan_id, local_path, remote_path, created, reason) VALUES ($p, $l, $r, $c, $why)";
        cmd.Parameters.AddWithValue("$p", planId);
        cmd.Parameters.AddWithValue("$l", localPath);
        cmd.Parameters.AddWithValue("$r", remotePath);
        cmd.Parameters.AddWithValue("$c", Iso.Of(createdUtc));
        cmd.Parameters.AddWithValue("$why", reason);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Versions of one file, newest first.</summary>
    public IReadOnlyList<BackupVersion> Versions(long planId, string localPath)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT id, local_path, remote_path, created, reason FROM backup_versions WHERE plan_id = $p AND local_path = $l ORDER BY created DESC, id DESC";
        cmd.Parameters.AddWithValue("$p", planId);
        cmd.Parameters.AddWithValue("$l", localPath);
        using var r = cmd.ExecuteReader();
        var list = new List<BackupVersion>();
        while (r.Read()) list.Add(new BackupVersion(r.GetInt64(0), r.GetString(1), r.GetString(2), Iso.Parse(r.GetString(3)), r.GetString(4)));
        return list;
    }

    public void RemoveVersion(long id)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM backup_versions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }
}
