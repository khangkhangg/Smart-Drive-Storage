namespace Miu.App.Services;

/// <summary>Groups "safe to remove" items for the Home summary, by the rule that marked them.</summary>
public static class SafeCategories
{
    private static readonly string[] PackageCaches =
        ["npm-cache", "pnpm-store", "yarn-cache", "bun-cache", "pip-cache", "nuget-http", "go-build-cache", "cargo-registry-cache", "conda-pkgs"];

    public static string For(string? ruleId) => ruleId switch
    {
        null => "Other",
        _ when ruleId.StartsWith("dev.", StringComparison.Ordinal) && PackageCaches.Any(c => ruleId == "dev." + c) => "Package caches",
        _ when ruleId.StartsWith("dev.", StringComparison.Ordinal) => "Dev build files",
        "sys.temp-files" or "sys.tmp-unix" => "Temp files",
        "sys.crash-dumps" or "sys.crashdumps-folder" => "Crash dumps",
        "sys.chrome-cache" or "sys.firefox-cache" => "Browser caches",
        "sys.thumbnails" => "Thumbnails",
        _ => "Other",
    };
}
