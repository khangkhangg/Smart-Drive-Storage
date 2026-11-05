using System.Security.Cryptography;
using System.Text;
using Miu.Cloud;

namespace Miu.Cloud.Tests;

public class HashesAndSigningTests
{
    private static string Hash(string kind, byte[] data) => CloudHashes.ComputeAsync(kind, new MemoryStream(data), default).GetAwaiter().GetResult()!;

    [Fact]
    public void Md5_and_s3_etags()
    {
        Hash(CloudHashes.Md5, Encoding.ASCII.GetBytes("abc")).Should().Be("900150983cd24fb0d6963f7d28e17f72");
        var data = new byte[25];
        new Random(3).NextBytes(data);
        var parts = new[] { data[..10], data[10..20], data[20..] };
        var expected = Convert.ToHexStringLower(MD5.HashData(parts.SelectMany(MD5.HashData).ToArray())) + "-3";
        Hash(CloudHashes.S3EtagPrefix + "10", data).Should().Be(expected);
        Hash(CloudHashes.S3EtagPrefix + "100", data).Should().Be(Convert.ToHexStringLower(MD5.HashData(data))); // one part = plain md5
    }

    [Fact]
    public void Dropbox_content_hash_is_sha256_of_block_sha256s()
    {
        var small = Encoding.ASCII.GetBytes("hello");
        Hash(CloudHashes.Dropbox, small).Should().Be(Convert.ToHexStringLower(SHA256.HashData(SHA256.HashData(small))));
        var big = new byte[4 * 1024 * 1024 + 7];
        new Random(5).NextBytes(big);
        var blocks = SHA256.HashData(big.AsSpan(0, 4 * 1024 * 1024)).Concat(SHA256.HashData(big.AsSpan(4 * 1024 * 1024))).ToArray();
        Hash(CloudHashes.Dropbox, big).Should().Be(Convert.ToHexStringLower(SHA256.HashData(blocks)));
        // Fed in odd pieces, the result is the same.
        var h = CloudHashes.Create(CloudHashes.Dropbox)!;
        for (var i = 0; i < big.Length; i += 999_983) h.Append(big.AsSpan(i, Math.Min(999_983, big.Length - i)));
        h.Finish().Should().Be(Hash(CloudHashes.Dropbox, big));
    }

    [Fact]
    public void QuickXor_follows_the_reference_algorithm()
    {
        Hash(CloudHashes.QuickXor, []).Should().Be(Convert.ToBase64String(new byte[20]));
        var one = new byte[20];
        one[0] = 0x61;  // 'a' at bit 0
        one[12] ^= 1;   // the length (1), little-endian, xored into the last 8 bytes
        Hash(CloudHashes.QuickXor, [0x61]).Should().Be(Convert.ToBase64String(one));
        var two = new byte[20];
        two[0] = 0x61;
        two[1] = unchecked((byte)(0x62 << 3)); // the 2nd byte goes in at bit 11
        two[2] = 0x62 >> 5;
        two[12] ^= 2;
        Hash(CloudHashes.QuickXor, [0x61, 0x62]).Should().Be(Convert.ToBase64String(two));
        var data = new byte[1000];
        new Random(9).NextBytes(data);
        var h = CloudHashes.Create(CloudHashes.QuickXor)!;
        h.Append(data.AsSpan(0, 333));
        h.Append(data.AsSpan(333));
        h.Finish().Should().Be(Hash(CloudHashes.QuickXor, data));
    }

    [Fact]
    public void SigV4_matches_the_aws_get_object_example()
    {
        var headers = new Dictionary<string, string> { ["host"] = "examplebucket.s3.amazonaws.com", ["range"] = "bytes=0-9" };
        var auth = SigV4.Sign("GET", "/test.txt", new Dictionary<string, string>(), headers, SigV4.EmptyPayload,
            "AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "us-east-1", "s3", new DateTime(2013, 5, 24, 0, 0, 0, DateTimeKind.Utc));
        auth.Should().Be("AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, " +
                         "SignedHeaders=host;range;x-amz-content-sha256;x-amz-date, Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41");
    }

    [Fact]
    public void SigV4_matches_the_aws_put_object_example()
    {
        var payload = SigV4.Hex(SHA256.HashData(Encoding.UTF8.GetBytes("Welcome to Amazon S3.")));
        var headers = new Dictionary<string, string>
        {
            ["host"] = "examplebucket.s3.amazonaws.com", ["date"] = "Fri, 24 May 2013 00:00:00 GMT", ["x-amz-storage-class"] = "REDUCED_REDUNDANCY",
        };
        var auth = SigV4.Sign("PUT", "/" + SigV4.Encode("test$file.text"), new Dictionary<string, string>(), headers, payload,
            "AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "us-east-1", "s3", new DateTime(2013, 5, 24, 0, 0, 0, DateTimeKind.Utc));
        auth.Should().EndWith("Signature=98ad721746da40c64f1a55b78f14c238d841ea1380cd77a1b5971af0ece108bd");
    }

    [Fact]
    public void SigV4_encoding_rules()
    {
        SigV4.Encode("a b+c~d/é.txt", keepSlash: true).Should().Be("a%20b%2Bc~d/%C3%A9.txt");
        SigV4.Encode("a/b").Should().Be("a%2Fb");
    }

    [Fact]
    public async Task Throttled_stream_limits_speed_and_hashing_stream_counts()
    {
        var data = new byte[200_000];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await new ThrottledStream(new MemoryStream(data), 400_000).CopyToAsync(Stream.Null);
        sw.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(300));
        var h = new HashingStream(new MemoryStream(Encoding.ASCII.GetBytes("abc")), CloudHashes.Create(CloudHashes.Md5));
        await h.CopyToAsync(Stream.Null);
        h.BytesRead.Should().Be(3);
    }
}
