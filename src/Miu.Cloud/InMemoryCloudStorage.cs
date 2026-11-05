using System.Security.Cryptography;

namespace Miu.Cloud;

/// <summary>A cloud that lives in memory: for tests and demos. Reports md5 checksums, like Google Drive.</summary>
public sealed class InMemoryCloudStorage(string name = "Test cloud") : ICloudStorage
{
    private readonly Dictionary<string, (byte[] Data, DateTime Modified)> _files = new(StringComparer.Ordinal);

    public string ProviderName => name;
    public string? ExpectedHashKind(long size) => NoHash ? null : CloudHashes.Md5;
    public IReadOnlyDictionary<string, (byte[] Data, DateTime Modified)> Files => _files;
    public List<string> Removed { get; } = [];

    /// <summary>Uploads to matching paths fail.</summary>
    public Func<string, bool> FailUpload { get; set; } = _ => false;
    /// <summary>Stores different bytes than were sent (a corrupted upload).</summary>
    public Func<string, bool> Corrupt { get; set; } = _ => false;
    /// <summary>Reports no checksum (like a provider that has none).</summary>
    public bool NoHash { get; set; }
    public int Uploads { get; private set; }

    public Task<CloudItem?> GetAsync(string path, CancellationToken ct) =>
        Task.FromResult(_files.TryGetValue(CloudPath.Normalize(path), out var f) ? Item(CloudPath.Normalize(path), f) : null);

    public Task<IReadOnlyList<CloudItem>> ListAsync(string folder, CancellationToken ct)
    {
        var prefix = CloudPath.Normalize(folder);
        prefix = prefix.Length == 0 ? "" : prefix + "/";
        return Task.FromResult<IReadOnlyList<CloudItem>>(_files.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(kv => Item(kv.Key, kv.Value)).ToList());
    }

    public async Task<CloudItem> UploadAsync(string path, Stream content, long size, IProgress<long>? progress, CancellationToken ct)
    {
        path = CloudPath.Normalize(path);
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        progress?.Report(ms.Length);
        if (FailUpload(path)) throw new CloudException("Upload failed (test).", System.Net.HttpStatusCode.ServiceUnavailable);
        var data = ms.ToArray();
        if (Corrupt(path) && data.Length > 0) data[0] ^= 0xFF;
        _files[path] = (data, DateTime.UtcNow);
        Uploads++;
        return Item(path, _files[path]);
    }

    public Task DownloadAsync(string path, Stream destination, CancellationToken ct) =>
        _files.TryGetValue(CloudPath.Normalize(path), out var f) ? destination.WriteAsync(f.Data, ct).AsTask() : throw new CloudException("Not found.", System.Net.HttpStatusCode.NotFound);

    public Task MoveAsync(string from, string to, CancellationToken ct)
    {
        from = CloudPath.Normalize(from);
        if (!_files.Remove(from, out var f)) throw new CloudException("Not found.", System.Net.HttpStatusCode.NotFound);
        _files[CloudPath.Normalize(to)] = f;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string path, CancellationToken ct)
    {
        _files.Remove(CloudPath.Normalize(path));
        Removed.Add(CloudPath.Normalize(path));
        return Task.CompletedTask;
    }

    /// <summary>Puts a file in place directly (test setup).</summary>
    public void Put(string path, byte[] data) => _files[CloudPath.Normalize(path)] = (data, DateTime.UtcNow);

    private CloudItem Item(string path, (byte[] Data, DateTime Modified) f) =>
        new(path, f.Data.Length, f.Modified, NoHash ? null : CloudHashes.Md5, NoHash ? null : Convert.ToHexStringLower(MD5.HashData(f.Data)), path);
}
