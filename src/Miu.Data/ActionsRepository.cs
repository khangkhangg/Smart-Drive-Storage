using System.Globalization;
using System.Text.Json;

namespace Miu.Data;

public sealed record ActionItem(string OriginalPath, string TrashLocation, long Bytes);

public sealed record ActionRecord(long Id, DateTime TsUtc, string Kind, IReadOnlyList<ActionItem> Items, long Bytes, string Origin, bool Undone);

/// <summary>The Activity log: every clean Miu performs, with what is needed to undo it.</summary>
public sealed class ActionsRepository(MiuDatabase db)
{
    public long Add(string kind, IReadOnlyList<ActionItem> items, string origin)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "INSERT INTO actions_log(ts, kind, items_json, bytes, origin) VALUES ($ts, $k, $items, $b, $o); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$items", JsonSerializer.Serialize(items));
        cmd.Parameters.AddWithValue("$b", items.Sum(i => i.Bytes));
        cmd.Parameters.AddWithValue("$o", origin);
        return (long)cmd.ExecuteScalar()!;
    }

    public IReadOnlyList<ActionRecord> Recent(int count)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT id, ts, kind, items_json, bytes, origin, undone FROM actions_log ORDER BY id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", count);
        return Read(cmd);
    }

    public ActionRecord? Get(long id)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT id, ts, kind, items_json, bytes, origin, undone FROM actions_log WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return Read(cmd).FirstOrDefault();
    }

    /// <summary>Keeps only the items still waiting to be restored (after a partial undo).</summary>
    public void UpdateItems(long id, IReadOnlyList<ActionItem> items)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE actions_log SET items_json = $items, bytes = $b WHERE id = $id";
        cmd.Parameters.AddWithValue("$items", JsonSerializer.Serialize(items));
        cmd.Parameters.AddWithValue("$b", items.Sum(i => i.Bytes));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void MarkUndone(long id)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE actions_log SET undone = 1 WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static List<ActionRecord> Read(Microsoft.Data.Sqlite.SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<ActionRecord>();
        while (r.Read())
            list.Add(new ActionRecord(
                r.GetInt64(0),
                DateTime.Parse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                r.GetString(2),
                JsonSerializer.Deserialize<List<ActionItem>>(r.GetString(3)) ?? [],
                r.GetInt64(4),
                r.GetString(5),
                r.GetInt64(6) == 1));
        return list;
    }
}
