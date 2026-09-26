using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Orch8.Sdk.Tests;

/// <summary>A recorded request.</summary>
public sealed record Recorded(string Method, string RawPath, string PathAndQuery, string? Body, IReadOnlyDictionary<string, string> Headers, DateTime At)
{
    public JsonNode? Json => Body is null or "" ? null : JsonNode.Parse(Body);
}

/// <summary>An in-memory <see cref="HttpMessageHandler"/> that routes requests to a delegate and records them.</summary>
public sealed class FakeHttp : HttpMessageHandler
{
    private readonly Func<Recorded, Task<HttpResponseMessage>> _route;
    public ConcurrentQueue<Recorded> Requests { get; } = new();

    public FakeHttp(Func<Recorded, Task<HttpResponseMessage>> route) => _route = route;

    public FakeHttp(Func<Recorded, HttpResponseMessage> route) : this(r => Task.FromResult(route(r))) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers) headers[h.Key] = string.Join(",", h.Value);
        if (request.Content is not null)
            foreach (var h in request.Content.Headers) headers[h.Key] = string.Join(",", h.Value);
        var uri = request.RequestUri!;
        var rec = new Recorded(request.Method.Method, uri.AbsolutePath, uri.PathAndQuery, body, headers, DateTime.UtcNow);
        Requests.Enqueue(rec);
        cancellationToken.ThrowIfCancellationRequested();
        var res = await _route(rec);
        res.RequestMessage = request;
        return res;
    }

    public List<Recorded> Find(string method, string pathSuffix)
        => Requests.Where(r => r.Method == method && r.RawPath.EndsWith(pathSuffix, StringComparison.Ordinal)).ToList();

    public static HttpResponseMessage Json(int status, string json)
        => new((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Json(int status, JsonNode node) => Json(status, node.ToJsonString());

    public static HttpResponseMessage Empty(int status) => new((HttpStatusCode)status);

    public static HttpResponseMessage Error(int status, string code, string message)
        => Json(status, $"{{\"error\":{{\"code\":\"{code}\",\"message\":\"{message}\",\"request_id\":null}}}}");
}
