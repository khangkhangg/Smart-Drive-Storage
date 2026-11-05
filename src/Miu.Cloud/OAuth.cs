using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Miu.Cloud;

/// <param name="RedirectPort">0 = any free port (Google, Microsoft); Dropbox needs the exact registered port.</param>
public sealed record OAuthClient(string Provider, string AuthorizeUrl, string TokenUrl, string ClientId, string? ClientSecret, string Scope,
    IReadOnlyDictionary<string, string>? ExtraAuthParams = null, string RedirectHost = "127.0.0.1", int RedirectPort = 0);

public sealed record OAuthTokens(string AccessToken, string? RefreshToken, DateTime ExpiresUtc);

/// <summary>PKCE (RFC 7636): proves the token request comes from the app that started the sign-in.</summary>
public static class Pkce
{
    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    public static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Sign-in in the system browser: Miu listens once on 127.0.0.1 (never on the network), the provider redirects back
/// with a code, Miu checks the state and swaps the code (with the PKCE verifier) for tokens. No password passes through Miu.
/// </summary>
public sealed class LoopbackOAuth(HttpClient http, Func<Uri, Task> openBrowser)
{
    public async Task<OAuthTokens> SignInAsync(OAuthClient client, TimeSpan timeout, CancellationToken ct)
    {
        var port = client.RedirectPort > 0 ? client.RedirectPort : FreePort();
        var redirect = $"http://{client.RedirectHost}:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        listener.Start();
        try
        {
            var verifier = Pkce.NewVerifier();
            var state = Pkce.NewVerifier();
            var q = new Dictionary<string, string>
            {
                ["client_id"] = client.ClientId, ["response_type"] = "code", ["redirect_uri"] = redirect,
                ["state"] = state, ["code_challenge"] = Pkce.Challenge(verifier), ["code_challenge_method"] = "S256",
            };
            if (client.Scope.Length > 0) q["scope"] = client.Scope;
            if (client.ExtraAuthParams is not null) foreach (var (k, v) in client.ExtraAuthParams) q[k] = v;
            // Start listening before the browser can come back (opening it may not return until later).
            var opening = openBrowser(new Uri(client.AuthorizeUrl + "?" + Form(q)));

            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(timeout);
            while (true)
            {
                var ctxTask = listener.GetContextAsync();
                var done = await Task.WhenAny(ctxTask, Task.Delay(Timeout.Infinite, window.Token), opening);
                if (done == opening)
                {
                    if (opening.IsFaulted) throw new CloudException("Couldn't open your web browser: " + opening.Exception!.InnerException?.Message);
                    opening = Task.Delay(Timeout.Infinite, window.Token); // opened: now just wait for the redirect
                    done = await Task.WhenAny(ctxTask, opening);
                }
                if (done != ctxTask)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new CloudException("Sign-in timed out. Try again.");
                }
                var ctx = await ctxTask;
                var query = System.Web.HttpUtility.ParseQueryString(ctx.Request.Url?.Query ?? "");
                if (query["state"] is null && query["code"] is null && query["error"] is null) { Respond(ctx, 404, "Not found."); continue; } // e.g. /favicon.ico
                if (!string.Equals(query["state"], state, StringComparison.Ordinal))
                {
                    Respond(ctx, 400, "This sign-in link doesn't match. Please start again from Miu.");
                    throw new CloudException("Sign-in was rejected (state mismatch).");
                }
                if (query["error"] is { } error)
                {
                    Respond(ctx, 200, "Sign-in was cancelled. You can close this tab.");
                    throw new CloudException(error == "access_denied" ? "Sign-in was cancelled." : "Sign-in failed: " + error);
                }
                Respond(ctx, 200, "Signed in. Miu is finishing the connection; you can close this tab and go back to Miu.");
                return await ExchangeAsync(client, new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code", ["code"] = query["code"] ?? "", ["redirect_uri"] = redirect, ["code_verifier"] = verifier,
                }, null, ct);
            }
        }
        finally { listener.Stop(); }
    }

    /// <summary>Gets new tokens from a refresh token. Providers may or may not send a new refresh token.</summary>
    public static Task<OAuthTokens> RefreshAsync(HttpClient http, OAuthClient client, string refreshToken, CancellationToken ct) =>
        ExchangeAsync(http, client, new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken }, refreshToken, ct);

    private Task<OAuthTokens> ExchangeAsync(OAuthClient client, Dictionary<string, string> form, string? keepRefresh, CancellationToken ct) =>
        ExchangeAsync(http, client, form, keepRefresh, ct);

    private static async Task<OAuthTokens> ExchangeAsync(HttpClient http, OAuthClient client, Dictionary<string, string> form, string? keepRefresh, CancellationToken ct)
    {
        form["client_id"] = client.ClientId;
        if (client.ClientSecret is { Length: > 0 } secret) form["client_secret"] = secret;
        HttpResponseMessage r;
        try { r = await http.PostAsync(client.TokenUrl, new FormUrlEncodedContent(form), ct); }
        catch (HttpRequestException ex) { throw new CloudException("Couldn't reach " + client.Provider + ": " + ex.Message, null, ex); }
        using (r)
        {
            var body = await r.Content.ReadAsStringAsync(ct);
            if (!r.IsSuccessStatusCode)
            {
                // Never echo the body: it may contain tokens. The error code is enough.
                string? code = null;
                try { code = JsonDocument.Parse(body).RootElement.TryGetProperty("error", out var e) ? e.GetString() : null; } catch (JsonException) { }
                throw new CloudException(code == "invalid_grant" ? $"{client.Provider} access was withdrawn or expired. Connect the account again." : $"{client.Provider} refused the sign-in ({code ?? ((int)r.StatusCode).ToString()}).", r.StatusCode);
            }
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var expires = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var s) ? s : 3600;
            return new OAuthTokens(root.GetProperty("access_token").GetString()!,
                root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : keepRefresh, DateTime.UtcNow.AddSeconds(expires));
        }
    }

    private static void Respond(HttpListenerContext ctx, int status, string text)
    {
        var html = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=utf-8><title>Miu Smart Storage</title><body style=\"font-family:sans-serif;padding:40px\"><h2>Miu Smart Storage</h2><p>{WebUtility.HtmlEncode(text)}</p>");
        try
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.OutputStream.Write(html);
            ctx.Response.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The browser closed the connection early: the sign-in itself can still go on.
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public static string Form(IEnumerable<KeyValuePair<string, string>> q) => string.Join('&', q.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
}

