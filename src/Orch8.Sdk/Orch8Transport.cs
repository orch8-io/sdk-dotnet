using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Orch8.Sdk;

/// <summary>
/// Low-level HTTP transport: auth/tenant headers, JSON bodies, typed errors from the engine's
/// error envelope, and the safe-method retry policy of <c>fixtures/transport.json</c>
/// (GET/HEAD retried on 408/425/429/5xx and transport errors; unsafe methods never replayed).
/// </summary>
internal sealed class Orch8Transport : IDisposable
{
    private static readonly int[] RetryableStatuses = { 408, 425, 429, 500, 502, 503, 504 };
    private static readonly MediaTypeHeaderValue JsonContentType = new("application/json") { CharSet = "utf-8" };

    private readonly Orch8ClientOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _baseUrl;

    public Orch8Transport(Orch8ClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.BaseUrl)) throw new ArgumentException("BaseUrl is required", nameof(options));
        _options = options;
        _baseUrl = options.BaseUrl.TrimEnd('/');
        if (options.HttpClient is not null)
        {
            _http = options.HttpClient;
            _ownsHttp = false;
        }
        else
        {
            _http = options.HttpMessageHandler is not null
                ? new HttpClient(options.HttpMessageHandler, disposeHandler: false)
                : new HttpClient();
            _http.Timeout = System.Threading.Timeout.InfiniteTimeSpan; // per-attempt timeout is applied below
            _ownsHttp = true;
        }
    }

    public Orch8ClientOptions Options => _options;

    public static bool IsRetryableStatus(int status) => Array.IndexOf(RetryableStatuses, status) >= 0;

    /// <summary>True for errors a caller may retry: transport failures and 408/425/429/5xx.</summary>
    public static bool IsRetryable(Exception e) => e is Orch8TransportException || (e is Orch8ApiException api && api.IsRetryable);

    public static bool IsSafe(HttpMethod method) => method == HttpMethod.Get || method == HttpMethod.Head;

    /// <summary>Percent-encodes <paramref name="id"/> as exactly one path segment (<c>a/b c</c> → <c>a%2Fb%20c</c>).</summary>
    public static string Segment(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (id.Length == 0) throw new ArgumentException("id must not be empty", nameof(id));
        return Uri.EscapeDataString(id);
    }

    /// <summary>Builds <c>?k=v&amp;...</c> from non-null pairs (empty string when none).</summary>
    public static string Query(IEnumerable<KeyValuePair<string, string?>> pairs)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in pairs)
        {
            if (v is null) continue;
            sb.Append(sb.Length == 0 ? '?' : '&').Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(v));
        }
        return sb.ToString();
    }

    /// <summary>Sends a request and deserializes the response body (default when the body is empty).</summary>
    public async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var text = await SendRawAsync(method, path, body, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(text, Orch8Json.Options);
        }
        catch (JsonException e)
        {
            throw new Orch8Exception($"could not decode {method} {path} response: {e.Message}", e);
        }
    }

    /// <summary>
    /// Sends a request, applying the retry policy for safe methods. Returns the response body text,
    /// or <c>null</c> for an empty body (e.g. 204).
    /// </summary>
    public async Task<string?> SendRawAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        ValidatePath(path);
        var attempts = IsSafe(method) ? Math.Max(1, _options.MaxAttempts) : 1;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await SendOnceAsync(method, path, body, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (attempt < attempts && IsRetryable(e) && !cancellationToken.IsCancellationRequested)
            {
                var delay = Backoff(attempt);
                if (e is Orch8RateLimitedException { RetryAfter: { } ra } && ra > delay) delay = ra;
                if (delay > _options.MaxRetryDelay) delay = _options.MaxRetryDelay;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private TimeSpan Backoff(int attempt)
        => TimeSpan.FromMilliseconds(_options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));

    internal static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/' || path.StartsWith("//", StringComparison.Ordinal) || path.Contains('\\'))
            throw new Orch8InvalidPathException($"invalid_path: request path must be a single absolute path, got '{path}'");
    }

    private async Task<string?> SendOnceAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, _baseUrl + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (_options.ApiKey is not null) request.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey);
        if (_options.TenantId is not null) request.Headers.TryAddWithoutValidation("x-tenant-id", _options.TenantId);
        foreach (var (k, v) in _options.DefaultHeaders) request.Headers.TryAddWithoutValidation(k, v);
        if (body is not null)
        {
            var bytes = body as byte[] ?? JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), Orch8Json.Options);
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = JsonContentType;
            request.Content = content;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.Timeout > TimeSpan.Zero && _options.Timeout != System.Threading.Timeout.InfiniteTimeSpan)
            timeout.CancelAfter(_options.Timeout);

        HttpResponseMessage response;
        string text;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException e)
        {
            throw new Orch8TransportException($"{method} {path}: request timed out after {_options.Timeout.TotalMilliseconds} ms", e);
        }
        catch (HttpRequestException e)
        {
            throw new Orch8TransportException($"{method} {path}: {e.Message}", e);
        }
        catch (IOException e)
        {
            throw new Orch8TransportException($"{method} {path}: {e.Message}", e);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (status >= 400) throw CreateApiException(status, text, response);
            return string.IsNullOrEmpty(text) ? null : text;
        }
    }

    internal static Orch8ApiException CreateApiException(int status, string? text, HttpResponseMessage? response = null)
    {
        string? code = null, message = null, requestId = null;
        JsonElement? details = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("error", out var err))
                {
                    if (err.ValueKind == JsonValueKind.Object)
                    {
                        if (err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String) code = c.GetString();
                        if (err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString();
                        if (err.TryGetProperty("request_id", out var r) && r.ValueKind == JsonValueKind.String) requestId = r.GetString();
                        if (err.TryGetProperty("details", out var d) && d.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) details = d.Clone();
                    }
                    else if (err.ValueKind == JsonValueKind.String)
                    {
                        message = err.GetString();
                    }
                }
            }
            catch (JsonException)
            {
                // not JSON: fall through with the raw text as message
            }
            message ??= text.Length > 512 ? text[..512] : text;
        }
        if (requestId is null && response is not null && response.Headers.TryGetValues("x-request-id", out var ids))
            requestId = ids.FirstOrDefault();
        if (string.IsNullOrEmpty(message)) message = $"HTTP {status}";

        TimeSpan? retryAfter = null;
        if (response?.Headers.RetryAfter is { } ra)
        {
            if (ra.Delta is { } delta) retryAfter = delta;
            else if (ra.Date is { } date) retryAfter = date - DateTimeOffset.UtcNow;
            if (retryAfter < TimeSpan.Zero) retryAfter = TimeSpan.Zero;
        }

        return status switch
        {
            400 => new Orch8BadRequestException(status, code, message, requestId, details, text),
            401 => new Orch8UnauthorizedException(status, code, message, requestId, details, text),
            403 => new Orch8ForbiddenException(status, code, message, requestId, details, text),
            404 => new Orch8NotFoundException(status, code, message, requestId, details, text),
            409 => new Orch8ConflictException(status, code, message, requestId, details, text),
            413 => new Orch8PayloadTooLargeException(status, code, message, requestId, details, text),
            422 => new Orch8UnprocessableException(status, code, message, requestId, details, text),
            429 => new Orch8RateLimitedException(status, code, message, requestId, details, text, retryAfter),
            >= 500 and <= 599 => new Orch8ServerException(status, code, message, requestId, details, text),
            _ => new Orch8ApiException(status, code, message, requestId, details, text),
        };
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    internal static string FormatInvariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
