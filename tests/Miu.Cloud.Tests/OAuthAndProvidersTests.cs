using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using Miu.Cloud;

namespace Miu.Cloud.Tests;

public class OAuthTests
{
    private static readonly OAuthClient Client = new("TestCloud", "https://auth.example/authorize", "https://auth.example/token", "client-1", null, "files");

    [Fact]
    public void Pkce_matches_the_rfc_example() =>
        Pkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk").Should().Be("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");

    [Fact]
    public async Task Loopback_sign_in_checks_state_and_sends_the_verifier()
    {
        var tokenHttp = new FakeHttp(_ => FakeHttp.Ok("""{"access_token":"AT","refresh_token":"RT","expires_in":3600}"""));
        Uri? opened = null;
        var oauth = new LoopbackOAuth(new HttpClient(tokenHttp), async url =>
        {
            opened = url;
            var q = HttpUtility.ParseQueryString(url.Query);
            using var browser = new HttpClient();
            (await browser.GetAsync($"{q["redirect_uri"]}favicon.ico")).StatusCode.Should().Be(HttpStatusCode.NotFound); // ignored
            _ = browser.GetAsync($"{q["redirect_uri"]}?code=CODE1&state={q["state"]}");
        });
        var tokens = await oauth.SignInAsync(Client, TimeSpan.FromSeconds(20), default);
        tokens.AccessToken.Should().Be("AT");
        tokens.RefreshToken.Should().Be("RT");

        var q = HttpUtility.ParseQueryString(opened!.Query);
        q["code_challenge_method"].Should().Be("S256");
        q["redirect_uri"].Should().StartWith("http://127.0.0.1:");
        var form = HttpUtility.ParseQueryString(Encoding.UTF8.GetString(tokenHttp.Requests.Single().Body));
        form["code"].Should().Be("CODE1");
        form["grant_type"].Should().Be("authorization_code");
        Pkce.Challenge(form["code_verifier"]!).Should().Be(q["code_challenge"]);
        form["redirect_uri"].Should().Be(q["redirect_uri"]);
    }

