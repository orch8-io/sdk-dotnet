using System.Text.Json.Nodes;

namespace Orch8.Sdk.Tests;

public class WorkerTests
{
    private static async Task<(Task Run, CancellationTokenSource Stop)> Start(Orch8Worker worker)
    {
        var stop = new CancellationTokenSource();
        var run = worker.RunAsync(stop.Token);
        await Task.Yield();
        return (run, stop);
    }

    private static async Task StopAndWait(Task run, CancellationTokenSource stop)
    {
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task PollsExecutesAndCompletesWithClaimEpoch()
    {
        var engine = new FakeEngine();
        var task = engine.Enqueue("echo", new JsonObject { ["n"] = 1 });
        await using var worker = engine.CreateWorker();
        worker.Register("echo", ctx => Task.FromResult<object?>(new { echo = ctx.Params, attempt = ctx.Attempt }));
        var (run, stop) = await Start(worker);

        await FakeEngine.Eventually(() => engine.Acks(task["id"]!.GetValue<string>()).Count == 1);
        await StopAndWait(run, stop);

        var poll = engine.Polls.First();
        Assert.Equal("/api/v1/workers/tasks/poll", poll.RawPath);
        Assert.Equal("echo", poll.Json!["handler_name"]!.GetValue<string>());
        Assert.Equal("test-worker", poll.Json!["worker_id"]!.GetValue<string>());
        Assert.InRange(poll.Json!["limit"]!.GetValue<int>(), 1, 4);
        Assert.False(poll.Json!.AsObject().ContainsKey("queue_name"));
        Assert.Equal("k", poll.Headers["x-api-key"]);
        Assert.Equal("t", poll.Headers["x-tenant-id"]);

        var complete = engine.Acks(task["id"]!.GetValue<string>()).Single();
        Assert.EndsWith("/complete", complete.RawPath);
        Assert.Equal(1, complete.Json!["claim_epoch"]!.GetValue<long>());
        Assert.Equal("test-worker", complete.Json!["worker_id"]!.GetValue<string>());
        Assert.Equal("{\"echo\":{\"n\":1},\"attempt\":0}", complete.Json!["output"]!.ToJsonString());
    }

    [Fact]
    public async Task HandlerWithoutOutputCompletesWithEmptyObject()
    {
        var engine = new FakeEngine();
        var task = engine.Enqueue("noop");
        await using var worker = engine.CreateWorker();
        worker.Register("noop", _ => Task.CompletedTask);
        var (run, stop) = await Start(worker);
        await FakeEngine.Eventually(() => engine.Acks(task["id"]!.GetValue<string>()).Count == 1);
        await StopAndWait(run, stop);
        Assert.Equal("{}", engine.Acks(task["id"]!.GetValue<string>()).Single().Json!["output"]!.ToJsonString());
    }

    [Theory]
    [InlineData("retryable", true, "boom")]
    [InlineData("permanent", false, "fatal")]
    [InlineData("generic", true, "crash")]
    public async Task FailuresAreClassifiedWithExplicitRetryableFlag(string kind, bool retryable, string message)
    {
        var engine = new FakeEngine();
        var task = engine.Enqueue(kind);
        await using var worker = engine.CreateWorker();
        worker.Register(kind, _ => kind switch
        {
            "retryable" => throw new RetryableTaskException("boom"),
            "permanent" => throw new NonRetryableTaskException("fatal"),
            _ => throw new InvalidOperationException("crash"),
        });
        var (run, stop) = await Start(worker);
        var id = task["id"]!.GetValue<string>();
        await FakeEngine.Eventually(() => engine.Acks(id).Count == 1);
        await StopAndWait(run, stop);

        var fail = engine.Acks(id).Single();
        Assert.EndsWith("/fail", fail.RawPath);
        Assert.Equal(retryable, fail.Json!["retryable"]!.GetValue<bool>());
        Assert.Equal(message, fail.Json!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task ClaimForUnregisteredHandlerFailsPermanently()
    {
        var engine = new FakeEngine();
        var task = engine.Enqueue("ghost");
        await using var worker = engine.CreateWorker();
        worker.Register("echo", _ => Task.CompletedTask);
        Assert.Equal(1, await worker.ClaimAsync("ghost"));
        var id = task["id"]!.GetValue<string>();
        await FakeEngine.Eventually(() => engine.Acks(id).Count == 1);
        Assert.False(engine.Acks(id).Single().Json!["retryable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CheckpointsResumeAndTrackCasSequence()
    {
        var engine = new FakeEngine();
        var task = engine.Enqueue("checkpoint", new JsonObject { ["steps"] = 3 }, t =>
        {
            t["resume_checkpoint"] = new JsonObject { ["step"] = 1 };
            t["checkpoint_seq"] = 4;
        });
        await using var worker = engine.CreateWorker();
        worker.Register("checkpoint", async ctx =>
        {
            var start = ctx.ResumeCheckpoint?["step"]?.GetValue<int>() ?? 0;
            for (var i = start + 1; i <= 3; i++) await ctx.CheckpointAsync(new { step = i });
            return new { resumed_from = start, seq = ctx.CheckpointSeq };
        });
        var (run, stop) = await Start(worker);
        var id = task["id"]!.GetValue<string>();
        await FakeEngine.Eventually(() => engine.Acks(id).Count == 1);
        await StopAndWait(run, stop);

        var cps = engine.Mutations(id, "heartbeat").Where(r => r.Json!.AsObject().ContainsKey("checkpoint")).ToList();
        Assert.Equal(new long[] { 4, 5 }, cps.Select(r => r.Json!["checkpoint_seq"]!.GetValue<long>()));
        Assert.Equal(new[] { 2, 3 }, cps.Select(r => r.Json!["checkpoint"]!["step"]!.GetValue<int>()));
        Assert.Equal("{\"resumed_from\":1,\"seq\":6}", engine.Acks(id).Single().Json!["output"]!.ToJsonString());
    }

    [Theory]
    [InlineData(409)]
    [InlineData(404)]
    public async Task LeaseLossOnHeartbeatStopsHeartbeatsCancelsHandlerAndSkipsAck(int status)
    {
        var engine = new FakeEngine { HeartbeatIntervalSecs = 1, LeaseSecs = 1 }; // → 500 ms heartbeats
        var task = engine.Enqueue("slow");
        var id = task["id"]!.GetValue<string>();
        engine.Override = r => r.RawPath.EndsWith($"{id}/heartbeat") ? FakeHttp.Error(status, "conflict", "lost") : null;
        var cancelled = new TaskCompletionSource();
        await using var worker = engine.CreateWorker();
        worker.Register("slow", async ctx =>
        {
            try { await Task.Delay(10_000, ctx.CancellationToken); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
        });
        var (run, stop) = await Start(worker);

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(1200); // would allow at least two more heartbeats
        Assert.Single(engine.Mutations(id, "heartbeat"));
        Assert.Empty(engine.Acks(id));
        Assert.Equal(0, worker.InFlightCount);
        Assert.Equal(4, worker.FreeSlots);
        await StopAndWait(run, stop);
    }

    [Fact]
    public async Task LeaseLossOnCheckpointThrowsLeaseLostAndSkipsAck()
    {
        var engine = new FakeEngine();
        var task = engine.Enqueue("cp");
        var id = task["id"]!.GetValue<string>();
        engine.Override = r => r.RawPath.EndsWith($"{id}/heartbeat") ? FakeHttp.Error(409, "conflict", "stale seq") : null;
        Exception? seen = null;
        await using var worker = engine.CreateWorker();
        worker.Register("cp", async ctx =>
        {
            try { await ctx.CheckpointAsync(new { step = 1 }); }
            catch (Exception e) { seen = e; throw; }
        });
        var (run, stop) = await Start(worker);
        await FakeEngine.Eventually(() => seen is not null && worker.InFlightCount == 0);
        await StopAndWait(run, stop);
        Assert.IsType<LeaseLostException>(seen);
        Assert.Empty(engine.Acks(id));
    }

    [Fact]
    public async Task CompleteIsRetriedWithIdenticalBodyAfter503()
    {
        var engine = new FakeEngine();
        var task = engine.Enqueue("echo", new JsonObject { ["x"] = "y" });
        var id = task["id"]!.GetValue<string>();
        var failures = 2;
        engine.Override = r => r.RawPath.EndsWith($"{id}/complete") && Interlocked.Decrement(ref failures) >= 0
            ? FakeHttp.Error(503, "unavailable", "down")
            : null;
        await using var worker = engine.CreateWorker();
        worker.Register("echo", ctx => Task.FromResult<object?>(new { echo = ctx.Params }));
        var (run, stop) = await Start(worker);
        await FakeEngine.Eventually(() => engine.Acks(id).Count == 3 && worker.InFlightCount == 0);
        await StopAndWait(run, stop);

        var acks = engine.Acks(id);
        Assert.All(acks, a => Assert.EndsWith("/complete", a.RawPath));
        Assert.Single(acks.Select(a => a.Body).Distinct());
    }

    [Fact]
    public async Task CompleteRejectedWith409IsNotRetriedNorTurnedIntoFail()
    {
        var engine = new FakeEngine();
        var task = engine.Enqueue("echo");
        var id = task["id"]!.GetValue<string>();
        engine.Override = r => r.RawPath.EndsWith($"{id}/complete") ? FakeHttp.Error(409, "conflict", "lease changed") : null;
        await using var worker = engine.CreateWorker();
        worker.Register("echo", _ => Task.FromResult<object?>(new { ok = true }));
        var (run, stop) = await Start(worker);
        await FakeEngine.Eventually(() => engine.Acks(id).Count >= 1 && worker.InFlightCount == 0);
        await Task.Delay(300);
        await StopAndWait(run, stop);
        var ack = Assert.Single(engine.Acks(id));
        Assert.EndsWith("/complete", ack.RawPath);
    }

    [Fact]
    public async Task TimeoutCancelsHandlerAndFailsRetryable()
    {
        var engine = new FakeEngine();
        var task = engine.Enqueue("slow", null, t => t["timeout_ms"] = 300);
        var id = task["id"]!.GetValue<string>();
        await using var worker = engine.CreateWorker();
        var observed = false;
        worker.Register("slow", async ctx =>
        {
            try { await Task.Delay(10_000, ctx.CancellationToken); }
            catch (OperationCanceledException) { observed = true; throw; }
        });
        var (run, stop) = await Start(worker);
        await FakeEngine.Eventually(() => engine.Acks(id).Count == 1);
        await StopAndWait(run, stop);
        var fail = engine.Acks(id).Single();
        Assert.EndsWith("/fail", fail.RawPath);
        Assert.True(fail.Json!["retryable"]!.GetValue<bool>());
        Assert.Contains("timed out", fail.Json!["message"]!.GetValue<string>());
        Assert.True(observed);
    }

    [Fact]
    public async Task TimeoutIsEnforcedEvenIfHandlerIgnoresCancellation()
    {
        var engine = new FakeEngine();
        var task = engine.Enqueue("stubborn", null, t => t["timeout_ms"] = 200);
        var id = task["id"]!.GetValue<string>();
        await using var worker = engine.CreateWorker();
        worker.Register("stubborn", async _ => { await Task.Delay(3000); return new { late = true }; });
        var (run, stop) = await Start(worker);
        await FakeEngine.Eventually(() => engine.Acks(id).Count == 1, 2000);
        Assert.EndsWith("/fail", engine.Acks(id).Single().RawPath);
        await StopAndWait(run, stop);
    }

    [Fact]
    public async Task ConcurrencyIsBoundedAcrossHandlerLoops()
    {
        var engine = new FakeEngine();
        for (var i = 0; i < 6; i++) engine.Enqueue(i % 2 == 0 ? "a" : "b");
        await using var worker = engine.CreateWorker(o => o.Concurrency = 2);
        var running = 0;
        var maxRunning = 0;
        async Task<object?> Work(WorkerTaskContext _)
        {
            var now = Interlocked.Increment(ref running);
            lock (engine) maxRunning = Math.Max(maxRunning, now);
            await Task.Delay(150);
            Interlocked.Decrement(ref running);
            return null;
        }
        worker.Register("a", Work);
        worker.Register("b", Work);
        var (run, stop) = await Start(worker);
        await FakeEngine.Eventually(() => engine.Http.Requests.Count(r => r.RawPath.EndsWith("/complete")) == 6);
        await StopAndWait(run, stop);

        Assert.InRange(maxRunning, 1, 2);
        Assert.InRange(engine.MaxInFlight, 1, 2);
        Assert.All(engine.Polls, p => Assert.InRange(p.Json!["limit"]!.GetValue<int>(), 1, 2));
    }

    [Fact]
    public async Task EmptyPollHonoursPollAfterMs()
    {
        var engine = new FakeEngine { PollAfterMs = 300 };
        await using var worker = engine.CreateWorker(o => o.PollInterval = TimeSpan.FromMilliseconds(5));
        worker.Register("idle", _ => Task.CompletedTask);
        var (run, stop) = await Start(worker);
        await Task.Delay(1100);
        await StopAndWait(run, stop);
        var polls = engine.Polls.Select(p => p.At).ToList();
        Assert.InRange(polls.Count, 2, 5);
        for (var i = 1; i < polls.Count; i++) Assert.True((polls[i] - polls[i - 1]).TotalMilliseconds >= 280, "poll_after_ms not honoured");
    }

    [Fact]
    public async Task PollErrorsBackOffExponentiallyAndRecover()
    {
        var engine = new FakeEngine();
        var failing = 3;
        engine.Override = r => r.RawPath.Contains("/poll") && Interlocked.Decrement(ref failing) >= 0 ? FakeHttp.Error(503, "unavailable", "down") : null;
        var task = engine.Enqueue("echo");
        await using var worker = engine.CreateWorker(o => o.PollInterval = TimeSpan.FromMilliseconds(20));
        worker.Register("echo", _ => Task.CompletedTask);
        var (run, stop) = await Start(worker);
        await FakeEngine.Eventually(() => engine.Acks(task["id"]!.GetValue<string>()).Count == 1);
        await StopAndWait(run, stop);
        var at = engine.Polls.Select(p => p.At).Take(4).ToList();
        var gaps = at.Zip(at.Skip(1), (a, b) => (b - a).TotalMilliseconds).ToList();
        Assert.True(gaps[0] >= 35 && gaps[1] >= 75 && gaps[2] >= 150, $"gaps {string.Join(",", gaps)}");
    }

    [Fact]
    public async Task HeartbeatIntervalIsCappedByServerHints()
    {
        var engine = new FakeEngine { HeartbeatIntervalSecs = 5, LeaseSecs = 60 };
        await using var worker = engine.CreateWorker(o => o.HeartbeatInterval = TimeSpan.FromSeconds(15));
        worker.Register("x", _ => Task.CompletedTask);
        Assert.Equal(TimeSpan.FromSeconds(15), worker.EffectiveHeartbeatInterval);
        await worker.ClaimAsync("x");
        Assert.Equal(TimeSpan.FromSeconds(5), worker.EffectiveHeartbeatInterval);
        engine.LeaseSecs = 4;
        await worker.ClaimAsync("x");
        Assert.Equal(TimeSpan.FromSeconds(2), worker.EffectiveHeartbeatInterval);
    }

    [Fact]
    public async Task HeartbeatsAtServerHintWhileRunning()
    {
        var engine = new FakeEngine { HeartbeatIntervalSecs = 1 };
        var task = engine.Enqueue("slow");
        var id = task["id"]!.GetValue<string>();
        await using var worker = engine.CreateWorker();
        worker.Register("slow", ctx => Task.Delay(2300, ctx.CancellationToken));
        var (run, stop) = await Start(worker);
        await FakeEngine.Eventually(() => engine.Acks(id).Count == 1);
        await StopAndWait(run, stop);
        var hbs = engine.Mutations(id, "heartbeat");
        Assert.InRange(hbs.Count, 2, 3);
        Assert.All(hbs, h => Assert.Equal(new[] { "claim_epoch", "worker_id" }, h.Json!.AsObject().Select(kv => kv.Key).OrderBy(k => k)));
    }

    [Fact]
    public async Task QueueAndVersionAreSentOnPolls()
    {
        var engine = new FakeEngine();
        await using var worker = engine.CreateWorker(o => { o.Queue = "gpu"; o.Version = "1.4.2"; });
        worker.Register("render", _ => Task.CompletedTask);
        var (run, stop) = await Start(worker);
        await FakeEngine.Eventually(() => engine.Polls.Count >= 1);
        await StopAndWait(run, stop);
        var poll = engine.Polls[0];
        Assert.Equal("/api/v1/workers/tasks/poll/queue", poll.RawPath);
        Assert.Equal("gpu", poll.Json!["queue_name"]!.GetValue<string>());
        Assert.Equal("1.4.2", poll.Json!["version"]!.GetValue<string>());
    }

    [Fact]
    public async Task GracefulShutdownStopsPollingAndDrainsInFlight()
    {
        var engine = new FakeEngine();
        var task = engine.Enqueue("slow");
        var id = task["id"]!.GetValue<string>();
        var started = new TaskCompletionSource();
        await using var worker = engine.CreateWorker();
        worker.Register("slow", async ctx =>
        {
            started.TrySetResult();
            await Task.Delay(400, ctx.CancellationToken);
            return new { done = true };
        });
        var (run, stop) = await Start(worker);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        var stoppedAt = DateTime.UtcNow;
        Assert.False(run.IsCompleted);
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        var ack = Assert.Single(engine.Acks(id));
        Assert.EndsWith("/complete", ack.RawPath);
        // At most one poll that was already in flight when stop was requested.
        Assert.True(engine.Polls.Count(p => p.At > stoppedAt.AddMilliseconds(5)) <= 1);
    }

    [Fact]
    public async Task DrainTimeoutAbandonsTasksWithoutAck()
    {
        var engine = new FakeEngine();
        var task = engine.Enqueue("forever");
        var id = task["id"]!.GetValue<string>();
        var started = new TaskCompletionSource();
        var cancelled = false;
        await using var worker = engine.CreateWorker(o => o.ShutdownTimeout = TimeSpan.FromMilliseconds(200));
        worker.Register("forever", async ctx =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ctx.CancellationToken); }
            catch (OperationCanceledException) { cancelled = true; throw; }
        });
        var (run, stop) = await Start(worker);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        await FakeEngine.Eventually(() => cancelled, 2000);
        await Task.Delay(100);
        Assert.Empty(engine.Acks(id));
    }

    [Fact]
    public void ClassifyTreatsOnlyExplicitNonRetryableAsPermanent()
    {
        Assert.False(Orch8Worker.Classify(new NonRetryableTaskException("x")).Retryable);
        Assert.False(Orch8Worker.Classify(new TaskFailedException("x", false)).Retryable);
        Assert.True(Orch8Worker.Classify(new RetryableTaskException("x")).Retryable);
        Assert.True(Orch8Worker.Classify(new Exception("x")).Retryable);
        Assert.False(Orch8Worker.Classify(new AggregateException(new NonRetryableTaskException("inner"))).Retryable);
        Assert.Equal("Exception", Orch8Worker.Classify(new Exception("")).Message);
    }

    [Fact]
    public void RegistrationValidation()
    {
        var engine = new FakeEngine();
        var worker = engine.CreateWorker();
        worker.Register("a", _ => Task.CompletedTask);
        Assert.Throws<InvalidOperationException>(() => worker.Register("a", _ => Task.CompletedTask));
        Assert.Throws<ArgumentException>(() => worker.Register("", _ => Task.CompletedTask));
        Assert.True(worker.HasHandler("a"));
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.CreateWorker(o => o.Concurrency = 0));
    }

    [Fact]
    public void DefaultWorkerIdIsHostnamePid()
    {
        Assert.Equal($"{Environment.MachineName}-{Environment.ProcessId}", new Orch8WorkerOptions().WorkerId);
    }
}
