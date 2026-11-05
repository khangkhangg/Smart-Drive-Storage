namespace Miu.Data;

/// <summary>Actions offered in a Telegram message, waiting for a tap. Each runs at most once and expires.</summary>
public sealed class RemoteActionsRepository(MiuDatabase db)
{
    public long Create(string payloadJson, DateTime expiresUtc)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "INSERT INTO pending_remote_actions(payload_json, expires_at, status) VALUES ($p, $e, 'pending'); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$p", payloadJson);
        cmd.Parameters.AddWithValue("$e", Iso.Of(expiresUtc));
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>The payload if the action is still pending and not expired (without using it up).</summary>
    public string? Peek(long id, DateTime nowUtc)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT payload_json, expires_at FROM pending_remote_actions WHERE id = $id AND status = 'pending'";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return Iso.Parse(r.GetString(1)) > nowUtc ? r.GetString(0) : null;
    }

    /// <summary>Uses the action up and returns its payload, or null if it was used, expired or never existed.</summary>
    public string? TryTake(long id, DateTime nowUtc)
    {
        var payload = Peek(id, nowUtc);
        if (payload is null) return null;
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE pending_remote_actions SET status = 'used' WHERE id = $id AND status = 'pending'";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() == 1 ? payload : null;
    }
}
