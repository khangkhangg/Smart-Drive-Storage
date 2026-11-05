using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Miu.Cloud;

/// <param name="Endpoint">e.g. https://s3.eu-west-1.amazonaws.com, https://s3.us-west-002.backblazeb2.com, https://&lt;account&gt;.r2.cloudflarestorage.com</param>
public sealed record S3Config(string Endpoint, string Region, string Bucket, string AccessKey, string SecretKey);

/// <summary>
/// S3 and S3-compatible storage (AWS, Backblaze B2, Wasabi, Cloudflare R2, MinIO). Files up to 64 MB go in one
/// request; bigger files use multipart upload (aborted if anything fails). Every request is signed with SigV4 over the
/// payload's SHA-256, so S3 rejects anything that arrives damaged.
/// </summary>
public sealed class S3Storage(HttpClient http, S3Config config, Func<DateTime>? utcNow = null) : ICloudStorage
{
    public const long SinglePutLimit = 64L << 20;
    private const long MaxCopy = 5L << 30;
    private static readonly XNamespace Ns = "http://s3.amazonaws.com/doc/2006-03-01/";
    private readonly Func<DateTime> _now = utcNow ?? (() => DateTime.UtcNow);
    private readonly Uri _endpoint = new(config.Endpoint.TrimEnd('/'));

    public string ProviderName => "S3";
    public string? ExpectedHashKind(long size) => HashKindFor(size);

    /// <summary>AWS uses bucket.host addressing; other services use path addressing.</summary>
    private bool VirtualHost => _endpoint.Host.EndsWith("amazonaws.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Part size for a multipart upload: 16 MB, larger for huge files so there are at most 9,000 parts.</summary>
    public static long PartSizeFor(long size) => Math.Max(16L << 20, (size + 8999) / 9000 + 1);

    public static string HashKindFor(long size) => size <= SinglePutLimit ? CloudHashes.Md5 : CloudHashes.S3EtagPrefix + PartSizeFor(size).ToString(CultureInfo.InvariantCulture);

    public async Task<CloudItem?> GetAsync(string path, CancellationToken ct)
    {
        path = CloudPath.Normalize(path);
        using var r = await SendAsync(HttpMethod.Head, path, null, null, null, ct, allowNotFound: true);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        var size = r.Content.Headers.ContentLength ?? 0;
        // Encrypted buckets (SSE-KMS / SSE-C) don't use the MD5 as ETag, so single uploads also carry Miu's own MD5.
        var hash = size <= SinglePutLimit && r.Headers.TryGetValues("x-amz-meta-miu-md5", out var md5s) ? md5s.First() : r.Headers.ETag?.Tag.Trim('"');
        return new CloudItem(path, size, r.Content.Headers.LastModified?.UtcDateTime, HashKindFor(size), hash, path);
    }

    public async Task<IReadOnlyList<CloudItem>> ListAsync(string folder, CancellationToken ct)
    {
        var prefix = CloudPath.Normalize(folder);
        if (prefix.Length > 0) prefix += "/";
        var items = new List<CloudItem>();
        string? token = null;
        do
        {
            var q = new Dictionary<string, string> { ["list-type"] = "2", ["prefix"] = prefix };
            if (token is not null) q["continuation-token"] = token;
            using var r = await SendAsync(HttpMethod.Get, "", q, null, null, ct);
            var doc = XDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            var root = doc.Root!;
            var ns = root.Name.Namespace;
            foreach (var c in root.Elements(ns + "Contents"))
            {
                var key = c.Element(ns + "Key")!.Value;
                if (key.EndsWith('/')) continue;
                var size = long.Parse(c.Element(ns + "Size")!.Value, CultureInfo.InvariantCulture);
                var modified = DateTime.Parse(c.Element(ns + "LastModified")!.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                items.Add(new CloudItem(key, size, modified, HashKindFor(size), c.Element(ns + "ETag")?.Value.Trim('"'), key));
            }
            token = root.Element(ns + "IsTruncated")?.Value == "true" ? root.Element(ns + "NextContinuationToken")?.Value : null;
        } while (token is not null);
        return items;
    }

    public async Task<CloudItem> UploadAsync(string path, Stream content, long size, IProgress<long>? progress, CancellationToken ct)
    {
        path = CloudPath.Normalize(path);
        if (size <= SinglePutLimit)
        {
            var data = new byte[size];
            await content.ReadExactlyAsync(data, ct);
            if (content.ReadByte() != -1) throw new CloudException("The file grew while it was being uploaded.");
            var md5 = MD5.HashData(data);
            using var r = await SendAsync(HttpMethod.Put, path, null, data, new Dictionary<string, string>
            {
                ["content-md5"] = Convert.ToBase64String(md5), ["x-amz-meta-miu-md5"] = Convert.ToHexStringLower(md5),
            }, ct);
            progress?.Report(size);
            // S3 rejects the upload unless the bytes match Content-MD5, so a 2xx means it stored exactly these bytes.
            return new CloudItem(path, size, _now(), CloudHashes.Md5, Convert.ToHexStringLower(md5), path);
        }

        var partSize = PartSizeFor(size);
        string uploadId;
        using (var init = await SendAsync(HttpMethod.Post, path, new Dictionary<string, string> { ["uploads"] = "" }, null, null, ct))
            uploadId = XDocument.Parse(await init.Content.ReadAsStringAsync(ct)).Root!.Elements().First(e => e.Name.LocalName == "UploadId").Value;
        try
        {
            var etags = new List<string>();
            var partMd5s = new List<byte>();
            var buffer = new byte[partSize];
            long sent = 0;
            for (var part = 1; sent < size; part++)
            {
                var n = (int)Math.Min(partSize, size - sent);
                await content.ReadExactlyAsync(buffer.AsMemory(0, n), ct);
                var chunk = buffer.AsSpan(0, n).ToArray();
                var partMd5 = MD5.HashData(chunk);
                partMd5s.AddRange(partMd5);
                using var r = await SendAsync(HttpMethod.Put, path,
                    new Dictionary<string, string> { ["partNumber"] = part.ToString(CultureInfo.InvariantCulture), ["uploadId"] = uploadId }, chunk,
                    new Dictionary<string, string> { ["content-md5"] = Convert.ToBase64String(partMd5) }, ct);
                etags.Add(r.Headers.ETag?.Tag ?? throw new CloudException("The storage didn't confirm a part."));
                sent += n;
                progress?.Report(sent);
            }
            if (content.ReadByte() != -1) throw new CloudException("The file grew while it was being uploaded.");
            var xml = new XElement("CompleteMultipartUpload", etags.Select((e, i) => new XElement("Part", new XElement("PartNumber", i + 1), new XElement("ETag", e))));
            using var done = await SendAsync(HttpMethod.Post, path, new Dictionary<string, string> { ["uploadId"] = uploadId }, Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting)), null, ct);
            var body = await done.Content.ReadAsStringAsync(ct);
            if (body.Contains("<Error>", StringComparison.Ordinal)) throw new CloudException("The storage couldn't complete the upload: " + ErrorMessage(body)); // S3 can report errors with 200
            // Every part was checked against its Content-MD5, so the multipart ETag of what was sent describes what S3 stored.
            var etag = Convert.ToHexStringLower(MD5.HashData(partMd5s.ToArray())) + "-" + etags.Count.ToString(CultureInfo.InvariantCulture);
            return new CloudItem(path, size, _now(), HashKindFor(size), etag, path);
        }
        catch
        {
            try { using var _ = await SendAsync(HttpMethod.Delete, path, new Dictionary<string, string> { ["uploadId"] = uploadId }, null, null, CancellationToken.None, allowNotFound: true); }
            catch (Exception ex) when (ex is HttpRequestException or CloudException) { } // best effort: the bucket's lifecycle rules clean up the rest
            throw;
        }
    }

