using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Miu.Cloud;

/// <summary>
/// Google Drive with the drive.file scope: Miu can only see and change files it created. Uploads use resumable
/// sessions in 8 MiB chunks; removing a file moves it to the Drive trash.
/// </summary>
public sealed class GoogleDriveStorage(OAuthSession session) : ICloudStorage
{
    private const string Api = "https://www.googleapis.com/drive/v3/files";
    private const string UploadApi = "https://www.googleapis.com/upload/drive/v3/files";
    private const string FolderMime = "application/vnd.google-apps.folder";
    private const string Fields = "id,name,mimeType,size,md5Checksum,modifiedTime";
    public const int ChunkSize = 32 * 256 * 1024; // 8 MiB, a multiple of 256 KiB as Drive requires
    private readonly Dictionary<string, string> _folders = new(StringComparer.Ordinal) { [""] = "root" };

    public string ProviderName => "Google Drive";
    public string? ExpectedHashKind(long size) => CloudHashes.Md5;

    private sealed record DriveFile(string Id, string Name, string MimeType, string? Size, string? Md5Checksum, DateTime? ModifiedTime);
    private sealed record DriveList(List<DriveFile> Files, string? NextPageToken);

    public async Task<CloudItem?> GetAsync(string path, CancellationToken ct)
    {
        path = CloudPath.Normalize(path);
        if (await FolderIdAsync(CloudPath.Parent(path), create: false, ct) is not { } parent) return null;
        var f = await FindAsync(parent, CloudPath.Name(path), folder: false, ct);
        return f is null ? null : Item(path, f);
    }

    public async Task<IReadOnlyList<CloudItem>> ListAsync(string folder, CancellationToken ct)
    {
        folder = CloudPath.Normalize(folder);
        var items = new List<CloudItem>();
        if (await FolderIdAsync(folder, create: false, ct) is not { } id) return items;
        var queue = new Queue<(string Id, string Path)>([(id, folder)]);
        while (queue.Count > 0)
        {
            var (fid, fpath) = queue.Dequeue();
            foreach (var f in await ChildrenAsync($"'{fid}' in parents and trashed = false", ct))
            {
                var p = fpath.Length == 0 ? f.Name : fpath + "/" + f.Name;
                if (f.MimeType == FolderMime) { _folders[p] = f.Id; queue.Enqueue((f.Id, p)); }
                else items.Add(Item(p, f));
            }
        }
        return items;
    }

