using System.Text.Json;

namespace Orch8.Sdk;

/// <summary>
/// Typed REST client for the Orch8 engine (<c>/api/v1</c>). Thread-safe; create one per
/// engine and reuse it. Access resources through <see cref="Sequences"/>, <see cref="Instances"/>
/// and <see cref="Jobs"/>.
/// </summary>
public sealed class Orch8Client : IDisposable
{
    /// <summary>Creates a client from options.</summary>
    public Orch8Client(Orch8ClientOptions options)
    {
        Transport = new Orch8Transport(options);
        Sequences = new SequencesApi(Transport);
        Instances = new InstancesApi(Transport);
        Jobs = new JobsApi(Transport);
    }

    /// <summary>Creates a client for <paramref name="baseUrl"/> (including <c>/api/v1</c>).</summary>
    public Orch8Client(string baseUrl, string? apiKey = null, string? tenantId = null)
        : this(new Orch8ClientOptions { BaseUrl = baseUrl, ApiKey = apiKey, TenantId = tenantId }) { }

    internal Orch8Transport Transport { get; }

    /// <summary>The options this client was created with.</summary>
    public Orch8ClientOptions Options => Transport.Options;

    /// <summary>Sequence (workflow definition) operations.</summary>
    public SequencesApi Sequences { get; }

    /// <summary>Instance operations.</summary>
    public InstancesApi Instances { get; }

    /// <summary>Background job operations.</summary>
    public JobsApi Jobs { get; }

    /// <summary>
    /// Sends an arbitrary request relative to the base URL and returns the decoded body
    /// (<c>default</c> for an empty body). GET/HEAD are retried per the transport policy.
    /// </summary>
    public Task<T?> SendAsync<T>(HttpMethod method, string path, object? body = null, CancellationToken cancellationToken = default)
        => Transport.SendAsync<T>(method, path, body, cancellationToken);

    /// <inheritdoc />
    public void Dispose() => Transport.Dispose();

    internal static List<T> ReadList<T>(string? text, params string[] wrapperKeys)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<T>();
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in wrapperKeys)
            {
                if (root.TryGetProperty(key, out var inner) && inner.ValueKind == JsonValueKind.Array)
                {
                    root = inner;
                    break;
                }
            }
        }
        if (root.ValueKind != JsonValueKind.Array) throw new Orch8Exception("expected a JSON array in list response");
        return root.Deserialize<List<T>>(Orch8Json.Options) ?? new List<T>();
    }
}

/// <summary><c>/sequences</c> operations.</summary>
public sealed class SequencesApi
{
    private readonly Orch8Transport _t;
    internal SequencesApi(Orch8Transport t) => _t = t;

    /// <summary><c>POST /sequences</c>.</summary>
    public async Task<CreateSequenceResponse> CreateAsync(SequenceDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return await _t.SendAsync<CreateSequenceResponse>(HttpMethod.Post, "/sequences", definition, cancellationToken).ConfigureAwait(false)
            ?? throw new Orch8Exception("empty response from POST /sequences");
    }

    /// <summary><c>GET /sequences/{id}</c>.</summary>
    public async Task<SequenceDefinition> GetAsync(string id, CancellationToken cancellationToken = default)
        => await _t.SendAsync<SequenceDefinition>(HttpMethod.Get, $"/sequences/{Orch8Transport.Segment(id)}", null, cancellationToken).ConfigureAwait(false)
           ?? throw new Orch8Exception("empty response from GET /sequences/{id}");

    /// <summary><c>GET /sequences?tenant_id&amp;namespace&amp;limit&amp;offset</c>.</summary>
    public async Task<List<SequenceDefinition>> ListAsync(SequenceListQuery? query = null, CancellationToken cancellationToken = default)
    {
        var text = await _t.SendRawAsync(HttpMethod.Get, "/sequences" + (query?.ToQueryString() ?? ""), null, cancellationToken).ConfigureAwait(false);
        return Orch8Client.ReadList<SequenceDefinition>(text, "items", "sequences");
    }
}

/// <summary><c>/instances</c> operations.</summary>
public sealed class InstancesApi
{
    private readonly Orch8Transport _t;
    internal InstancesApi(Orch8Transport t) => _t = t;