    [Fact]
    public async Task A_redirect_with_the_wrong_state_is_rejected()
    {
        var tokenHttp = new FakeHttp(_ => FakeHttp.Ok("""{"access_token":"AT"}"""));
        var oauth = new LoopbackOAuth(new HttpClient(tokenHttp), url =>
        {
            var q = HttpUtility.ParseQueryString(url.Query);
            _ = new HttpClient().GetAsync($"{q["redirect_uri"]}?code=EVIL&state=forged");
            return Task.CompletedTask;
        });
        await FluentActions.Awaiting(() => oauth.SignInAsync(Client, TimeSpan.FromSeconds(20), default)).Should().ThrowAsync<CloudException>().WithMessage("*state*");
        tokenHttp.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Session_refreshes_once_on_401_and_saves_new_tokens()
    {
        var calls = 0;
        var http = new FakeHttp(r =>
        {
            if (r.Uri.AbsoluteUri == Client.TokenUrl) return FakeHttp.Ok("""{"access_token":"NEW","expires_in":3600}""");
            calls++;
            return r.Headers["authorization"] == "Bearer NEW" ? FakeHttp.Ok("{}") : FakeHttp.Status(HttpStatusCode.Unauthorized);
        });
        OAuthTokens? saved = null;
        var session = new OAuthSession(new HttpClient(http), Client, new OAuthTokens("OLD", "RT", DateTime.UtcNow.AddHours(1)), t => saved = t);
        using var r = await session.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "https://api.example/x"), default);
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        calls.Should().Be(2);
        saved!.AccessToken.Should().Be("NEW");
        saved.RefreshToken.Should().Be("RT"); // kept when the provider doesn't send a new one
        var form = HttpUtility.ParseQueryString(Encoding.UTF8.GetString(http.Requests.Single(x => x.Uri.AbsoluteUri == Client.TokenUrl).Body));
        form["grant_type"].Should().Be("refresh_token");
    }

    [Fact]
    public async Task A_revoked_refresh_token_asks_to_connect_again_without_echoing_the_body()
    {
        var http = new FakeHttp(r => r.Uri.AbsoluteUri == Client.TokenUrl
            ? FakeHttp.Status(HttpStatusCode.BadRequest, """{"error":"invalid_grant","refresh_token":"SHOULD-NOT-LEAK"}""")
            : FakeHttp.Status(HttpStatusCode.Unauthorized));
        var session = new OAuthSession(new HttpClient(http), Client, new OAuthTokens("OLD", "RT", DateTime.UtcNow.AddHours(1)), _ => { });
        var ex = await FluentActions.Awaiting(() => session.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "https://api.example/x"), default)).Should().ThrowAsync<CloudException>();
        ex.Which.Message.Should().Contain("Connect the account again").And.NotContain("SHOULD-NOT-LEAK");
    }

    [Fact]
    public void Client_ids_load_from_file_and_environment()
    {
        var f = Path.Combine(Path.GetTempPath(), "miu-clients-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(f, """{"googleClientId":"g-id","googleClientSecret":"g-sec","dropboxClientId":"d-id"}""");
        try
        {
            var ids = CloudClientIds.Load(f);
            ids.Google!.ClientId.Should().Be("g-id");
            ids.Google.Scope.Should().Be("https://www.googleapis.com/auth/drive.file");
            ids.Microsoft.Should().BeNull();
            ids.Dropbox!.RedirectPort.Should().Be(53682);
            CloudClientIds.Load(null).Google.Should().BeNull();
        }
        finally { File.Delete(f); }
    }
}

public class ProviderTests
{
    private static OAuthSession Session(FakeHttp http) =>
        new(new HttpClient(http), new OAuthClient("P", "https://a", "https://t", "id", null, "s"), new OAuthTokens("AT", "RT", DateTime.UtcNow.AddHours(1)), _ => { });

    private static string Q(SeenRequest r) => HttpUtility.UrlDecode(r.Uri.Query);

    [Fact]
    public async Task Google_drive_creates_folders_then_uploads_in_resumable_chunks()
    {
        var size = GoogleDriveStorage.ChunkSize + 5;
        var http = new FakeHttp(r =>
        {
            if (r.Method == HttpMethod.Get) return FakeHttp.Ok("""{"files":[]}"""); // nothing exists yet
            if (r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/drive/v3/files") return FakeHttp.Ok("""{"id":"F_BACKUP","name":"Backup","mimeType":"application/vnd.google-apps.folder"}""");
            if (r.Method == HttpMethod.Post) return FakeHttp.Ok("", configure: x => x.Headers.Location = new Uri("https://upload.example/session1"));
            return r.Headers["content-range"].EndsWith($"{size - 1}/{size}")
                ? FakeHttp.Ok("""{"id":"FILE1","name":"a.bin","mimeType":"application/octet-stream","size":"8388613","md5Checksum":"abc123"}""")
                : FakeHttp.Status((HttpStatusCode)308);
        });
        var item = await new GoogleDriveStorage(Session(http)).UploadAsync("Backup/a.bin", new MemoryStream(new byte[size]), size, null, default);
        item.Should().Be(new CloudItem("Backup/a.bin", size, null, CloudHashes.Md5, "abc123", "FILE1"));
        Q(http.Requests[0]).Should().Contain("name = 'Backup' and 'root' in parents");
        var start = http.Requests.Single(r => r.Uri.Host == "www.googleapis.com" && r.Uri.AbsolutePath.StartsWith("/upload"));
        Encoding.UTF8.GetString(start.Body).Should().Contain("\"parents\":[\"F_BACKUP\"]");
        start.Headers["x-upload-content-length"].Should().Be(size.ToString());
        var puts = http.Requests.Where(r => r.Method == HttpMethod.Put).ToList();
        puts.Select(p => p.Headers["content-range"]).Should().Equal($"bytes 0-{GoogleDriveStorage.ChunkSize - 1}/{size}", $"bytes {GoogleDriveStorage.ChunkSize}-{size - 1}/{size}");
    }

    [Fact]
    public async Task Google_drive_escapes_quotes_in_names_and_trashes_instead_of_deleting()
    {
        var http = new FakeHttp(r => r.Method == HttpMethod.Get
            ? FakeHttp.Ok("""{"files":[{"id":"X","name":"it's","mimeType":"text/plain","size":"1"}]}""")
            : FakeHttp.Ok("{}"));
        await new GoogleDriveStorage(Session(http)).RemoveAsync("it's", default);
        Q(http.Requests[0]).Should().Contain(@"name = 'it\'s'");
        var patch = http.Requests.Last();
        patch.Method.Should().Be(HttpMethod.Patch);
        Encoding.UTF8.GetString(patch.Body).Should().Contain("\"trashed\":true");
    }

    [Fact]
    public async Task OneDrive_small_and_session_uploads_with_quickxor()
    {
        var http = new FakeHttp(r =>
            r.Uri.AbsolutePath.EndsWith("/createUploadSession") ? FakeHttp.Ok("""{"uploadUrl":"https://up.example/s1"}""")
            : r.Uri.Host == "up.example" && r.Headers["content-range"].StartsWith("bytes 0-") ? FakeHttp.Status(HttpStatusCode.Accepted, "{}")
            : FakeHttp.Ok("""{"id":"I1","name":"x","size":5,"file":{"hashes":{"quickXorHash":"QX=="}}}"""));
        var od = new OneDriveStorage(Session(http));
        var small = await od.UploadAsync("Backup/My Photos/é.jpg", new MemoryStream(new byte[5]), 5, null, default);
        http.Requests[0].Uri.AbsoluteUri.Should().Be("https://graph.microsoft.com/v1.0/me/drive/special/approot:/Backup/My%20Photos/%C3%A9.jpg:/content");
        small.HashKind.Should().Be(CloudHashes.QuickXor);
        small.Hash.Should().Be("QX==");

        var size = OneDriveStorage.ChunkSize + 3;
        await od.UploadAsync("Backup/big.bin", new MemoryStream(new byte[size]), size, null, default);
        var chunks = http.Requests.Where(r => r.Uri.Host == "up.example").ToList();
        chunks.Should().HaveCount(2);
        chunks.Should().OnlyContain(c => !c.Headers.ContainsKey("authorization")); // the session URL is pre-authorised
        chunks[1].Headers["content-range"].Should().Be($"bytes {OneDriveStorage.ChunkSize}-{size - 1}/{size}");
    }

    [Fact]
    public async Task Dropbox_session_upload_offsets_and_ascii_header()
    {
        var http = new FakeHttp(r =>
            r.Uri.AbsolutePath.EndsWith("upload_session/start") ? FakeHttp.Ok("""{"session_id":"S1"}""")
            : r.Uri.AbsolutePath.EndsWith("append_v2") ? FakeHttp.Ok("null")
            : FakeHttp.Ok("""{".tag":"file","name":"b","path_display":"/Backup/b","id":"id:1","size":1,"content_hash":"CH"}"""));
        var size = 2L * DropboxStorage.ChunkSize + 10;
        var item = await new DropboxStorage(Session(http)).UploadAsync("Backup/café.bin", new MemoryStream(new byte[size]), size, null, default);
        item.HashKind.Should().Be(CloudHashes.Dropbox);
        http.Requests.Select(r => r.Uri.AbsolutePath).Should().Equal("/2/files/upload_session/start", "/2/files/upload_session/append_v2", "/2/files/upload_session/finish");
        var finish = JsonDocument.Parse(http.Requests[2].Headers["dropbox-api-arg"]).RootElement;
        finish.GetProperty("cursor").GetProperty("offset").GetInt64().Should().Be(2L * DropboxStorage.ChunkSize);
        finish.GetProperty("commit").GetProperty("mode").GetString().Should().Be("overwrite");
        http.Requests[2].Headers["dropbox-api-arg"].Should().Contain("\\u00E9").And.MatchRegex("^[\\x00-\\x7F]*$");
        http.Requests.Sum(r => (long)r.Body.Length).Should().Be(size);
    }

    [Fact]
    public async Task Dropbox_missing_paths_are_null_and_list_follows_cursors()
    {
        var http = new FakeHttp(r =>
            r.Uri.AbsolutePath.EndsWith("get_metadata") ? FakeHttp.Status(HttpStatusCode.Conflict, """{"error_summary":"path/not_found/"}""")
            : r.Uri.AbsolutePath.EndsWith("list_folder") ? FakeHttp.Ok("""{"entries":[{".tag":"file","name":"a","path_display":"/B/a","size":1,"content_hash":"h"},{".tag":"folder","name":"d","path_display":"/B/d"}],"cursor":"C1","has_more":true}""")
            : FakeHttp.Ok("""{"entries":[{".tag":"file","name":"b","path_display":"/B/d/b","size":2}],"cursor":"C2","has_more":false}"""));
        var dbx = new DropboxStorage(Session(http));
        (await dbx.GetAsync("B/none", default)).Should().BeNull();
        (await dbx.ListAsync("B", default)).Select(i => i.Path).Should().Equal("B/a", "B/d/b");
    }
}
