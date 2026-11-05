using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Miu.Cloud;

/// <summary>
/// OneDrive through Microsoft Graph, inside the app folder (Apps/Miu Smart Storage). Small files go in one request;
/// bigger ones use an upload session in 10 MiB chunks. Removing a file sends it to the OneDrive recycle bin.
/// </summary>
public sealed class OneDriveStorage(OAuthSession session) : ICloudStorage
{
    private const string Root = "https://graph.microsoft.com/v1.0/me/drive/special/approot";
    public const long SimpleLimit = 4L << 20;
    public const int ChunkSize = 32 * 327_680; // 10 MiB, a multiple of 320 KiB as Graph requires
    private const string Select = "id,name,size,file,folder,lastModifiedDateTime";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public string ProviderName => "OneDrive";
    public string? ExpectedHashKind(long size) => CloudHashes.QuickXor;

    private sealed record Hashes(string? QuickXorHash);
    private sealed record FileFacet(Hashes? Hashes);
    private sealed record DriveItem(string Id, string Name, long Size, FileFacet? File, JsonElement? Folder, DateTime? LastModifiedDateTime);
    private sealed record Page(List<DriveItem> Value, [property: JsonPropertyName("@odata.nextLink")] string? NextLink);
    private sealed record UploadSession(string UploadUrl);

    public static string ItemUrl(string path) => path.Length == 0 ? Root : Root + ":/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString)) + ":";

    public async Task<CloudItem?> GetAsync(string path, CancellationToken ct)
    {
        path = CloudPath.Normalize(path);
        using var r = await session.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{ItemUrl(path)}?$select={Select}"), ct, allowed: HttpStatusCode.NotFound);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        var item = await r.Content.ReadFromJsonAsync<DriveItem>(Web, ct);
        return item?.File is null ? null : Item(path, item);
    }

