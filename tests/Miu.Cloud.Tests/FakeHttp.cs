using System.Net;

namespace Miu.Cloud.Tests;

public sealed record SeenRequest(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, byte[] Body);

/// <summary>An HTTP server in a delegate: records every request and answers from the handler.</summary>
public sealed class FakeHttp(Func<SeenRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<SeenRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        if (request.Content is not null)
            foreach (var h in request.Content.Headers) headers[h.Key.ToLowerInvariant()] = string.Join(",", h.Value);
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
        var seen = new SeenRequest(request.Method, request.RequestUri!, headers, body);
        Requests.Add(seen);
        return respond(seen);
    }

    public static HttpResponseMessage Ok(string body = "", string contentType = "application/json", Action<HttpResponseMessage>? configure = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, contentType) };
        configure?.Invoke(r);
        return r;
    }

    public static HttpResponseMessage Status(HttpStatusCode code, string body = "") => new(code) { Content = new StringContent(body) };
}
