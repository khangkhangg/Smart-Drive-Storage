namespace Miu.Data;

/// <summary>The one paired Telegram chat (the bot token itself is in the secret store).</summary>
public sealed class TelegramRepository(MiuDatabase db)
{
    public long? ChatId()
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT chat_id FROM telegram WHERE id = 1";
        return cmd.ExecuteScalar() is long id ? id : null;
    }

    public void Pair(long chatId, DateTime pairedUtc)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "INSERT INTO telegram(id, chat_id, paired_at) VALUES (1, $c, $t) ON CONFLICT(id) DO UPDATE SET chat_id = excluded.chat_id, paired_at = excluded.paired_at";
        cmd.Parameters.AddWithValue("$c", chatId);
        cmd.Parameters.AddWithValue("$t", Iso.Of(pairedUtc));
        cmd.ExecuteNonQuery();
    }

    public void Unpair()
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM telegram WHERE id = 1";
        cmd.ExecuteNonQuery();
    }
}