    public async Task DownloadAsync(string path, Stream destination, CancellationToken ct)
    {
        using var r = await SendAsync(HttpMethod.Get, CloudPath.Normalize(path), null, null, null, ct, stream: true);
        await r.Content.CopyToAsync(destination, ct);
    }

    public async Task MoveAsync(string from, string to, CancellationToken ct)
    {
        from = CloudPath.Normalize(from);
        to = CloudPath.Normalize(to);
        var item = await GetAsync(from, ct) ?? throw new CloudException("Not found: " + from, HttpStatusCode.NotFound);
        var source = "/" + config.Bucket + "/" + SigV4.Encode(from, keepSlash: true);
        if (item.Size <= MaxCopy)
        {
            using var r = await SendAsync(HttpMethod.Put, to, null, null, new Dictionary<string, string> { ["x-amz-copy-source"] = source }, ct);
            var body = await r.Content.ReadAsStringAsync(ct);
            if (body.Contains("<Error>", StringComparison.Ordinal)) throw new CloudException("Copy failed: " + ErrorMessage(body));
        }
        else await MultipartCopyAsync(source, to, item.Size, ct);
        // Only remove the original once the copy is really there.
        var copy = await GetAsync(to, ct);
        if (copy is null || copy.Size != item.Size) throw new CloudException("The copy didn't arrive complete, so the original was kept.");
        using var _ = await SendAsync(HttpMethod.Delete, from, null, null, null, ct, allowNotFound: true);
    }

