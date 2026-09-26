namespace Orch8.Sdk;

/// <summary>Connection settings shared by <see cref="Orch8Client"/> and <see cref="Orch8Worker"/>.</summary>
public sealed class Orch8ClientOptions
{
    /// <summary>
    /// Engine base URL <b>including</b> the <c>/api/v1</c> prefix, e.g. <c>http://localhost:8080/api/v1</c>.
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:8080/api/v1";

    /// <summary>Sent as <c>x-api-key</c> when set.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Sent as <c>x-tenant-id</c> when set. SDKs should always send the configured tenant.</summary>
    public string? TenantId { get; set; }

    /// <summary>Maximum attempts for safe (GET/HEAD) requests. Unsafe methods are never replayed. Default 3.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Base delay of the exponential backoff between safe retries (<c>base * 2^(n-1)</c>). Default 250 ms.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Upper bound for a single retry delay (also caps <c>Retry-After</c>). Default 10 s.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Per-attempt request timeout. Default 30 s.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Optional pre-configured <see cref="HttpClient"/> (e.g. from <c>IHttpClientFactory</c>). The SDK
    /// never disposes an injected client and ignores its <c>BaseAddress</c>.
    /// </summary>
    public HttpClient? HttpClient { get; set; }

    /// <summary>
    /// Optional <see cref="HttpMessageHandler"/> used to build the SDK-owned <see cref="HttpClient"/>
    /// when <see cref="HttpClient"/> is not set (useful for testing and proxies).
    /// </summary>
    public HttpMessageHandler? HttpMessageHandler { get; set; }

    /// <summary>Extra headers added to every request (e.g. <c>x-request-id</c>).</summary>
    public IDictionary<string, string> DefaultHeaders { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Builds options from the standard environment variables: <c>ORCH8_BASE_URL</c>, <c>ORCH8_API_KEY</c>,
    /// <c>ORCH8_TENANT_ID</c>, <c>ORCH8_MAX_ATTEMPTS</c>, <c>ORCH8_RETRY_BASE_DELAY_MS</c>.
    /// </summary>
    public static Orch8ClientOptions FromEnvironment()
    {
        var o = new Orch8ClientOptions();
        var baseUrl = Environment.GetEnvironmentVariable("ORCH8_BASE_URL");
        if (!string.IsNullOrEmpty(baseUrl)) o.BaseUrl = baseUrl;
        o.ApiKey = NullIfEmpty(Environment.GetEnvironmentVariable("ORCH8_API_KEY"));
        o.TenantId = NullIfEmpty(Environment.GetEnvironmentVariable("ORCH8_TENANT_ID"));
        if (int.TryParse(Environment.GetEnvironmentVariable("ORCH8_MAX_ATTEMPTS"), out var attempts)) o.MaxAttempts = attempts;
        if (int.TryParse(Environment.GetEnvironmentVariable("ORCH8_RETRY_BASE_DELAY_MS"), out var delay)) o.RetryBaseDelay = TimeSpan.FromMilliseconds(delay);
        return o;
    }

    internal static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
