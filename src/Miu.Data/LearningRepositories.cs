using System.Globalization;
using System.Text.Json;
using Miu.Core.Learning;
using Miu.Core.Rules;

namespace Miu.Data;

/// <summary>Every Keep / Delete choice (explicit or from cleaning), the raw material for learned rules.</summary>
public sealed class DecisionsRepository(MiuDatabase db)
{
    public void Add(Decision d, string origin)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "INSERT INTO decisions(ts, path, features_json, action, reason, origin) VALUES ($ts, $p, $f, $a, $r, $o)";
        cmd.Parameters.AddWithValue("$ts", d.TsUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$p", d.Path);
        cmd.Parameters.AddWithValue("$f", JsonSerializer.Serialize(d, RuleLoader.JsonOptions));
        cmd.Parameters.AddWithValue("$a", d.Action == DecisionAction.Keep ? "keep" : "delete");
        cmd.Parameters.AddWithValue("$r", (object?)d.Reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$o", origin is "app" or "telegram" or "auto" ? origin : "app");
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<Decision> All()
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT features_json FROM decisions ORDER BY id";
        using var r = cmd.ExecuteReader();
        var list = new List<Decision>();
        while (r.Read())
            if (JsonSerializer.Deserialize<Decision>(r.GetString(0), RuleLoader.JsonOptions) is { } d) list.Add(d);
        return list;
    }
}

/// <summary>Rules the user made or accepted (built-in rules stay in rules/builtin).</summary>
public sealed class RulesRepository(MiuDatabase db)
{
    public void Upsert(Rule rule)
    {
        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rules(id, json, source, priority, enabled, created, updated) VALUES ($id, $j, $s, $p, $e, $now, $now)
            ON CONFLICT(id) DO UPDATE SET json = excluded.json, source = excluded.source, priority = excluded.priority,
                                          enabled = excluded.enabled, updated = excluded.updated
            """;
        cmd.Parameters.AddWithValue("$id", rule.Id);
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(rule, RuleLoader.JsonOptions));
        cmd.Parameters.AddWithValue("$s", rule.Source.ToString());
        cmd.Parameters.AddWithValue("$p", rule.Priority);
        cmd.Parameters.AddWithValue("$e", rule.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<Rule> All()
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT json, enabled FROM rules ORDER BY created";
        using var r = cmd.ExecuteReader();
        var list = new List<Rule>();
        while (r.Read())
            if (JsonSerializer.Deserialize<Rule>(r.GetString(0), RuleLoader.JsonOptions) is { } rule)
                list.Add(rule with { Enabled = r.GetInt64(1) == 1, Match = rule.Match ?? new RuleMatch(), Conditions = rule.Conditions ?? new RuleConditions() });
        return list;
    }

    public void SetEnabled(string id, bool enabled)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE rules SET enabled = $e, updated = $now WHERE id = $id";
        cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void Delete(string id)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM rules WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }
}

/// <summary>Rules Miu proposes from patterns in your decisions, waiting for "Turn on" or "Not now".</summary>
public sealed class SuggestionsRepository(MiuDatabase db)
{
    public long Add(RuleSuggestion s)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "INSERT INTO rule_suggestions(rule_json, evidence_json, confidence, status, created, key) VALUES ($r, $e, $c, 'pending', $t, $k); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$r", JsonSerializer.Serialize(s.Rule, RuleLoader.JsonOptions));
        cmd.Parameters.AddWithValue("$e", s.Evidence);
        cmd.Parameters.AddWithValue("$c", s.Confidence);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$k", s.Key);
        return (long)cmd.ExecuteScalar()!;
    }

    public IReadOnlyList<(long Id, RuleSuggestion Suggestion)> Pending()
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT id, key, rule_json, evidence_json, confidence FROM rule_suggestions WHERE status = 'pending' ORDER BY id";
        using var r = cmd.ExecuteReader();
        var list = new List<(long, RuleSuggestion)>();
        while (r.Read())
            if (JsonSerializer.Deserialize<Rule>(r.GetString(2), RuleLoader.JsonOptions) is { } rule)
                list.Add((r.GetInt64(0), new RuleSuggestion(r.IsDBNull(1) ? "" : r.GetString(1),
                    rule with { Match = rule.Match ?? new RuleMatch(), Conditions = rule.Conditions ?? new RuleConditions() },
                    r.GetString(3), r.GetDouble(4))));
        return list;
    }

    public void SetStatus(long id, string status)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE rule_suggestions SET status = $s WHERE id = $id";
        cmd.Parameters.AddWithValue("$s", status is "accepted" or "rejected" ? status : "pending");
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Every pattern already proposed (pending, accepted or rejected), so none is proposed twice.</summary>
    public ISet<string> KnownKeys()
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT key FROM rule_suggestions WHERE key IS NOT NULL";
        using var r = cmd.ExecuteReader();
        var set = new HashSet<string>();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }
}