    /// <summary><c>POST /instances</c>. Returns <c>deduplicated: true</c> when an idempotency key matched.</summary>
    public async Task<CreateInstanceResponse> CreateAsync(CreateInstanceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await _t.SendAsync<CreateInstanceResponse>(HttpMethod.Post, "/instances", request, cancellationToken).ConfigureAwait(false)
            ?? throw new Orch8Exception("empty response from POST /instances");
    }

    /// <summary><c>GET /instances/{id}</c>.</summary>
    public async Task<TaskInstance> GetAsync(string id, CancellationToken cancellationToken = default)
        => await _t.SendAsync<TaskInstance>(HttpMethod.Get, $"/instances/{Orch8Transport.Segment(id)}", null, cancellationToken).ConfigureAwait(false)
           ?? throw new Orch8Exception("empty response from GET /instances/{id}");

    /// <summary><c>GET /instances?tenant_id&amp;namespace&amp;sequence_id&amp;state&amp;limit&amp;offset</c>.</summary>
    public async Task<List<TaskInstance>> ListAsync(InstanceListQuery? query = null, CancellationToken cancellationToken = default)
    {
        var text = await _t.SendRawAsync(HttpMethod.Get, "/instances" + (query?.ToQueryString() ?? ""), null, cancellationToken).ConfigureAwait(false);
        return Orch8Client.ReadList<TaskInstance>(text, "items", "instances");
    }

    /// <summary><c>POST /instances/{id}/signals</c>.</summary>
    public async Task<SendSignalResponse> SignalAsync(string id, SignalType signalType, object? payload = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signalType);
        var body = new SendSignalRequest { SignalType = signalType, Payload = Orch8Json.ToNode(payload) };
        return await _t.SendAsync<SendSignalResponse>(HttpMethod.Post, $"/instances/{Orch8Transport.Segment(id)}/signals", body, cancellationToken).ConfigureAwait(false)
               ?? new SendSignalResponse();
    }

    /// <summary>Cancels an instance (signal <c>"cancel"</c>).</summary>
    public Task<SendSignalResponse> CancelAsync(string id, CancellationToken cancellationToken = default)
        => SignalAsync(id, SignalType.Cancel, null, cancellationToken);
}

/// <summary><c>/jobs</c> operations.</summary>
public sealed class JobsApi
{
    private readonly Orch8Transport _t;
    internal JobsApi(Orch8Transport t) => _t = t;

    /// <summary><c>POST /jobs</c>.</summary>
    public async Task<Job> EnqueueAsync(EnqueueJobRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.Handler)) throw new ArgumentException("Handler is required", nameof(request));
        return await _t.SendAsync<Job>(HttpMethod.Post, "/jobs", request, cancellationToken).ConfigureAwait(false)
            ?? throw new Orch8Exception("empty response from POST /jobs");
    }

    /// <summary>Convenience overload: enqueue <paramref name="handler"/> with <paramref name="payload"/>.</summary>
    public Task<Job> EnqueueAsync(string handler, object? payload, CancellationToken cancellationToken = default)
        => EnqueueAsync(new EnqueueJobRequest { Handler = handler, Payload = Orch8Json.ToNode(payload) ?? new System.Text.Json.Nodes.JsonObject() }, cancellationToken);

    /// <summary><c>GET /jobs/{id}</c>.</summary>
    public async Task<Job> GetAsync(string id, CancellationToken cancellationToken = default)
        => await _t.SendAsync<Job>(HttpMethod.Get, $"/jobs/{Orch8Transport.Segment(id)}", null, cancellationToken).ConfigureAwait(false)
           ?? throw new Orch8Exception("empty response from GET /jobs/{id}");

    /// <summary><c>GET /jobs?...</c>. Accepts a bare array or <c>{"items": [...]}</c> / <c>{"jobs": [...]}</c>.</summary>
    public async Task<List<Job>> ListAsync(JobListQuery? query = null, CancellationToken cancellationToken = default)
    {
        var text = await _t.SendRawAsync(HttpMethod.Get, "/jobs" + (query?.ToQueryString() ?? ""), null, cancellationToken).ConfigureAwait(false);
        return Orch8Client.ReadList<Job>(text, "items", "jobs");
    }

    /// <summary><c>DELETE /jobs/{id}</c>. Returns the cancelled job, or null when the server sent no body.</summary>
    public Task<Job?> CancelAsync(string id, CancellationToken cancellationToken = default)
        => _t.SendAsync<Job>(HttpMethod.Delete, $"/jobs/{Orch8Transport.Segment(id)}", null, cancellationToken);
}
