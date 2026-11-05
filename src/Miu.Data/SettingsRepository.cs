namespace Miu.Data;

public static class SettingKeys
{
    public const string AutoCleanAllowed = "automation.autoCleanAllowed";
    public const string RunInBackground = "agent.runInBackground";
    public const string Theme = "ui.theme";
    public const string LastScanPath = "ui.lastScanPath";
}

public sealed class SettingsRepository(MiuDatabase db)
{
    public string? Get(string key)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void Set(string key, string value)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "INSERT INTO settings(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public bool GetBool(string key, bool fallback) => Get(key) is { } v ? v == "1" : fallback;

    public void SetBool(string key, bool value) => Set(key, value ? "1" : "0");
}
