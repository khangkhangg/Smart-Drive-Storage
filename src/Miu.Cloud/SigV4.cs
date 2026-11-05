using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Miu.Cloud;

/// <summary>AWS Signature Version 4 (used by S3 and every S3-compatible service).</summary>
public static class SigV4
{
    public const string EmptyPayload = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>
    /// Returns the Authorization header value. <paramref name="headers"/> must contain every header to sign (host included);
    /// x-amz-date and x-amz-content-sha256 are added to it.
    /// </summary>
    /// <param name="canonicalPath">The already URI-encoded path, e.g. "/bucket/my%20file.txt".</param>
    public static string Sign(string method, string canonicalPath, IReadOnlyDictionary<string, string> query, IDictionary<string, string> headers,
        string payloadHash, string accessKey, string secretKey, string region, string service, DateTime utcNow)
    {
        var amzDate = utcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var day = amzDate[..8];
        headers["x-amz-date"] = amzDate;
        headers["x-amz-content-sha256"] = payloadHash;

        var canonicalQuery = string.Join('&', query
            .Select(kv => (K: Encode(kv.Key), V: Encode(kv.Value)))
            .OrderBy(kv => kv.K, StringComparer.Ordinal).ThenBy(kv => kv.V, StringComparer.Ordinal)
            .Select(kv => kv.K + "=" + kv.V));
        var signed = headers.Select(h => (Name: h.Key.ToLowerInvariant(), Value: Collapse(h.Value)))
            .OrderBy(h => h.Name, StringComparer.Ordinal).ToList();
        var canonicalHeaders = string.Concat(signed.Select(h => $"{h.Name}:{h.Value}\n"));
        var signedHeaders = string.Join(';', signed.Select(h => h.Name));
        var canonicalRequest = $"{method}\n{canonicalPath}\n{canonicalQuery}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";

        var scope = $"{day}/{region}/{service}/aws4_request";
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";
        var key = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), day), region), service), "aws4_request");
        var signature = Hex(Hmac(key, stringToSign));
        return $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
    }

    /// <summary>URI-encodes per AWS rules: everything except A-Z a-z 0-9 - _ . ~ (and '/' when <paramref name="keepSlash"/>).</summary>
    public static string Encode(string s, bool keepSlash = false)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            var c = (char)b;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or '~' || (keepSlash && c == '/')) sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    public static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    private static string Collapse(string v) => string.Join(' ', v.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
}
