using System.Text.Json;

namespace Miu.App.Services;

/// <summary>
/// Saves your Miu setup to a JSON file you can keep or move to another computer: settings, your rules, what you
/// taught Miu, automations, sync jobs, backup plans and which accounts/shares exist. Never any password, token or key.
/// </summary>
public static class ExportService
{
    private static readonly HashSet<string> NeverExported = new(StringComparer.OrdinalIgnoreCase) { "telegram.token" };

    public static string Export(AppServices s, string folder, DateTime nowLocal)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"Miu-export-{nowLocal:yyyy-MM-dd-HHmmss}.json");
        var data = new Dictionary<string, object?>
        {
            ["app"] = $"{AppInfo.Name} {AppInfo.Version}",
            ["exported"] = nowLocal.ToString("O"),
            ["settings"] = AllSettings(s).Where(kv => !NeverExported.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value),
            ["rules"] = s.RulesStore.All(),
            ["automations"] = s.Watches.All(),
            ["syncJobs"] = s.SyncJobs.All(),
            ["backupPlans"] = s.BackupPlans.All(),
            ["cloudAccounts"] = s.CloudAccounts.All().Select(a => new { a.Provider, a.DisplayName }),
            ["networkShares"] = s.NetworkShares.All().Select(n => new { n.Unc, n.Letter, n.UserName, n.Reconnect }),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions(Miu.Core.Rules.RuleLoader.JsonOptions) { WriteIndented = true }));
        return path;
    }

    private static IEnumerable<KeyValuePair<string, string>> AllSettings(AppServices s)
    {
        using var cmd = s.Database.Connection.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM settings ORDER BY key";
        using var r = cmd.ExecuteReader();
        var list = new List<KeyValuePair<string, string>>();
        while (r.Read()) list.Add(new(r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1)));
        return list;
    }
}
