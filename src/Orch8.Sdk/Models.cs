using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Orch8.Sdk;

/// <summary>
/// Base class for API models: keeps any response field the SDK does not model in
/// <see cref="ExtensionData"/> so nothing the engine adds is lost (and it round-trips on write).
/// </summary>
public abstract class Orch8Model
{
    /// <summary>Fields not modelled by this SDK version.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

// ---------------------------------------------------------------------------
// Sequences
// ---------------------------------------------------------------------------

/// <summary>
/// A sequence (workflow) definition. On create, <see cref="Id"/>, <see cref="Version"/> and
/// <see cref="CreatedAt"/> may be left unset; blocks are raw JSON (see the engine's
/// <c>BlockDefinition</c> schema).
/// </summary>
public sealed class SequenceDefinition : Orch8Model
{
    /// <summary>Sequence id.</summary>
    public string? Id { get; set; }
    /// <summary>Owning tenant.</summary>
    public string? TenantId { get; set; }
    /// <summary>Namespace.</summary>
    public string? Namespace { get; set; }
    /// <summary>Name.</summary>
    public string? Name { get; set; }
    /// <summary>Version number.</summary>
    public int? Version { get; set; }
    /// <summary>Whether this version is deprecated.</summary>
    public bool? Deprecated { get; set; }
    /// <summary>Block definitions (raw JSON objects).</summary>
    public List<JsonNode?>? Blocks { get; set; }
    /// <summary>Optional JSON Schema for <c>context.data</c>.</summary>
    public JsonNode? InputSchema { get; set; }
    /// <summary>Creation time.</summary>
    public DateTimeOffset? CreatedAt { get; set; }
}

/// <summary>Response of <c>POST /sequences</c>.</summary>
public sealed class CreateSequenceResponse : Orch8Model
{
    /// <summary>Id of the created sequence.</summary>
    public string Id { get; set; } = "";
    /// <summary>Non-fatal validation warnings, when any.</summary>
    public List<JsonNode?>? Warnings { get; set; }
}

/// <summary>Filters for <c>GET /sequences</c>.</summary>
public sealed class SequenceListQuery : ListQueryBase
{
    /// <summary>Tenant filter.</summary>
    public string? TenantId { get; set; }
    /// <summary>Namespace filter.</summary>
    public string? Namespace { get; set; }

    internal override IEnumerable<KeyValuePair<string, string?>> Pairs()
    {
        yield return new("tenant_id", TenantId);
        yield return new("namespace", Namespace);
    }
}

// ---------------------------------------------------------------------------
// Instances
// ---------------------------------------------------------------------------

/// <summary>Body of <c>POST /instances</c>.</summary>
public sealed class CreateInstanceRequest : Orch8Model
{
    /// <summary>Sequence to run.</summary>
    public string SequenceId { get; set; } = "";
    /// <summary>Tenant.</summary>
    public string TenantId { get; set; } = "";
    /// <summary>Namespace.</summary>
    public string Namespace { get; set; } = "default";
    /// <summary>Initial execution context (<c>{"data": {...}, "config": {...}}</c>).</summary>
    public JsonNode? Context { get; set; }
    /// <summary>Priority (<c>Low | Normal | High | Critical</c>).</summary>
    public string? Priority { get; set; }
    /// <summary>IANA timezone.</summary>
    public string? Timezone { get; set; }
    /// <summary>Arbitrary metadata.</summary>
    public JsonNode? Metadata { get; set; }
    /// <summary>Deduplication key; a replay returns the existing id with <c>deduplicated: true</c>.</summary>
    public string? IdempotencyKey { get; set; }
    /// <summary>Concurrency key.</summary>
    public string? ConcurrencyKey { get; set; }
    /// <summary>Max concurrent instances sharing <see cref="ConcurrencyKey"/>.</summary>
    public int? MaxConcurrency { get; set; }
    /// <summary>Delay the first run until this time.</summary>
    public DateTimeOffset? NextFireAt { get; set; }
    /// <summary>Run in dry-run mode.</summary>
    public bool? DryRun { get; set; }
}

/// <summary>Response of <c>POST /instances</c>.</summary>
public sealed class CreateInstanceResponse : Orch8Model
{
    /// <summary>Instance id (the existing one on an idempotent replay).</summary>
    public string Id { get; set; } = "";
    /// <summary>True when an existing instance was returned for the idempotency key.</summary>
    public bool? Deduplicated { get; set; }
}

/// <summary>A workflow instance (<c>TaskInstance</c>).</summary>
public sealed class TaskInstance : Orch8Model
{
    /// <summary>Instance id.</summary>
    public string Id { get; set; } = "";
    /// <summary>Sequence id.</summary>
    public string? SequenceId { get; set; }
    /// <summary>Tenant.</summary>
    public string? TenantId { get; set; }
    /// <summary>Namespace.</summary>
    public string? Namespace { get; set; }
    /// <summary>State: scheduled, running, waiting, paused, completed, failed, cancelled.</summary>
    public string? State { get; set; }
    /// <summary>Priority.</summary>
    public string? Priority { get; set; }
    /// <summary>Timezone.</summary>
    public string? Timezone { get; set; }
    /// <summary>Metadata.</summary>
    public JsonNode? Metadata { get; set; }
    /// <summary>Execution context.</summary>
    public JsonNode? Context { get; set; }
    /// <summary>Idempotency key.</summary>
    public string? IdempotencyKey { get; set; }
    /// <summary>Parent instance id for sub-sequences.</summary>
    public string? ParentInstanceId { get; set; }
    /// <summary>Next scheduled fire time.</summary>
    public DateTimeOffset? NextFireAt { get; set; }
    /// <summary>Creation time.</summary>
    public DateTimeOffset? CreatedAt { get; set; }
    /// <summary>Last update time.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>Filters for <c>GET /instances</c>.</summary>
public sealed class InstanceListQuery : ListQueryBase
{
    /// <summary>Tenant filter.</summary>
    public string? TenantId { get; set; }
    /// <summary>Namespace filter.</summary>
    public string? Namespace { get; set; }
    /// <summary>Sequence filter.</summary>
    public string? SequenceId { get; set; }
    /// <summary>State filter.</summary>
    public string? State { get; set; }