    public async Task<CloudItem> UploadAsync(string path, Stream content, long size, IProgress<long>? progress, CancellationToken ct)
    {
        path = CloudPath.Normalize(path);
        var parent = await FolderIdAsync(CloudPath.Parent(path), create: true, ct) ?? throw new CloudException("Couldn't create the folder.");
        var existing = await FindAsync(parent, CloudPath.Name(path), folder: false, ct);
        using var start = await session.SendAsync(() =>
        {
            var req = existing is null
                ? new HttpRequestMessage(HttpMethod.Post, $"{UploadApi}?uploadType=resumable&fields={Fields}") { Content = JsonContent.Create(new { name = CloudPath.Name(path), parents = new[] { parent } }) }
                : new HttpRequestMessage(HttpMethod.Patch, $"{UploadApi}/{existing.Id}?uploadType=resumable&fields={Fields}") { Content = JsonContent.Create(new { }) };
            req.Headers.Add("X-Upload-Content-Length", size.ToString(CultureInfo.InvariantCulture));
            return req;
        }, ct);
        var sessionUri = start.Headers.Location ?? throw new CloudException("Google Drive didn't start the upload.");

        var buffer = new byte[ChunkSize];
        long sent = 0;
        while (true)
        {
            var n = (int)Math.Min(ChunkSize, size - sent);
            if (n > 0) await content.ReadExactlyAsync(buffer.AsMemory(0, n), ct);
            var range = size == 0 ? "bytes */0" : $"bytes {sent}-{sent + n - 1}/{size}";
            using var r = await session.SendAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Put, sessionUri) { Content = new ByteArrayContent(buffer, 0, n) };
                req.Content.Headers.TryAddWithoutValidation("Content-Range", range);
                return req;
            }, ct, allowed: (HttpStatusCode)308);
            sent += n;
            progress?.Report(sent);
            if ((int)r.StatusCode == 308) continue; // more to send
            var f = await r.Content.ReadFromJsonAsync<DriveFile>(ct) ?? throw new CloudException("Google Drive didn't confirm the upload.");
            if (sent != size) throw new CloudException("Google Drive finished the upload early.");
            return Item(path, f);
        }
    }

    public async Task DownloadAsync(string path, Stream destination, CancellationToken ct)
    {
        var item = await GetAsync(path, ct) ?? throw new CloudException("Not found: " + path, HttpStatusCode.NotFound);
        using var r = await session.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{Api}/{item.Id}?alt=media"), ct, stream: true);
        await r.Content.CopyToAsync(destination, ct);
    }

    public async Task MoveAsync(string from, string to, CancellationToken ct)
    {
        from = CloudPath.Normalize(from);
        to = CloudPath.Normalize(to);
        var oldParent = await FolderIdAsync(CloudPath.Parent(from), create: false, ct) ?? throw new CloudException("Not found: " + from, HttpStatusCode.NotFound);
        var item = await FindAsync(oldParent, CloudPath.Name(from), folder: false, ct) ?? throw new CloudException("Not found: " + from, HttpStatusCode.NotFound);
        var newParent = await FolderIdAsync(CloudPath.Parent(to), create: true, ct)!;
        using var _ = await session.SendAsync(() => new HttpRequestMessage(HttpMethod.Patch, $"{Api}/{item.Id}?addParents={newParent}&removeParents={oldParent}&fields=id")
        { Content = JsonContent.Create(new { name = CloudPath.Name(to) }) }, ct);
    }

    public async Task RemoveAsync(string path, CancellationToken ct)
    {
        if (await GetAsync(path, ct) is not { } item) return;
        using var _ = await session.SendAsync(() => new HttpRequestMessage(HttpMethod.Patch, $"{Api}/{item.Id}?fields=id") { Content = JsonContent.Create(new { trashed = true }) }, ct);
    }

    private async Task<string?> FolderIdAsync(string path, bool create, CancellationToken ct)
    {
        if (_folders.TryGetValue(path, out var known)) return known;
        var parent = await FolderIdAsync(CloudPath.Parent(path), create, ct);
        if (parent is null) return null;
        var name = CloudPath.Name(path);
        var found = await FindAsync(parent, name, folder: true, ct);
        if (found is null)
        {
            if (!create) return null;
            using var r = await session.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{Api}?fields=id")
            { Content = JsonContent.Create(new { name, mimeType = FolderMime, parents = new[] { parent } }) }, ct);
            found = await r.Content.ReadFromJsonAsync<DriveFile>(ct);
        }
        return _folders[path] = found!.Id;
    }

    private async Task<DriveFile?> FindAsync(string parent, string name, bool folder, CancellationToken ct)
    {
        var q = $"name = '{Escape(name)}' and '{parent}' in parents and trashed = false and mimeType {(folder ? "=" : "!=")} '{FolderMime}'";
        return (await ChildrenAsync(q, ct)).FirstOrDefault();
    }

    private async Task<List<DriveFile>> ChildrenAsync(string q, CancellationToken ct)
    {
        var all = new List<DriveFile>();
        string? page = null;
        do
        {
            var url = $"{Api}?q={Uri.EscapeDataString(q)}&fields={Uri.EscapeDataString($"nextPageToken,files({Fields})")}&pageSize=1000&spaces=drive" +
                      (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page));
            using var r = await session.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
            var list = await r.Content.ReadFromJsonAsync<DriveList>(new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
            all.AddRange(list?.Files ?? []);
            page = list?.NextPageToken;
        } while (page is not null);
        return all;
    }

    private static string Escape(string name) => name.Replace("\\", "\\\\").Replace("'", "\\'");

    private static CloudItem Item(string path, DriveFile f) =>
        new(path, long.TryParse(f.Size, out var s) ? s : 0, f.ModifiedTime, f.Md5Checksum is null ? null : CloudHashes.Md5, f.Md5Checksum, f.Id);
}