    /// <summary>Copies objects over 5 GB in 1 GB ranges (a single copy request is limited to 5 GB).</summary>
    private async Task MultipartCopyAsync(string source, string to, long size, CancellationToken ct)
    {
        const long range = 1L << 30;
        string uploadId;
        using (var init = await SendAsync(HttpMethod.Post, to, new Dictionary<string, string> { ["uploads"] = "" }, null, null, ct))
            uploadId = XDocument.Parse(await init.Content.ReadAsStringAsync(ct)).Root!.Elements().First(e => e.Name.LocalName == "UploadId").Value;
        try
        {
            var etags = new List<string>();
            for (long start = 0, part = 1; start < size; start += range, part++)
            {
                var end = Math.Min(size, start + range) - 1;
                using var r = await SendAsync(HttpMethod.Put, to,
                    new Dictionary<string, string> { ["partNumber"] = part.ToString(CultureInfo.InvariantCulture), ["uploadId"] = uploadId }, null,
                    new Dictionary<string, string> { ["x-amz-copy-source"] = source, ["x-amz-copy-source-range"] = $"bytes={start}-{end}" }, ct);
                etags.Add(XDocument.Parse(await r.Content.ReadAsStringAsync(ct)).Root!.Elements().First(e => e.Name.LocalName == "ETag").Value);
            }
            var xml = new XElement("CompleteMultipartUpload", etags.Select((e, i) => new XElement("Part", new XElement("PartNumber", i + 1), new XElement("ETag", e))));
            using var done = await SendAsync(HttpMethod.Post, to, new Dictionary<string, string> { ["uploadId"] = uploadId }, Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting)), null, ct);
            var body = await done.Content.ReadAsStringAsync(ct);
            if (body.Contains("<Error>", StringComparison.Ordinal)) throw new CloudException("The storage couldn't complete the copy: " + ErrorMessage(body)); // 200 with an error
        }
        catch
        {
            try { using var _ = await SendAsync(HttpMethod.Delete, to, new Dictionary<string, string> { ["uploadId"] = uploadId }, null, null, CancellationToken.None, allowNotFound: true); }
            catch (Exception ex) when (ex is HttpRequestException or CloudException) { }
            throw;
        }
    }

    public async Task RemoveAsync(string path, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Delete, CloudPath.Normalize(path), null, null, null, ct, allowNotFound: true);
    }

    /// <summary>Checks the keys and bucket (lists one key).</summary>
    public async Task TestAsync(CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Get, "", new Dictionary<string, string> { ["list-type"] = "2", ["max-keys"] = "1" }, null, null, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string key, Dictionary<string, string>? query, byte[]? body,
        Dictionary<string, string>? extraHeaders, CancellationToken ct, bool allowNotFound = false, bool stream = false)
    {
        query ??= [];
        var host = VirtualHost ? $"{config.Bucket}.{_endpoint.Host}" : _endpoint.Host;
        if (!_endpoint.IsDefaultPort) host += ":" + _endpoint.Port.ToString(CultureInfo.InvariantCulture);
        var basePath = _endpoint.AbsolutePath.TrimEnd('/');
        var path = basePath + (VirtualHost ? "" : "/" + SigV4.Encode(config.Bucket)) + "/" + SigV4.Encode(key, keepSlash: true);
        var payload = body is null ? SigV4.EmptyPayload : SigV4.Hex(SHA256.HashData(body));
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["host"] = host };
        if (extraHeaders is not null) foreach (var (k, v) in extraHeaders) headers[k] = v;
        var auth = SigV4.Sign(method.Method, path, query, headers, payload, config.AccessKey, config.SecretKey, config.Region, "s3", _now());

        var qs = string.Join('&', query.Select(kv => kv.Value.Length == 0 && kv.Key == "uploads" ? "uploads" : SigV4.Encode(kv.Key) + "=" + SigV4.Encode(kv.Value)));
        var uri = new Uri($"{_endpoint.Scheme}://{host}{path}{(qs.Length > 0 ? "?" + qs : "")}");
        using var req = new HttpRequestMessage(method, uri);
        req.Headers.TryAddWithoutValidation("Authorization", auth);
        if (body is not null)
        {
            req.Content = new ByteArrayContent(body);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }
        foreach (var (k, v) in headers)
        {
            if (k.Equals("host", StringComparison.OrdinalIgnoreCase)) continue;
            if (k.Equals("content-md5", StringComparison.OrdinalIgnoreCase)) req.Content?.Headers.TryAddWithoutValidation("Content-MD5", v);
            else req.Headers.TryAddWithoutValidation(k, v);
        }
        HttpResponseMessage r;
        try { r = await http.SendAsync(req, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct); }
        catch (HttpRequestException ex) { throw new CloudException("Couldn't reach the storage: " + ex.Message, null, ex); }
        if (r.IsSuccessStatusCode || (allowNotFound && r.StatusCode == HttpStatusCode.NotFound)) return r;
        var text = method == HttpMethod.Head ? "" : await r.Content.ReadAsStringAsync(ct);
        r.Dispose();
        throw new CloudException(r.StatusCode switch
        {
            HttpStatusCode.Forbidden => "Access denied. Check the access key, secret key and bucket. " + ErrorMessage(text),
            HttpStatusCode.NotFound => "Not found. Check the bucket name and endpoint. " + ErrorMessage(text),
            _ => $"The storage answered {(int)r.StatusCode}. " + ErrorMessage(text),
        }, r.StatusCode);
    }

    private static string ErrorMessage(string xml)
    {
        try { return XDocument.Parse(xml).Root?.Element("Message")?.Value ?? ""; }
        catch (System.Xml.XmlException) { return ""; }
    }
}