    internal override IEnumerable<KeyValuePair<string, string?>> Pairs()
    {
        yield return new("tenant_id", TenantId);
        yield return new("namespace", Namespace);
        yield return new("sequence_id", SequenceId);
        yield return new("state", State);
    }
}

/// <summary>Body of <c>POST /instances/{id}/signals</c>.</summary>
public sealed class SendSignalRequest
{
    /// <summary>Signal type.</summary>
    public SignalType SignalType { get; set; } = SignalType.Cancel;
    /// <summary>Optional payload.</summary>
    public JsonNode? Payload { get; set; }
}

/// <summary>Response of <c>POST /instances/{id}/signals</c>.</summary>
public sealed class SendSignalResponse : Orch8Model
{
    /// <summary>Id of the enqueued signal.</summary>
    public string? SignalId { get; set; }
}

/// <summary>
/// A signal type: one of the built-ins (<c>pause</c>, <c>resume</c>, <c>cancel</c>, <c>update_context</c>)
/// serialized as a string, or a custom signal serialized as <c>{"custom": "&lt;name&gt;"}</c>.
/// </summary>
[JsonConverter(typeof(SignalTypeConverter))]
public sealed class SignalType : IEquatable<SignalType>
{
    private SignalType(string? builtIn, string? custom)
    {
        BuiltIn = builtIn;
        CustomName = custom;
    }

    /// <summary>Built-in name, or null for a custom signal.</summary>
    public string? BuiltIn { get; }
    /// <summary>Custom signal name, or null for a built-in.</summary>
    public string? CustomName { get; }

    /// <summary><c>"pause"</c>.</summary>
    public static SignalType Pause { get; } = new("pause", null);
    /// <summary><c>"resume"</c>.</summary>
    public static SignalType Resume { get; } = new("resume", null);
    /// <summary><c>"cancel"</c>.</summary>
    public static SignalType Cancel { get; } = new("cancel", null);
    /// <summary><c>"update_context"</c>.</summary>
    public static SignalType UpdateContext { get; } = new("update_context", null);

    /// <summary>A custom signal <c>{"custom": name}</c>.</summary>
    public static SignalType Custom(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new(null, name);
    }

    /// <summary>Parses a built-in signal name.</summary>
    public static SignalType FromString(string value) => value switch
    {
        "pause" => Pause,
        "resume" => Resume,
        "cancel" => Cancel,
        "update_context" => UpdateContext,
        _ => new SignalType(value, null),
    };

