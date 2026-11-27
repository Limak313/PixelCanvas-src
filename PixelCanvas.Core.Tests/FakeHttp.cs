using System.Net;
using System.Net.Http;

namespace PixelCanvas.Tests;

/// <summary>按 URL 路由的假 HTTP 处理器；记录请求正文，便于断言上报/下载内容。</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    internal List<RequestRecord> Requests { get; } = new();

    internal FakeHttpHandler Map(string url, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _routes[url] = respond;
        return this;
    }

    internal FakeHttpHandler MapBytes(string url, byte[] bytes) =>
        Map(url, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });

    internal FakeHttpHandler MapText(string url, string text) =>
        Map(url, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RequestRecord(request.Method.Method, request.RequestUri?.ToString() ?? "", body,
            request.Headers.Authorization?.ToString()));
        if (_routes.TryGetValue(request.RequestUri?.ToString() ?? "", out var respond)) return respond(request);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

internal sealed record RequestRecord(string Method, string Url, string Body, string? Authorization);
