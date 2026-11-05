using System.Diagnostics;
using System.Text.Json;
using Miu.Cloud;
using Miu.Data;

namespace Miu.App.Services;

public sealed record CloudProviderChoice(string Key, string Name, bool Available, string Note);

/// <summary>Connecting cloud accounts. Tokens and keys go only to the secret store (cloud.&lt;id&gt;), never to the database.</summary>
public sealed class CloudAccountsService(AppServices services)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };
    private sealed record S3Secret(string Endpoint, string Region, string Bucket, string AccessKey, string SecretKey);
    private sealed record OAuthSecret(string AccessToken, string? RefreshToken, DateTime ExpiresUtc);

    public CloudClientIds ClientIds { get; } = CloudClientIds.Load(Path.Combine(AppContext.BaseDirectory, "cloud-clients.json"));

    public IReadOnlyList<CloudProviderChoice> Providers =>
    [
        Choice("gdrive", "Google Drive", ClientIds.Google),
        Choice("onedrive", "OneDrive", ClientIds.Microsoft),
        Choice("dropbox", "Dropbox", ClientIds.Dropbox),
        new("s3", "S3-compatible (AWS, Backblaze B2, Wasabi, Cloudflare R2, MinIO)", true, "Uses an access key for one bucket."),
    ];

    private static CloudProviderChoice Choice(string key, string name, OAuthClient? client) =>
        new(key, name, client is not null, client is null ? "Not available in this build (needs Miu's app registration)." : "Sign in with your browser. Miu only sees its own folder.");

    public static string ProviderName(string key) => key switch { "gdrive" => "Google Drive", "onedrive" => "OneDrive", "dropbox" => "Dropbox", _ => "S3" };

    /// <summary>Checks the keys against the bucket, then saves the account.</summary>
    public async Task<CloudAccount> ConnectS3Async(string name, S3Config config, CancellationToken ct = default)
    {
        await new S3Storage(Http, config).TestAsync(ct);
        var id = services.CloudAccounts.Add("s3", string.IsNullOrWhiteSpace(name) ? $"S3 · {config.Bucket}" : name.Trim());
        services.Secrets.Set(CloudAccount.SecretKeyFor(id), JsonSerializer.Serialize(new S3Secret(config.Endpoint, config.Region, config.Bucket, config.AccessKey, config.SecretKey)));
        return services.CloudAccounts.Get(id)!;
    }

    /// <summary>Signs in with the system browser (PKCE, loopback redirect).</summary>
    public async Task<CloudAccount> ConnectOAuthAsync(string provider, Func<Uri, Task>? openBrowser = null, CancellationToken ct = default)
    {
        var client = ClientFor(provider) ?? throw new CloudException($"{ProviderName(provider)} isn't available in this build.");
        var tokens = await new LoopbackOAuth(Http, openBrowser ?? OpenBrowserAsync).SignInAsync(client, TimeSpan.FromMinutes(5), ct);
        var id = services.CloudAccounts.Add(provider, ProviderName(provider));
        SaveTokens(id, tokens);
        return services.CloudAccounts.Get(id)!;
    }

    /// <summary>Removes the account and its secret. Refused while backup plans still use it.</summary>
    public string? Disconnect(CloudAccount account)
    {
        if (services.BackupPlans.All().Any(p => p.AccountId == account.Id)) return "Delete the backup plans that use this account first.";
        services.Secrets.Delete(account.SecretKey);
        services.CloudAccounts.Delete(account.Id);
        return null;
    }

    public ICloudStorage Open(CloudAccount account)
    {
        if (services.CloudStorageOverride is { } fake) return fake(account);
        var secret = services.Secrets.Get(account.SecretKey) ?? throw new CloudException($"Miu has no sign-in for {account.DisplayName}. Connect it again.");
        if (account.Provider == "s3")
        {
            var s = JsonSerializer.Deserialize<S3Secret>(secret)!;
            return new S3Storage(Http, new S3Config(s.Endpoint, s.Region, s.Bucket, s.AccessKey, s.SecretKey));
        }
        var client = ClientFor(account.Provider) ?? throw new CloudException($"{account.DisplayName} isn't available in this build.");
        var t = JsonSerializer.Deserialize<OAuthSecret>(secret)!;
        var session = new OAuthSession(Http, client, new OAuthTokens(t.AccessToken, t.RefreshToken, t.ExpiresUtc), tokens => SaveTokens(account.Id, tokens));
        return account.Provider switch
        {
            "gdrive" => new GoogleDriveStorage(session),
            "onedrive" => new OneDriveStorage(session),
            "dropbox" => new DropboxStorage(session),
            _ => throw new CloudException("Unknown provider: " + account.Provider),
        };
    }

    private OAuthClient? ClientFor(string provider) => provider switch
    {
        "gdrive" => ClientIds.Google,
        "onedrive" => ClientIds.Microsoft,
        "dropbox" => ClientIds.Dropbox,
        _ => null,
    };

    private void SaveTokens(long id, OAuthTokens t) =>
        services.Secrets.Set(CloudAccount.SecretKeyFor(id), JsonSerializer.Serialize(new OAuthSecret(t.AccessToken, t.RefreshToken, t.ExpiresUtc)));

    private static Task OpenBrowserAsync(Uri url)
    {
        if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS()) Process.Start("open", [url.AbsoluteUri]);
        else Process.Start("xdg-open", [url.AbsoluteUri]);
        return Task.CompletedTask;
    }
}
