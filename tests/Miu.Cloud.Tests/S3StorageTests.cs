using System.Net;
using System.Security.Cryptography;
using System.Text;
using Miu.Cloud;

namespace Miu.Cloud.Tests;

public class S3StorageTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);

    private static S3Storage Store(FakeHttp http, string endpoint = "https://s3.us-west-002.backblazeb2.com") =>
        new(new HttpClient(http), new S3Config(endpoint, "us-west-002", "my-bucket", "KEYID", "SECRET"), () => Now);

    [Fact]
    public async Task Small_upload_is_one_signed_put_with_content_md5_path_style()
    {
        var http = new FakeHttp(_ => FakeHttp.Ok("", configure: r => r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"" + Convert.ToHexStringLower(MD5.HashData("hi"u8.ToArray())) + "\"")));
        var item = await Store(http).UploadAsync("Backup/My Photos/a+b.jpg", new MemoryStream("hi"u8.ToArray()), 2, null, default);
        var req = http.Requests.Single();
        req.Method.Should().Be(HttpMethod.Put);
        req.Uri.AbsoluteUri.Should().Be("https://s3.us-west-002.backblazeb2.com/my-bucket/Backup/My%20Photos/a%2Bb.jpg");
        req.Headers["authorization"].Should().StartWith("AWS4-HMAC-SHA256 Credential=KEYID/20261105/us-west-002/s3/aws4_request");
        req.Headers["authorization"].Should().Contain("content-md5");
        req.Headers["content-md5"].Should().Be(Convert.ToBase64String(MD5.HashData("hi"u8.ToArray())));
        req.Headers["x-amz-content-sha256"].Should().Be(SigV4.Hex(SHA256.HashData("hi"u8.ToArray())));
        item.HashKind.Should().Be(CloudHashes.Md5);
        item.Hash.Should().Be(Convert.ToHexStringLower(MD5.HashData("hi"u8.ToArray())));
    }

    [Fact]
    public async Task Aws_uses_virtual_host_addressing()
    {
        var http = new FakeHttp(_ => FakeHttp.Status(HttpStatusCode.NotFound));
        (await Store(http, "https://s3.eu-west-1.amazonaws.com").GetAsync("x.txt", default)).Should().BeNull();
        http.Requests.Single().Uri.AbsoluteUri.Should().Be("https://my-bucket.s3.eu-west-1.amazonaws.com/x.txt");
    }

    [Fact]
    public async Task Large_upload_uses_multipart_and_completes_with_every_etag()
    {
        var size = S3Storage.SinglePutLimit + 5;
        var http = new FakeHttp(r =>
        {
            if (r.Method == HttpMethod.Post && r.Uri.Query == "?uploads") return FakeHttp.Ok("<InitiateMultipartUploadResult><UploadId>U1</UploadId></InitiateMultipartUploadResult>", "application/xml");
            if (r.Method == HttpMethod.Put) return FakeHttp.Ok("", configure: x => x.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"e" + r.Uri.Query.Split('&')[0].Split('=')[1] + "\""));
            return FakeHttp.Ok("<CompleteMultipartUploadResult><ETag>\"abc-5\"</ETag></CompleteMultipartUploadResult>", "application/xml");
        });
        var item = await Store(http).UploadAsync("big.bin", new MemoryStream(new byte[size]), size, null, default);
        var puts = http.Requests.Where(r => r.Method == HttpMethod.Put).ToList();
        puts.Should().HaveCount((int)((size + S3Storage.PartSizeFor(size) - 1) / S3Storage.PartSizeFor(size)));
        puts.Sum(p => (long)p.Body.Length).Should().Be(size);
        var complete = Encoding.UTF8.GetString(http.Requests.Last().Body);
        complete.Should().Contain("<PartNumber>1</PartNumber><ETag>\"e1\"</ETag>");
        item.HashKind.Should().Be(S3Storage.HashKindFor(size));
        item.Hash.Should().Be("abc-5");
    }

    [Fact]
    public async Task A_failed_part_aborts_the_multipart_upload()
    {
        var size = S3Storage.SinglePutLimit + 5;
        var http = new FakeHttp(r =>
            r.Method == HttpMethod.Post ? FakeHttp.Ok("<InitiateMultipartUploadResult><UploadId>U1</UploadId></InitiateMultipartUploadResult>", "application/xml")
            : r.Method == HttpMethod.Put ? FakeHttp.Status(HttpStatusCode.InternalServerError, "<Error><Message>boom</Message></Error>")
            : FakeHttp.Ok());
        var act = () => Store(http).UploadAsync("big.bin", new MemoryStream(new byte[size]), size, null, default);
        (await act.Should().ThrowAsync<CloudException>()).Which.IsTransient.Should().BeTrue();
        http.Requests.Last().Method.Should().Be(HttpMethod.Delete);
        http.Requests.Last().Uri.Query.Should().Be("?uploadId=U1");
    }

    [Fact]
    public async Task List_follows_continuation_tokens()
    {
        var pages = new Queue<string>([
            """<ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/"><IsTruncated>true</IsTruncated><NextContinuationToken>T2</NextContinuationToken><Contents><Key>B/a.txt</Key><Size>3</Size><LastModified>2026-11-01T10:00:00.000Z</LastModified><ETag>"x"</ETag></Contents><Contents><Key>B/dir/</Key><Size>0</Size><LastModified>2026-11-01T10:00:00.000Z</LastModified></Contents></ListBucketResult>""",
            """<ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/"><IsTruncated>false</IsTruncated><Contents><Key>B/dir/b.txt</Key><Size>5</Size><LastModified>2026-11-02T10:00:00.000Z</LastModified><ETag>"y"</ETag></Contents></ListBucketResult>""",
        ]);
        var http = new FakeHttp(_ => FakeHttp.Ok(pages.Dequeue(), "application/xml"));
        var items = await Store(http).ListAsync("B", default);
        items.Select(i => (i.Path, i.Size, i.Hash)).Should().Equal(("B/a.txt", 3L, "x"), ("B/dir/b.txt", 5L, "y"));
        http.Requests[1].Uri.Query.Should().Contain("continuation-token=T2");
        http.Requests[0].Uri.Query.Should().Contain("prefix=B%2F");
    }

    [Fact]
    public async Task Errors_explain_access_problems_without_secrets()
    {
        var http = new FakeHttp(_ => FakeHttp.Status(HttpStatusCode.Forbidden, "<Error><Code>SignatureDoesNotMatch</Code><Message>The signature does not match.</Message></Error>"));
        var ex = await FluentActions.Awaiting(() => Store(http).TestAsync(default)).Should().ThrowAsync<CloudException>();
        ex.Which.Message.Should().Contain("Access denied").And.NotContain("SECRET");
        ex.Which.IsTransient.Should().BeFalse();
    }

    [Fact]
    public async Task Move_copies_then_deletes()
    {
        var http = new FakeHttp(r => r.Method == HttpMethod.Head
            ? FakeHttp.Ok("", configure: x => { x.Content.Headers.ContentLength = 3; x.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"e\""); })
            : FakeHttp.Ok("<CopyObjectResult><ETag>\"e\"</ETag></CopyObjectResult>", "application/xml"));
        await Store(http).MoveAsync("B/a b.txt", "B/.miu-versions/x/a b.txt", default);
        var put = http.Requests.Single(r => r.Method == HttpMethod.Put);
        put.Headers["x-amz-copy-source"].Should().Be("/my-bucket/B/a%20b.txt");
        http.Requests.Last().Method.Should().Be(HttpMethod.Delete);
    }
}
