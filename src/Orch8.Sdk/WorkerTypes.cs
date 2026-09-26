using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Orch8.Sdk;

/// <summary>Worker settings. Connection settings live in <see cref="Orch8ClientOptions"/>.</summary>
public sealed class Orch8WorkerOptions
{
    /// <summary>Unique per process. Default <c>hostname-pid</c>.</summary>
    public string WorkerId { get; set; } = $"{Environment.MachineName}-{Environment.ProcessId}";

    /// <summary>Maximum tasks executing at once across all handlers. Default 10.</summary>
    public int Concurrency { get; set; } = 10;

    /// <summary>Base poll interval; also the base of the exponential backoff after poll errors. Default 1 s.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound of the poll-error backoff. Default 30 s.</summary>
    public TimeSpan MaxPollBackoff { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Heartbeat interval for in-flight tasks. Default 15 s; the effective interval is capped by the
    /// server's <c>heartbeat_interval_secs</c> hint and half of <c>lease_secs</c>.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Named queue. When set the worker polls <c>/workers/tasks/poll/queue</c>.</summary>
    public string? Queue { get; set; }

    /// <summary>Worker/app version sent on polls (used by version pins).</summary>
    public string? Version { get; set; }

    /// <summary>How long a graceful shutdown waits for in-flight tasks before abandoning them. Default 30 s.</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum attempts for a <c>complete</c>/<c>fail</c> acknowledgement on transient errors. Default 5.</summary>
    public int AckMaxAttempts { get; set; } = 5;

    /// <summary>Optional logger.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>
    /// Builds options from <c>ORCH8_WORKER_ID</c>, <c>ORCH8_CONCURRENCY</c>, <c>ORCH8_POLL_INTERVAL_MS</c>,
    /// <c>ORCH8_SHUTDOWN_TIMEOUT_MS</c>, <c>ORCH8_QUEUE</c>, <c>ORCH8_WORKER_VERSION</c>.
    /// </summary>
    public static Orch8WorkerOptions FromEnvironment()
    {
        var o = new Orch8WorkerOptions();
        var id = Environment.GetEnvironmentVariable("ORCH8_WORKER_ID");
        if (!string.IsNullOrEmpty(id)) o.WorkerId = id;
        if (int.TryParse(Environment.GetEnvironmentVariable("ORCH8_CONCURRENCY"), out var c)) o.Concurrency = c;
        if (int.TryParse(Environment.GetEnvironmentVariable("ORCH8_POLL_INTERVAL_MS"), out var p)) o.PollInterval = TimeSpan.FromMilliseconds(p);
        if (int.TryParse(Environment.GetEnvironmentVariable("ORCH8_SHUTDOWN_TIMEOUT_MS"), out var s)) o.ShutdownTimeout = TimeSpan.FromMilliseconds(s);
        o.Queue = Orch8ClientOptions.NullIfEmpty(Environment.GetEnvironmentVariable("ORCH8_QUEUE"));
        o.Version = Orch8ClientOptions.NullIfEmpty(Environment.GetEnvironmentVariable("ORCH8_WORKER_VERSION"));
        return o;
    }
}

/// <summary>
/// Throw from a handler to fail the task with an explicit retry classification. Any other
/// exception is reported as <c>retryable: true</c> (WORKER_PROTOCOL F4).
/// </summary>
public class TaskFailedException : Exception
{
    /// <summary>Creates a new exception.</summary>
    public TaskFailedException(string message, bool retryable, Exception? innerException = null) : base(message, innerException)
        => Retryable = retryable;

    /// <summary>Whether the engine may retry the step (subject to its retry policy).</summary>
    public bool Retryable { get; }
}

/// <summary>A transient handler failure: reported with <c>retryable: true</c>.</summary>
public class RetryableTaskException : TaskFailedException
{
    /// <summary>Creates a new exception.</summary>
    public RetryableTaskException(string message, Exception? innerException = null) : base(message, true, innerException) { }
}

/// <summary>A permanent handler failure: reported with <c>retryable: false</c>.</summary>
public class NonRetryableTaskException : TaskFailedException
{
    /// <summary>Creates a new exception.</summary>
    public NonRetryableTaskException(string message, Exception? innerException = null) : base(message, false, innerException) { }
}

/// <summary>
/// The worker no longer owns the task (a mutation returned 404/409). Thrown from
/// <see cref="WorkerTaskContext.CheckpointAsync"/>; the SDK sends no complete/fail for this claim.
/// </summary>
public class LeaseLostException : Orch8Exception
{
    /// <summary>Creates a new exception.</summary>
    public LeaseLostException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>Everything a handler receives for one claimed task.</summary>
public sealed class WorkerTaskContext
{
    private readonly Orch8Worker.TaskState _state;

    internal WorkerTaskContext(Orch8Worker.TaskState state, string workerId)
    {
        _state = state;
        WorkerId = workerId;
    }

    /// <summary>The claimed task as returned by the poll.</summary>
    public WorkerTask Task => _state.Task;
    /// <summary>Task id.</summary>
    public string TaskId => _state.Task.Id;
    /// <summary>Owning instance id.</summary>
    public string InstanceId => _state.Task.InstanceId;
    /// <summary>Step id.</summary>
    public string BlockId => _state.Task.BlockId;
    /// <summary>Handler name.</summary>
    public string HandlerName => _state.Task.HandlerName;
    /// <summary>Step parameters.</summary>
    public JsonNode? Params => _state.Task.Params;
    /// <summary>Execution context.</summary>
    public JsonNode? Context => _state.Task.Context;
    /// <summary>0 on first dispatch.</summary>
    public int Attempt => _state.Task.Attempt;
    /// <summary>Timeout in ms (measured from task creation), if any.</summary>
    public long? TimeoutMs => _state.Task.TimeoutMs;
    /// <summary>Claim epoch echoed on every mutation.</summary>
    public long ClaimEpoch => _state.Task.ClaimEpoch;
    /// <summary>Last durable checkpoint, or null. Resume from here instead of repeating work.</summary>
    public JsonNode? ResumeCheckpoint => _state.Task.ResumeCheckpoint;
    /// <summary>The current expected checkpoint sequence (updated after each checkpoint).</summary>
    public long CheckpointSeq => _state.Seq;
    /// <summary>This worker's id.</summary>
    public string WorkerId { get; }

    /// <summary>
    /// Cancelled when the lease is lost, the task's <c>timeout_ms</c> elapses, or a shutdown
    /// drain times out. Not cancelled by a graceful stop.
    /// </summary>
    public CancellationToken CancellationToken => _state.HandlerToken;

    /// <summary>Deserializes <see cref="Params"/>.</summary>
    public T? GetParams<T>() => Orch8Json.FromNode<T>(Params);

    /// <summary>Deserializes <see cref="ResumeCheckpoint"/>.</summary>
    public T? GetResumeCheckpoint<T>() => Orch8Json.FromNode<T>(ResumeCheckpoint);

    /// <summary>
    /// Persists a resumable checkpoint (≤ 256 KiB JSON). The CAS <c>checkpoint_seq</c> is tracked
    /// automatically. Throws <see cref="LeaseLostException"/> if the lease is gone.
    /// </summary>
    public Task CheckpointAsync(object value, CancellationToken cancellationToken = default)
        => _state.Worker.CheckpointAsync(_state, value, cancellationToken);
}