    public async Task<IReadOnlyList<CloudItem>> ListAsync(string folder, CancellationToken ct)
    {
        folder = CloudPath.Normalize(folder);
        var items = new List<CloudItem>();
        var queue = new Queue<string>([folder]);
        while (queue.Count > 0)
        {
            var f = queue.Dequeue();
            string? url = (f.Length == 0 ? Root : ItemUrl(f)) + $"/children?$select={Select}&$top=999";
            while (url is not null)
            {
                var u = url;
                using var r = await session.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, u), ct, allowed: HttpStatusCode.NotFound);
                if (r.StatusCode == HttpStatusCode.NotFound) break;
                var page = await r.Content.ReadFromJsonAsync<Page>(Web, ct);
                foreach (var c in page?.Value ?? [])
                {
                    var p = f.Length == 0 ? c.Name : f + "/" + c.Name;
                    if (c.Folder is not null) queue.Enqueue(p);
                    else if (c.File is not null) items.Add(Item(p, c));
                }
                url = page?.NextLink;
            }
        }
        return items;
    }

    public async Task<CloudItem> UploadAsync(string path, Stream content, long size, IProgress<long>? progress, CancellationToken ct)
    {
        path = CloudPath.Normalize(path);
        if (size <= SimpleLimit)
        {
            var data = new byte[size];
            await content.ReadExactlyAsync(data, ct);
            using var r = await session.SendAsync(() => new HttpRequestMessage(HttpMethod.Put, ItemUrl(path) + "/content") { Content = new ByteArrayContent(data) }, ct);
            progress?.Report(size);
            return Item(path, (await r.Content.ReadFromJsonAsync<DriveItem>(Web, ct))!);
        }

        string uploadUrl;
        using (var r = await session.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, ItemUrl(path) + "/createUploadSession")
               { Content = JsonContent.Create(new Dictionary<string, object> { ["item"] = new Dictionary<string, string> { ["@microsoft.graph.conflictBehavior"] = "replace" } }) }, ct))
            uploadUrl = (await r.Content.ReadFromJsonAsync<UploadSession>(Web, ct))!.UploadUrl;
        try
        {
            var buffer = new byte[ChunkSize];
            long sent = 0;
            while (true)
            {
                var n = (int)Math.Min(ChunkSize, size - sent);
                await content.ReadExactlyAsync(buffer.AsMemory(0, n), ct);
                // The upload URL is pre-authorised: no Authorization header (Graph rejects it there).
                using var req = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = new ByteArrayContent(buffer, 0, n) };
                req.Content.Headers.TryAddWithoutValidation("Content-Range", $"bytes {sent}-{sent + n - 1}/{size}");
                HttpResponseMessage r;
                try { r = await session.Http.SendAsync(req, ct); }
                catch (HttpRequestException ex) { throw new CloudException("Couldn't reach OneDrive: " + ex.Message, null, ex); }
                using (r)
                {
                    if (!r.IsSuccessStatusCode) throw new CloudException($"OneDrive answered {(int)r.StatusCode}: {OAuthSession.Summary(await r.Content.ReadAsStringAsync(ct))}", r.StatusCode);
                    sent += n;
                    progress?.Report(sent);
                    if (r.StatusCode == HttpStatusCode.Accepted) continue;
                    if (sent != size) throw new CloudException("OneDrive finished the upload early.");
                    return Item(path, (await r.Content.ReadFromJsonAsync<DriveItem>(Web, ct))!);
                }
            }
        }
        catch
        {
            try { using var _ = await session.Http.DeleteAsync(uploadUrl, CancellationToken.None); } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
            throw;
        }
    }

    public async Task DownloadAsync(string path, Stream destination, CancellationToken ct)
    {
        using var r = await session.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, ItemUrl(CloudPath.Normalize(path)) + "/content"), ct, stream: true);
        await r.Content.CopyToAsync(destination, ct);
    }

    public async Task MoveAsync(string from, string to, CancellationToken ct)
    {
        to = CloudPath.Normalize(to);
        var parentId = await EnsureFolderAsync(CloudPath.Parent(to), ct);
        using var _ = await session.SendAsync(() => new HttpRequestMessage(HttpMethod.Patch, ItemUrl(CloudPath.Normalize(from)))
        { Content = JsonContent.Create(new { parentReference = new { id = parentId }, name = CloudPath.Name(to) }) }, ct);
    }

    public async Task RemoveAsync(string path, CancellationToken ct)
    {
        using var _ = await session.SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, ItemUrl(CloudPath.Normalize(path))), ct, allowed: HttpStatusCode.NotFound);
    }

    private async Task<string> EnsureFolderAsync(string path, CancellationToken ct)
    {
        using (var r = await session.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{ItemUrl(path)}?$select=id"), ct, allowed: HttpStatusCode.NotFound))
            if (r.StatusCode != HttpStatusCode.NotFound) return (await r.Content.ReadFromJsonAsync<DriveItem>(Web, ct))!.Id;
        var parent = await EnsureFolderAsync(CloudPath.Parent(path), ct);
        using var c = await session.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"https://graph.microsoft.com/v1.0/me/drive/items/{parent}/children")
        {
            // "fail", never "replace": replacing would delete a folder that already exists.
            Content = JsonContent.Create(new Dictionary<string, object> { ["name"] = CloudPath.Name(path), ["folder"] = new { }, ["@microsoft.graph.conflictBehavior"] = "fail" }),
        }, ct, allowed: HttpStatusCode.Conflict);
        if (c.StatusCode == HttpStatusCode.Conflict) return await EnsureFolderAsync(path, ct); // created meanwhile: look it up
        return (await c.Content.ReadFromJsonAsync<DriveItem>(Web, ct))!.Id;
    }

    private static CloudItem Item(string path, DriveItem d) =>
        new(path, d.Size, d.LastModifiedDateTime, d.File?.Hashes?.QuickXorHash is null ? null : CloudHashes.QuickXor, d.File?.Hashes?.QuickXorHash, d.Id);
}