    /// <inheritdoc />
    public bool Equals(SignalType? other) => other is not null && other.BuiltIn == BuiltIn && other.CustomName == CustomName;
    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as SignalType);
    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(BuiltIn, CustomName);
    /// <inheritdoc />
    public override string ToString() => BuiltIn ?? $"custom:{CustomName}";
}

internal sealed class SignalTypeConverter : JsonConverter<SignalType>
{
    public override SignalType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return SignalType.FromString(reader.GetString()!);
        using var doc = JsonDocument.ParseValue(ref reader);
        if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("custom", out var c) && c.ValueKind == JsonValueKind.String)
            return SignalType.Custom(c.GetString()!);
        throw new JsonException("signal_type must be a string or {\"custom\": name}");
    }

    public override void Write(Utf8JsonWriter writer, SignalType value, JsonSerializerOptions options)
    {
        if (value.CustomName is not null)
        {
            writer.WriteStartObject();
            writer.WriteString("custom", value.CustomName);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteStringValue(value.BuiltIn);
        }
    }
}

// ---------------------------------------------------------------------------
// Jobs
// ---------------------------------------------------------------------------

/// <summary>Retry policy for a job.</summary>
public sealed class JobRetryPolicy
{
    /// <summary>Total attempts including the first.</summary>
    public int MaxAttempts { get; set; }
    /// <summary>Initial backoff in milliseconds.</summary>
    public long InitialBackoffMs { get; set; }
    /// <summary>Optional backoff cap in milliseconds.</summary>
    public long? MaxBackoffMs { get; set; }
}

/// <summary>Body of <c>POST /jobs</c>. Unset optional fields are omitted from the request.</summary>
public sealed class EnqueueJobRequest : Orch8Model
{
    /// <summary>Handler name.</summary>
    public string Handler { get; set; } = "";
    /// <summary>Job payload (defaults to <c>{}</c>).</summary>
    public JsonNode? Payload { get; set; } = new JsonObject();
    /// <summary>Named queue.</summary>
    public string? Queue { get; set; }
    /// <summary>Priority.</summary>
    public int? Priority { get; set; }
    /// <summary>Retry policy.</summary>
    public JobRetryPolicy? Retry { get; set; }
    /// <summary>Delay before the job becomes runnable, in milliseconds.</summary>
    public long? DelayMs { get; set; }
    /// <summary>Absolute run time.</summary>
    public DateTimeOffset? RunAt { get; set; }
    /// <summary>Deduplication key.</summary>
    public string? IdempotencyKey { get; set; }
    /// <summary>Arbitrary metadata.</summary>
    public JsonNode? Metadata { get; set; }
}

/// <summary>A background job.</summary>
public sealed class Job : Orch8Model
{
    /// <summary>Job id.</summary>
    public string Id { get; set; } = "";
    /// <summary>Backing instance id.</summary>
    public string? InstanceId { get; set; }
    /// <summary>Handler name.</summary>
    public string? Handler { get; set; }
    /// <summary>Status (e.g. <c>scheduled</c>, <c>running</c>, <c>completed</c>, <c>failed</c>, <c>cancelled</c>).</summary>
    public string? Status { get; set; }
    /// <summary>Creation time.</summary>
    public DateTimeOffset? CreatedAt { get; set; }
    /// <summary>Scheduled run time.</summary>
    public DateTimeOffset? RunAt { get; set; }
}

/// <summary>Filters for <c>GET /jobs</c>.</summary>
public sealed class JobListQuery : ListQueryBase
{
    /// <summary>Status filter.</summary>
    public string? Status { get; set; }
    /// <summary>Handler filter.</summary>
    public string? Handler { get; set; }
    /// <summary>Queue filter.</summary>
    public string? Queue { get; set; }

    internal override IEnumerable<KeyValuePair<string, string?>> Pairs()
    {
        yield return new("status", Status);
        yield return new("handler", Handler);
        yield return new("queue", Queue);
    }
}

/// <summary>Common pagination plus free-form extra query parameters.</summary>
public abstract class ListQueryBase
{
    /// <summary>Page size.</summary>
    public int? Limit { get; set; }
    /// <summary>Offset.</summary>
    public int? Offset { get; set; }

    /// <summary>Additional query parameters not modelled explicitly (unknown keys also land here when deserialized).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    internal abstract IEnumerable<KeyValuePair<string, string?>> Pairs();

