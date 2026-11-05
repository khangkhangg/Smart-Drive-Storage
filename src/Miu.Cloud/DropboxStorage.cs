using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Miu.Cloud;

/// <summary>
/// Dropbox inside the app folder. Files up to 8 MiB go in one request; bigger ones use an upload session in 8 MiB
/// chunks. Removed files can be recovered from Dropbox's deleted files.
/// </summary>
public sealed class DropboxStorage(OAuthSession session) : ICloudStorage
{
    private const string Api = "https://api.dropboxapi.com/2/files/";
    private const string Content = "https://content.dropboxapi.com/2/files/";
    public const int ChunkSize = 8 << 20;
    private static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public string ProviderName => "Dropbox";
    public string? ExpectedHashKind(long size) => CloudHashes.Dropbox;

    private sealed record Entry([property: JsonPropertyName(".tag")] string Tag, string Name, string PathDisplay, string? Id, long Size, string? ContentHash, DateTime? ServerModified);
    private sealed record ListResult(List<Entry> Entries, string Cursor, bool HasMore);
    private sealed record Session(string SessionId);

    private static string Dbx(string path) => path.Length == 0 ? "" : "/" + path;

    public async Task<CloudItem?> GetAsync(string path, CancellationToken ct)
    {
        path = CloudPath.Normalize(path);
        using var r = await RpcAsync("get_metadata", new { path = Dbx(path) }, ct, allowConflict: true);
        if (r.StatusCode == HttpStatusCode.Conflict) return null; // path/not_found
        var e = await r.Content.ReadFromJsonAsync<Entry>(Snake, ct);
        return e is { Tag: "file" } ? Item(path, e) : null;
    }

    public async Task<IReadOnlyList<CloudItem>> ListAsync(string folder, CancellationToken ct)
    {
        folder = CloudPath.Normalize(folder);
        var items = new List<CloudItem>();
        using var first = await RpcAsync("list_folder", new { path = Dbx(folder), recursive = true, limit = 2000 }, ct, allowConflict: true);
        if (first.StatusCode == HttpStatusCode.Conflict) return items;
        var page = await first.Content.ReadFromJsonAsync<ListResult>(Snake, ct);
        while (page is not null)
        {
            items.AddRange(page.Entries.Where(e => e.Tag == "file").Select(e => Item(CloudPath.Normalize(e.PathDisplay), e)));
            if (!page.HasMore) break;
            using var next = await RpcAsync("list_folder/continue", new { cursor = page.Cursor }, ct);
            page = await next.Content.ReadFromJsonAsync<ListResult>(Snake, ct);
        }
        return items;
    }

    public async Task<CloudItem> UploadAsync(string path, Stream content, long size, IProgress<long>? progress, CancellationToken ct)
    {
        path = CloudPath.Normalize(path);
        var commit = new { path = Dbx(path), mode = "overwrite", mute = true, strict_conflict = false };
        if (size <= ChunkSize)
        {
            var data = new byte[size];
            await content.ReadExactlyAsync(data, ct);
            using var r = await ContentAsync("upload", commit, data, ct);
            progress?.Report(size);
            return Item(path, (await r.Content.ReadFromJsonAsync<Entry>(Snake, ct))!);
        }
        var buffer = new byte[ChunkSize];
        await content.ReadExactlyAsync(buffer, ct);
        string sessionId;
        using (var s = await ContentAsync("upload_session/start", new { close = false }, buffer, ct))
            sessionId = (await s.Content.ReadFromJsonAsync<Session>(Snake, ct))!.SessionId;
        long sent = ChunkSize;
        progress?.Report(sent);
        while (true)
        {
            var n = (int)Math.Min(ChunkSize, size - sent);
            await content.ReadExactlyAsync(buffer.AsMemory(0, n), ct);
            var chunk = buffer.AsSpan(0, n).ToArray();
            var cursor = new { session_id = sessionId, offset = sent };
            if (sent + n < size)
            {
                using var _ = await ContentAsync("upload_session/append_v2", new { cursor, close = false }, chunk, ct);
                sent += n;
                progress?.Report(sent);
                continue;
            }
            using var done = await ContentAsync("upload_session/finish", new { cursor, commit }, chunk, ct);
            progress?.Report(size);
            return Item(path, (await done.Content.ReadFromJsonAsync<Entry>(Snake, ct))!);
        }
    }

    public async Task DownloadAsync(string path, Stream destination, CancellationToken ct)
    {
        using var r = await session.SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, Content + "download");
            req.Headers.TryAddWithoutValidation("Dropbox-API-Arg", Arg(new { path = Dbx(CloudPath.Normalize(path)) }));
            return req;
        }, ct, stream: true);
        await r.Content.CopyToAsync(destination, ct);
    }

    public async Task MoveAsync(string from, string to, CancellationToken ct)
    {
        using var _ = await RpcAsync("move_v2", new { from_path = Dbx(CloudPath.Normalize(from)), to_path = Dbx(CloudPath.Normalize(to)), autorename = false }, ct);
    }

    public async Task RemoveAsync(string path, CancellationToken ct)
    {
        using var _ = await RpcAsync("delete_v2", new { path = Dbx(CloudPath.Normalize(path)) }, ct, allowConflict: true);
    }

    private Task<HttpResponseMessage> RpcAsync(string endpoint, object body, CancellationToken ct, bool allowConflict = false) =>
        session.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, Api + endpoint) { Content = JsonContent.Create(body) }, ct,
            allowed: allowConflict ? [HttpStatusCode.Conflict] : []);

    private Task<HttpResponseMessage> ContentAsync(string endpoint, object arg, byte[] data, CancellationToken ct) =>
        session.SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, Content + endpoint) { Content = new ByteArrayContent(data) };
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            req.Headers.TryAddWithoutValidation("Dropbox-API-Arg", Arg(arg));
            return req;
        }, ct);

    /// <summary>The header must be ASCII: the default JSON encoder escapes everything else as \uXXXX.</summary>
    public static string Arg(object o) => JsonSerializer.Serialize(o);

    private static CloudItem Item(string path, Entry e) =>
        new(path, e.Size, e.ServerModified, e.ContentHash is null ? null : CloudHashes.Dropbox, e.ContentHash, e.Id ?? path);
}
