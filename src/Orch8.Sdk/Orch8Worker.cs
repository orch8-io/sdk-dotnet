using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Orch8.Sdk;

/// <summary>
/// Long-poll worker implementing WORKER_PROTOCOL.md §7: bounded concurrency (slots reserved before
/// each poll), <c>poll_after_ms</c> cadence with exponential backoff on errors, heartbeats at
/// <c>min(configured, server hint)</c>, CAS checkpoints, lease-loss handling (no ack after 404/409),
/// idempotent <c>complete</c> retries, local <c>timeout_ms</c> enforcement, and graceful drain.
/// </summary>
public sealed class Orch8Worker : IAsyncDisposable
{
    private readonly Orch8Transport _transport;
    private readonly bool _ownsTransport;
    private readonly Orch8WorkerOptions _options;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, Func<WorkerTaskContext, Task<object?>>> _handlers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<TaskState, byte> _inFlight = new();
    private readonly CancellationTokenSource _forceCts = new();
    private readonly object _slotLock = new();
    private int _free;
    private long _heartbeatMs;
    private int _running;
    private volatile bool _stopping;

    /// <summary>Creates a worker that shares <paramref name="client"/>'s connection.</summary>
    public Orch8Worker(Orch8Client client, Orch8WorkerOptions? options = null)
        : this(client?.Transport ?? throw new ArgumentNullException(nameof(client)), false, options) { }

    /// <summary>Creates a worker with its own connection.</summary>
    public Orch8Worker(Orch8ClientOptions clientOptions, Orch8WorkerOptions? options = null)
        : this(new Orch8Transport(clientOptions), true, options) { }

    private Orch8Worker(Orch8Transport transport, bool ownsTransport, Orch8WorkerOptions? options)
    {
        _transport = transport;
        _ownsTransport = ownsTransport;
        _options = options ?? new Orch8WorkerOptions();
        if (_options.Concurrency < 1) throw new ArgumentOutOfRangeException(nameof(options), "Concurrency must be >= 1");
        if (string.IsNullOrEmpty(_options.WorkerId)) throw new ArgumentException("WorkerId is required", nameof(options));
        if (_options.HeartbeatInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "HeartbeatInterval must be positive");
        _log = _options.Logger ?? NullLogger.Instance;
        _free = _options.Concurrency;
        _heartbeatMs = (long)_options.HeartbeatInterval.TotalMilliseconds;
    }

    /// <summary>The worker options.</summary>
    public Orch8WorkerOptions Options => _options;

    /// <summary>Registered handler names.</summary>
    public IReadOnlyCollection<string> HandlerNames => _handlers.Keys.ToArray();

    /// <summary>Number of tasks currently executing.</summary>
    public int InFlightCount => _inFlight.Count;

    /// <summary>Free concurrency slots (not reserved by a poll or a running task).</summary>
    public int FreeSlots { get { lock (_slotLock) return _free; } }

    /// <summary>The heartbeat interval currently in effect (configured value capped by the server hint).</summary>
    public TimeSpan EffectiveHeartbeatInterval => TimeSpan.FromMilliseconds(Interlocked.Read(ref _heartbeatMs));

    // ------------------------------------------------------------------
    // Registration
    // ------------------------------------------------------------------

    /// <summary>Registers a handler returning an output (serialized to JSON; <c>null</c> is sent as <c>{}</c>).</summary>
    public Orch8Worker Register<TOutput>(string handlerName, Func<WorkerTaskContext, Task<TOutput>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return RegisterCore(handlerName, async ctx => await handler(ctx).ConfigureAwait(false));
    }

    /// <summary>Registers a handler that produces no output (completed with <c>{}</c>).</summary>
    public Orch8Worker Register(string handlerName, Func<WorkerTaskContext, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return RegisterCore(handlerName, async ctx =>
        {
            await handler(ctx).ConfigureAwait(false);
            return null;
        });
    }

    private Orch8Worker RegisterCore(string handlerName, Func<WorkerTaskContext, Task<object?>> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(handlerName);
        if (Volatile.Read(ref _running) == 1) throw new InvalidOperationException("register handlers before starting the worker");
        if (!_handlers.TryAdd(handlerName, handler)) throw new InvalidOperationException($"handler '{handlerName}' is already registered");
        return this;
    }