    internal string ToQueryString()
    {
        IEnumerable<KeyValuePair<string, string?>> all = Pairs()
            .Append(new("limit", Limit.HasValue ? Orch8Transport.FormatInvariant(Limit.Value) : null))
            .Append(new("offset", Offset.HasValue ? Orch8Transport.FormatInvariant(Offset.Value) : null));
        if (Extra is not null)
            all = all.Concat(Extra.Select(kv => new KeyValuePair<string, string?>(kv.Key, ScalarToString(kv.Value))));
        return Orch8Transport.Query(all);
    }

    private static string? ScalarToString(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => e.GetRawText(),
    };
}

// ---------------------------------------------------------------------------
// Worker protocol
// ---------------------------------------------------------------------------

/// <summary>A task claimed by a worker (<c>WorkerTask</c>).</summary>
public sealed class WorkerTask : Orch8Model
{
    /// <summary>Task id, used in every mutation path.</summary>
    public string Id { get; set; } = "";
    /// <summary>Owning instance.</summary>
    public string InstanceId { get; set; } = "";
    /// <summary>Step id inside the sequence.</summary>
    public string BlockId { get; set; } = "";
    /// <summary>Handler the step names.</summary>
    public string HandlerName { get; set; } = "";
    /// <summary>Queue name (null for the default queue).</summary>
    public string? QueueName { get; set; }
    /// <summary>Step parameters.</summary>
    public JsonNode? Params { get; set; }
    /// <summary>Serialized execution context.</summary>
    public JsonNode? Context { get; set; }
    /// <summary>0 on first dispatch.</summary>
    public int Attempt { get; set; }
    /// <summary>Deadline in ms measured from <see cref="CreatedAt"/>.</summary>
    public long? TimeoutMs { get; set; }
    /// <summary>Task state (<c>claimed</c> in a poll response).</summary>
    public string? State { get; set; }
    /// <summary>Echo of the claiming worker.</summary>
    public string? WorkerId { get; set; }
    /// <summary>Ownership generation; echoed on every mutation.</summary>
    public long ClaimEpoch { get; set; }
    /// <summary>Last durable checkpoint, when any.</summary>
    public JsonNode? ResumeCheckpoint { get; set; }
    /// <summary>CAS version of the checkpoint.</summary>
    public long? CheckpointSeq { get; set; }
    /// <summary>Creation time.</summary>
    public DateTimeOffset? CreatedAt { get; set; }
    /// <summary>Claim time.</summary>
    public DateTimeOffset? ClaimedAt { get; set; }
}

/// <summary>Response of the poll endpoints.</summary>
public sealed class PollResponse : Orch8Model
{
    /// <summary>Claimed tasks.</summary>
    public List<WorkerTask> Tasks { get; set; } = new();
    /// <summary>Server lease (stale threshold) in seconds.</summary>
    public int? LeaseSecs { get; set; }
    /// <summary>Server heartbeat hint in seconds.</summary>
    public int? HeartbeatIntervalSecs { get; set; }
    /// <summary>Minimum wait before the next poll of this handler after an empty poll.</summary>
    public long? PollAfterMs { get; set; }
}

/// <summary>Push-dispatch envelope POSTed by the engine to a push queue's URL (§8.1).</summary>
public sealed class PushEnvelope : Orch8Model
{
    /// <summary>Durable pending task id (not claimed by the push).</summary>
    public string? TaskId { get; set; }
    /// <summary>Instance id.</summary>
    public string? InstanceId { get; set; }
    /// <summary>Block id.</summary>
    public string? BlockId { get; set; }
    /// <summary>Handler name to claim for.</summary>
    public string HandlerName { get; set; } = "";
    /// <summary>Queue name to claim from.</summary>
    public string? QueueName { get; set; }
    /// <summary>Step parameters.</summary>
    public JsonNode? Params { get; set; }
    /// <summary>Execution context.</summary>
    public JsonNode? Context { get; set; }
    /// <summary>Attempt.</summary>
    public int? Attempt { get; set; }
    /// <summary>Timeout.</summary>
    public long? TimeoutMs { get; set; }

    /// <summary>Parses the raw request body. Throws <see cref="JsonException"/> on malformed input.</summary>
    public static PushEnvelope Parse(ReadOnlySpan<byte> rawBody)
        => JsonSerializer.Deserialize<PushEnvelope>(rawBody, Orch8Json.Options) ?? throw new JsonException("empty push envelope");
}
