using System.Net;

namespace Miu.Cloud;

/// <param name="Path">'/'-separated path inside the account's backup area.</param>
/// <param name="HashKind">Which <see cref="CloudHashes"/> algorithm <paramref name="Hash"/> uses, or null when the provider gives none.</param>
public sealed record CloudItem(string Path, long Size, DateTime? ModifiedUtc, string? HashKind, string? Hash, string Id = "");

public sealed class CloudException(string message, HttpStatusCode? status = null, Exception? inner = null) : Exception(message, inner)
{
    public HttpStatusCode? Status { get; } = status;

    /// <summary>Worth retrying later: server errors, throttling, timeouts.</summary>
    public bool IsTransient => Status is null or HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int?)Status >= 500;
}

/// <summary>
/// One cloud account's storage, addressed by paths ('/'-separated, no leading slash). Providers keep Miu inside its own
/// area: Google Drive files Miu created, the OneDrive and Dropbox app folders, or an S3 bucket.
/// </summary>
public interface ICloudStorage
{
    string ProviderName { get; }

    /// <summary>The item at this path, or null when there is none.</summary>
    Task<CloudItem?> GetAsync(string path, CancellationToken ct);

    /// <summary>Files under a folder (all levels).</summary>
    Task<IReadOnlyList<CloudItem>> ListAsync(string folder, CancellationToken ct);

    /// <summary>Uploads (replacing any file at the path) and returns what the provider stored, including its checksum.</summary>
    Task<CloudItem> UploadAsync(string path, Stream content, long size, IProgress<long>? progress, CancellationToken ct);

    Task DownloadAsync(string path, Stream destination, CancellationToken ct);

    Task MoveAsync(string from, string to, CancellationToken ct);

    /// <summary>Removes a file: into the provider's trash where it has one (Drive, OneDrive, Dropbox).</summary>
    Task RemoveAsync(string path, CancellationToken ct);
}

public static class CloudPath
{
    public static string Normalize(string path) => string.Join('/', path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));
    public static string Parent(string path) => path.LastIndexOf('/') is var i and > 0 ? path[..i] : "";
    public static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];
}