    // ------------------------------------------------------------------
    // Run / shutdown
    // ------------------------------------------------------------------

    /// <summary>
    /// Polls every registered handler until <paramref name="stoppingToken"/> is cancelled, then stops
    /// polling immediately, keeps heartbeating in-flight tasks and waits up to
    /// <see cref="Orch8WorkerOptions.ShutdownTimeout"/> for them to finish and acknowledge.
    /// Tasks still running after the timeout are cancelled and abandoned without acknowledgement
    /// (the engine reclaims them after the lease expires).
    /// </summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        if (_handlers.IsEmpty) throw new InvalidOperationException("no handlers registered");
        if (Interlocked.Exchange(ref _running, 1) == 1) throw new InvalidOperationException("worker is already running");
        _log.LogInformation("orch8 worker {WorkerId} starting: handlers={Handlers} concurrency={Concurrency} queue={Queue}",
            _options.WorkerId, string.Join(",", _handlers.Keys), _options.Concurrency, _options.Queue ?? "(default)");
        var loops = _handlers.Keys.Select(h => Task.Run(() => PollLoopAsync(h, stoppingToken), CancellationToken.None)).ToArray();
        await Task.WhenAll(loops).ConfigureAwait(false);
        _log.LogInformation("orch8 worker {WorkerId} stopping: draining {Count} in-flight task(s)", _options.WorkerId, _inFlight.Count);
        await DrainAsync(_options.ShutdownTimeout).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops accepting new claims and waits up to <paramref name="timeout"/> for in-flight tasks to
    /// finish and acknowledge. Returns false (after cancelling and abandoning the rest) on timeout.
    /// </summary>
    public async Task<bool> DrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        _stopping = true;
        var deadline = DateTime.UtcNow + timeout;
        while (!_inFlight.IsEmpty)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            var all = Task.WhenAll(_inFlight.Keys.Select(s => s.Completion.Task));
            var winner = await Task.WhenAny(all, Task.Delay(remaining, cancellationToken)).ConfigureAwait(false);
            if (winner != all) break;
        }
        if (_inFlight.IsEmpty) return true;
        _log.LogWarning("orch8 worker {WorkerId}: drain timeout, abandoning {Count} task(s) without acknowledgement", _options.WorkerId, _inFlight.Count);
        foreach (var s in _inFlight.Keys) s.Abandon();
        _forceCts.Cancel();
        return false;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!_inFlight.IsEmpty) await DrainAsync(TimeSpan.Zero).ConfigureAwait(false);
        _forceCts.Dispose();
        if (_ownsTransport) _transport.Dispose();
    }

    private async Task PollLoopAsync(string handlerName, CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (FreeSlots <= 0)
                {
                    await Task.Delay(25, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                TimeSpan delay;
                try
                {
                    // The poll itself is not cancelled by the stopping token: a claim the server
                    // already made must be received and executed, not orphaned.
                    var res = await PollOnceAsync(handlerName, _options.Queue, _options.Concurrency, _forceCts.Token).ConfigureAwait(false);
                    failures = 0;
                    delay = res.Tasks.Count > 0
                        ? TimeSpan.Zero
                        : Max(_options.PollInterval, TimeSpan.FromMilliseconds(res.PollAfterMs ?? 0));
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    failures++;
                    var ms = Math.Min(_options.PollInterval.TotalMilliseconds * Math.Pow(2, Math.Min(failures, 20)), _options.MaxPollBackoff.TotalMilliseconds);
                    delay = TimeSpan.FromMilliseconds(ms);
                    _log.LogWarning(e, "orch8 poll for {Handler} failed ({Failures} in a row); retrying in {Delay} ms", handlerName, failures, (long)ms);
                }
                if (delay > TimeSpan.Zero) await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <summary>
    /// Claims up to <paramref name="limit"/> tasks for <paramref name="handlerName"/> (bounded by free
    /// slots) and starts executing them. Uses <c>/workers/tasks/poll/queue</c> when
    /// <paramref name="queueName"/> is set. This is how a push receiver reacts to a push (§8.3 D2).
    /// Returns the number of tasks started (0 when no slot is free or the worker is stopping).
    /// </summary>
    public async Task<int> ClaimAsync(string handlerName, string? queueName = null, int limit = 1, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(handlerName);
        if (_stopping) return 0;
        var res = await PollOnceAsync(handlerName, queueName, limit, cancellationToken).ConfigureAwait(false);
        return res.Tasks.Count;
    }

    /// <summary>True when a handler is registered under <paramref name="handlerName"/>.</summary>
    public bool HasHandler(string handlerName) => _handlers.ContainsKey(handlerName);

    private async Task<PollResponse> PollOnceAsync(string handlerName, string? queueName, int wanted, CancellationToken cancellationToken)
    {
        int limit;
        lock (_slotLock)
        {
            // P11: reserve the slots BEFORE the request so concurrent handler loops never
            // ask for more tasks than the worker can start.
            limit = Math.Min(wanted, _free);
            if (limit <= 0) return new PollResponse();
            _free -= limit;
        }

        PollResponse res;
        try
        {
            var body = new JsonObject
            {
                ["handler_name"] = handlerName,
                ["worker_id"] = _options.WorkerId,
                ["limit"] = limit,
            };
            if (queueName is not null) body["queue_name"] = queueName;
            if (_options.Version is not null) body["version"] = _options.Version;
            var path = queueName is not null ? "/workers/tasks/poll/queue" : "/workers/tasks/poll";
            res = await _transport.SendAsync<PollResponse>(HttpMethod.Post, path, body, cancellationToken).ConfigureAwait(false) ?? new PollResponse();
        }
        catch
        {
            ReleaseSlots(limit);
            throw;
        }

        res.Tasks ??= new List<WorkerTask>();
        var tasks = res.Tasks.Count > limit ? res.Tasks.GetRange(0, limit) : res.Tasks;
        if (res.Tasks.Count > limit)
            _log.LogWarning("orch8 poll returned {Count} tasks for limit {Limit}; ignoring the excess", res.Tasks.Count, limit);
        ReleaseSlots(limit - tasks.Count);
        UpdateHeartbeatInterval(res);
        foreach (var task in tasks) Start(task);
        res.Tasks = tasks;
        return res;
    }

    private void ReleaseSlots(int n)
    {
        if (n <= 0) return;
        lock (_slotLock) _free += n;
    }

    private void UpdateHeartbeatInterval(PollResponse res)
    {
        var ms = _options.HeartbeatInterval.TotalMilliseconds;
        if (res.HeartbeatIntervalSecs is > 0 and var hint) ms = Math.Min(ms, hint * 1000.0);
        if (res.LeaseSecs is > 0 and var lease) ms = Math.Min(ms, lease * 500.0);
        Interlocked.Exchange(ref _heartbeatMs, Math.Max(50, (long)ms));
    }

    // ------------------------------------------------------------------
    // Execution
    // ------------------------------------------------------------------

    private void Start(WorkerTask task)
    {
        var state = new TaskState(this, task, _forceCts.Token);
        _inFlight[state] = 0;
        _ = Task.Run(() => ExecuteAsync(state), CancellationToken.None);
    }

    private async Task ExecuteAsync(TaskState state)
    {
        var task = state.Task;
        using var hbCts = new CancellationTokenSource();
        var heartbeats = HeartbeatLoopAsync(state, hbCts.Token);
        try
        {
            if (!_handlers.TryGetValue(task.HandlerName, out var handler))
            {
                await AckAsync(state, "fail", Fail($"no handler registered for '{task.HandlerName}'", false)).ConfigureAwait(false);
                return;
            }

            if (task.TimeoutMs is { } timeoutMs and > 0)
            {
                var deadline = (task.CreatedAt ?? DateTimeOffset.UtcNow).AddMilliseconds(timeoutMs);
                var remaining = deadline - DateTimeOffset.UtcNow;
                state.ArmTimeout(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            }

            var ctx = new WorkerTaskContext(state, _options.WorkerId);
            Task<object?> handlerTask;
            try
            {
                handlerTask = handler(ctx);
            }
            catch (Exception e)
            {
                handlerTask = Task.FromException<object?>(e);
            }

            var interrupted = Task.Delay(Timeout.Infinite, state.HandlerToken);
            var first = await Task.WhenAny(handlerTask, interrupted).ConfigureAwait(false);
            if (first != handlerTask)
            {
                // Handler ignored cancellation; observe its eventual failure and move on.
                _ = handlerTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }

            if (state.Lost || state.Abandoned) return; // L2/L3: no ack after lease loss or forced shutdown

            if (first != handlerTask || (handlerTask.IsCanceled && state.TimedOut))
            {
                await AckAsync(state, "fail", Fail($"task timed out after {task.TimeoutMs} ms", true)).ConfigureAwait(false);
                return;
            }

            object? output;
            try
            {
                output = await handlerTask.ConfigureAwait(false);
            }
            catch (Exception e)
            {
                if (state.Lost || state.Abandoned || e is LeaseLostException) return;
                if (e is OperationCanceledException && state.TimedOut)
                {
                    await AckAsync(state, "fail", Fail($"task timed out after {task.TimeoutMs} ms", true)).ConfigureAwait(false);
                    return;
                }
                var (message, retryable) = Classify(e);
                _log.LogInformation("orch8 task {TaskId} ({Handler}) failed (retryable={Retryable}): {Message}", task.Id, task.HandlerName, retryable, message);
                await AckAsync(state, "fail", Fail(message, retryable)).ConfigureAwait(false);
                return;
            }

            if (state.Lost || state.Abandoned) return;
            JsonNode outputNode;
            try
            {
                outputNode = Orch8Json.ToNode(output) ?? new JsonObject();
            }
            catch (Exception e)
            {
                await AckAsync(state, "fail", Fail($"could not serialize handler output: {e.Message}", false)).ConfigureAwait(false);
                return;
            }
            await AckAsync(state, "complete", new JsonObject { ["output"] = outputNode }).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log.LogError(e, "orch8 task {TaskId}: unexpected worker error", task.Id);
        }
        finally
        {
            hbCts.Cancel();
            try { await heartbeats.ConfigureAwait(false); } catch { /* heartbeat loop never throws */ }
            state.Dispose();
            _inFlight.TryRemove(state, out _);
            ReleaseSlots(1);
            state.Completion.TrySetResult();
        }
    }

    private static JsonObject Fail(string message, bool retryable) => new()
    {
        ["message"] = string.IsNullOrEmpty(message) ? "error" : message,
        ["retryable"] = retryable, // F1: always explicit
    };

    internal static (string Message, bool Retryable) Classify(Exception e)
    {
        if (e is AggregateException { InnerExceptions.Count: 1 } agg) e = agg.InnerExceptions[0];
        var message = string.IsNullOrEmpty(e.Message) ? e.GetType().Name : e.Message;
        return e is TaskFailedException tf ? (message, tf.Retryable) : (message, true); // F4: generic → retryable
    }

    private async Task HeartbeatLoopAsync(TaskState state, CancellationToken token)
    {
        while (!token.IsCancellationRequested && !state.Lost && !state.Abandoned)
        {
            try
            {
                await Task.Delay(EffectiveHeartbeatInterval, token).ConfigureAwait(false);
                if (state.Lost || state.Abandoned) return;
                await MutateAsync(state, "heartbeat", null, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (LeaseLostException)
            {
                return; // L2: stop heartbeating
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "orch8 heartbeat for task {TaskId} failed; will retry", state.Task.Id);
            }
        }
    }

    private async Task AckAsync(TaskState state, string kind, JsonObject extra)
    {
        var attempts = Math.Max(1, _options.AckMaxAttempts);
        for (var i = 1; ; i++)
        {
            try
            {
                // The same JsonObject is re-sent on every attempt: identical body (K3).
                await MutateAsync(state, kind, extra, state.ForceToken).ConfigureAwait(false);
                return;
            }
            catch (LeaseLostException)
            {
                // L3: someone else owns the task (or it no longer exists); not a handler failure.
                _log.LogInformation("orch8 task {TaskId}: {Kind} rejected, lease lost; not retrying", state.Task.Id, kind);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (Orch8Transport.IsRetryable(e) && i < attempts)
            {
                var delay = Math.Min(200 * Math.Pow(2, i - 1), 5000);
                _log.LogWarning(e, "orch8 task {TaskId}: {Kind} attempt {Attempt} failed; retrying in {Delay} ms", state.Task.Id, kind, i, (long)delay);
                try { await Task.Delay(TimeSpan.FromMilliseconds(delay), state.ForceToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
            catch (Exception e)
            {
                // L4: never claim success for an ambiguous ack; leave it for lease recovery.
                _log.LogError(e, "orch8 task {TaskId}: {Kind} failed; leaving the task to lease recovery", state.Task.Id, kind);
                return;
            }
        }
    }

    internal async Task CheckpointAsync(TaskState state, object value, CancellationToken cancellationToken)
    {
        var node = Orch8Json.ToNode(value) ?? throw new ArgumentNullException(nameof(value));
        await state.CheckpointLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state.Lost) throw new LeaseLostException($"lease lost for task {state.Task.Id}");
            var body = new JsonObject { ["checkpoint"] = node, ["checkpoint_seq"] = state.Seq };
            var res = await MutateAsync(state, "heartbeat", body, cancellationToken).ConfigureAwait(false);
            // C2: the returned sequence is the expected value for the next checkpoint.
            state.Seq = res?["checkpoint_seq"]?.GetValue<long>() ?? state.Seq + 1;
        }
        finally
        {
            state.CheckpointLock.Release();
        }
    }

    private async Task<JsonNode?> MutateAsync(TaskState state, string kind, JsonObject? extra, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["worker_id"] = _options.WorkerId,
            ["claim_epoch"] = state.Task.ClaimEpoch,
        };
        if (extra is not null)
            foreach (var (k, v) in extra) body[k] = v?.DeepClone();
        try
        {
            return await _transport.SendAsync<JsonNode>(HttpMethod.Post, $"/workers/tasks/{Orch8Transport.Segment(state.Task.Id)}/{kind}", body, cancellationToken).ConfigureAwait(false);
        }
        catch (Orch8ApiException e) when (e.Status is 404 or 409)
        {
            _log.LogWarning("orch8 task {TaskId}: lease lost ({Status} {Code} on {Kind})", state.Task.Id, e.Status, e.Code, kind);
            state.MarkLost();
            throw new LeaseLostException($"lease lost for task {state.Task.Id}: {e.Message}", e);
        }
    }

    /// <summary>Per-claim execution state.</summary>
    internal sealed class TaskState : IDisposable
    {
        private readonly CancellationTokenSource _leaseCts = new();
        private readonly CancellationTokenSource _timeoutCts = new();
        private readonly CancellationTokenSource _abandonCts = new();
        private readonly CancellationTokenSource _handlerCts;
        private volatile bool _lost;
        private volatile bool _abandoned;
        private long _seq;

        public TaskState(Orch8Worker worker, WorkerTask task, CancellationToken forceToken)
        {
            Worker = worker;
            Task = task;
            ForceToken = forceToken;
            _seq = task.CheckpointSeq ?? 0; // C3
            _handlerCts = CancellationTokenSource.CreateLinkedTokenSource(_leaseCts.Token, _timeoutCts.Token, _abandonCts.Token, forceToken);
            HandlerToken = _handlerCts.Token;
        }

        public Orch8Worker Worker { get; }
        public WorkerTask Task { get; }
        public CancellationToken ForceToken { get; }
        public CancellationToken HandlerToken { get; }
        public SemaphoreSlim CheckpointLock { get; } = new(1, 1);
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Lost => _lost;
        public bool Abandoned => _abandoned || ForceToken.IsCancellationRequested;
        public bool TimedOut => _timeoutCts.IsCancellationRequested;

        public long Seq
        {
            get => Interlocked.Read(ref _seq);
            set => Interlocked.Exchange(ref _seq, value);
        }

        public void ArmTimeout(TimeSpan after) => _timeoutCts.CancelAfter(after);

        public void MarkLost()
        {
            _lost = true;
            TryCancel(_leaseCts);
        }

        public void Abandon()
        {
            _abandoned = true;
            TryCancel(_abandonCts);
        }

        private static void TryCancel(CancellationTokenSource cts)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            // Tokens already handed out stay valid after disposal; disposing the linked source
            // unregisters it from the worker-wide force token (no per-task leak).
            _handlerCts.Dispose();
            _timeoutCts.Dispose();
            _leaseCts.Dispose();
            _abandonCts.Dispose();
        }
    }
}