/// <summary>
/// Sends authorised requests: refreshes the access token shortly before it expires, and once more on a 401.
/// New tokens are handed to <c>save</c> (which stores them in the secret store).
/// </summary>
public sealed class OAuthSession(HttpClient http, OAuthClient client, OAuthTokens tokens, Action<OAuthTokens> save)
{
    private OAuthTokens _tokens = tokens;
    private readonly SemaphoreSlim _refreshing = new(1, 1);

    public HttpClient Http => http;

    private async Task<string> TokenAsync(bool force, CancellationToken ct)
    {
        if (!force && _tokens.ExpiresUtc > DateTime.UtcNow.AddMinutes(2)) return _tokens.AccessToken;
        await _refreshing.WaitAsync(ct);
        try
        {
            if (!force && _tokens.ExpiresUtc > DateTime.UtcNow.AddMinutes(2)) return _tokens.AccessToken;
            if (_tokens.RefreshToken is not { } refresh) throw new CloudException($"{client.Provider} sign-in expired. Connect the account again.", HttpStatusCode.Unauthorized);
            _tokens = await LoopbackOAuth.RefreshAsync(http, client, refresh, ct);
            save(_tokens);
            return _tokens.AccessToken;
        }
        finally { _refreshing.Release(); }
    }

    /// <summary>Sends a request built by <paramref name="build"/> (called again for the retry after a refresh).</summary>
    public async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct, bool stream = false, params HttpStatusCode[] allowed)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = build();
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(attempt > 0, ct));
            HttpResponseMessage r;
            try { r = await http.SendAsync(req, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct); }
            catch (HttpRequestException ex) { throw new CloudException($"Couldn't reach {client.Provider}: {ex.Message}", null, ex); }
            if (r.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { r.Dispose(); continue; }
            if (r.IsSuccessStatusCode || allowed.Contains(r.StatusCode)) return r;
            var text = await r.Content.ReadAsStringAsync(ct);
            r.Dispose();
            throw new CloudException($"{client.Provider} answered {(int)r.StatusCode}: {Summary(text)}", r.StatusCode);
        }
    }

    /// <summary>A short error description from a JSON error body (no tokens).</summary>
    public static string Summary(string body)
    {
        try
        {
            var root = JsonDocument.Parse(body).RootElement;
            if (root.TryGetProperty("error_summary", out var s)) return s.GetString() ?? "";
            if (root.TryGetProperty("error", out var e))
                return e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) ? m.GetString() ?? "" : e.ToString();
        }
        catch (JsonException) { }
        return body.Length > 200 ? body[..200] : body;
    }
}

/// <summary>Where Miu's sign-in app registrations come from (shipped next to the app, or environment variables).</summary>
public sealed record CloudClientIds(string? GoogleClientId, string? GoogleClientSecret, string? MicrosoftClientId, string? DropboxClientId, int DropboxRedirectPort = 53682)
{
    public static CloudClientIds Load(string? jsonPath)
    {
        string? Env(string k) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : null;
        var fromFile = new CloudClientIds(null, null, null, null);
        if (jsonPath is not null && File.Exists(jsonPath))
        {
            try { fromFile = JsonSerializer.Deserialize<CloudClientIds>(File.ReadAllText(jsonPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? fromFile; }
            catch (JsonException) { }
        }
        return new CloudClientIds(Env("MIU_GOOGLE_CLIENT_ID") ?? fromFile.GoogleClientId, Env("MIU_GOOGLE_CLIENT_SECRET") ?? fromFile.GoogleClientSecret,
            Env("MIU_MICROSOFT_CLIENT_ID") ?? fromFile.MicrosoftClientId, Env("MIU_DROPBOX_CLIENT_ID") ?? fromFile.DropboxClientId, fromFile.DropboxRedirectPort);
    }

    public OAuthClient? Google => GoogleClientId is { Length: > 0 } id
        ? new("Google Drive", "https://accounts.google.com/o/oauth2/v2/auth", "https://oauth2.googleapis.com/token", id, GoogleClientSecret,
            "https://www.googleapis.com/auth/drive.file", new Dictionary<string, string> { ["access_type"] = "offline", ["prompt"] = "consent" },
            RedirectHost: "localhost") // Windows lets normal users listen on "localhost" without a URL reservation
        : null;

    public OAuthClient? Microsoft => MicrosoftClientId is { Length: > 0 } id
        ? new("OneDrive", "https://login.microsoftonline.com/common/oauth2/v2.0/authorize", "https://login.microsoftonline.com/common/oauth2/v2.0/token", id, null,
            "Files.ReadWrite.AppFolder offline_access User.Read", RedirectHost: "localhost")
        : null;

    public OAuthClient? Dropbox => DropboxClientId is { Length: > 0 } id
        ? new("Dropbox", "https://www.dropbox.com/oauth2/authorize", "https://api.dropboxapi.com/oauth2/token", id, null, "",
            new Dictionary<string, string> { ["token_access_type"] = "offline" }, RedirectHost: "localhost", RedirectPort: DropboxRedirectPort)
        : null;
}
