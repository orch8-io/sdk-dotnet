using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Orch8.Sdk.Tests;

/// <summary>
/// Minimal scripted engine for worker tests: per-handler pending queues, claim epochs, checkpoint CAS,
/// and an <see cref="Override"/> hook to inject faults.
/// </summary>
public sealed class FakeEngine
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Queue<JsonObject>> _pending = new();
    private readonly Dictionary<string, JsonObject> _claimed = new();
    private int _nextId;

    public FakeEngine()
    {
        Http = new FakeHttp(Route);
    }

    public FakeHttp Http { get; }
    public int HeartbeatIntervalSecs { get; set; } = 1;
    public int LeaseSecs { get; set; } = 60;
    public long PollAfterMs { get; set; } = 50;

    /// <summary>Return a response to override the default behaviour for a request, or null.</summary>
    public Func<Recorded, HttpResponseMessage?>? Override { get; set; }

    public int InFlight { get; private set; }
    public int MaxInFlight { get; private set; }

    public JsonObject Enqueue(string handler, JsonNode? @params = null, Action<JsonObject>? customize = null)
    {
        lock (_lock)
        {
            var id = $"00000000-0000-7000-8000-{++_nextId:D12}";
            var task = new JsonObject
            {
                ["id"] = id,
                ["instance_id"] = $"inst-{_nextId}",
                ["block_id"] = "step-1",
                ["handler_name"] = handler,
                ["params"] = @params?.DeepClone() ?? new JsonObject(),
                ["context"] = new JsonObject { ["data"] = new JsonObject() },
                ["attempt"] = 0,
                ["timeout_ms"] = null,
                ["state"] = "pending",
                ["claim_epoch"] = 0,
                ["checkpoint_seq"] = 0,
                ["created_at"] = DateTimeOffset.UtcNow.ToString("O"),
            };
            customize?.Invoke(task);
            if (!_pending.TryGetValue(handler, out var q)) _pending[handler] = q = new Queue<JsonObject>();
            q.Enqueue(task);
            return task;
        }
    }

    public List<Recorded> Polls => Http.Requests.Where(r => r.RawPath.Contains("/workers/tasks/poll")).ToList();

    public List<Recorded> Mutations(string taskId, string kind) => Http.Find("POST", $"/workers/tasks/{taskId}/{kind}");

    public List<Recorded> Acks(string taskId) => Http.Requests
        .Where(r => r.RawPath.EndsWith($"/workers/tasks/{taskId}/complete") || r.RawPath.EndsWith($"/workers/tasks/{taskId}/fail"))
        .ToList();

    private HttpResponseMessage Route(Recorded r)
    {
        if (Override?.Invoke(r) is { } overridden) return overridden;
        var path = r.RawPath.Replace("/api/v1", "", StringComparison.Ordinal);
        var body = r.Json as JsonObject ?? new JsonObject();
        lock (_lock)
        {
            if (path is "/workers/tasks/poll" or "/workers/tasks/poll/queue")
            {
                var handler = body["handler_name"]!.GetValue<string>();
                var limit = (int)(L(body["limit"]) ?? 1);
                var tasks = new JsonArray();
                if (_pending.TryGetValue(handler, out var q))
                {
                    while (tasks.Count < limit && q.Count > 0)
                    {
                        var t = q.Dequeue();
                        t["state"] = "claimed";
                        t["worker_id"] = body["worker_id"]!.GetValue<string>();
                        t["claim_epoch"] = L(t["claim_epoch"]!) + 1;
                        _claimed[t["id"]!.GetValue<string>()] = t;
                        tasks.Add(t.DeepClone());
                        InFlight++;
                        MaxInFlight = Math.Max(MaxInFlight, InFlight);
                    }
                }
                return FakeHttp.Json(200, new JsonObject
                {
                    ["tasks"] = tasks,
                    ["lease_secs"] = LeaseSecs,
                    ["heartbeat_interval_secs"] = HeartbeatIntervalSecs,
                    ["poll_after_ms"] = tasks.Count == 0 ? PollAfterMs : 0,
                });
            }

            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries); // workers tasks {id} {kind}
            if (parts.Length == 4 && parts[0] == "workers" && parts[1] == "tasks")
            {
                var id = Uri.UnescapeDataString(parts[2]);
                if (!_claimed.TryGetValue(id, out var task)) return FakeHttp.Error(404, "not_found", "task not found");
                if (L(task["claim_epoch"]!) != L(body["claim_epoch"]) ||
                    task["worker_id"]?.GetValue<string>() != body["worker_id"]?.GetValue<string>())
                    return FakeHttp.Error(409, "conflict", "worker task ownership changed");
                switch (parts[3])
                {
                    case "heartbeat":
                        var seq = L(task["checkpoint_seq"]!);
                        if (body.ContainsKey("checkpoint"))
                        {
                            if (L(body["checkpoint_seq"]) != seq) return FakeHttp.Error(409, "conflict", "checkpoint sequence changed");
                            seq++;
                            task["checkpoint_seq"] = seq;
                            task["resume_checkpoint"] = body["checkpoint"]!.DeepClone();
                        }
                        return FakeHttp.Json(200, new JsonObject { ["checkpoint_seq"] = seq });
                    case "complete":
                    case "fail":
                        if (task["state"]!.GetValue<string>() == "claimed")
                        {
                            task["state"] = parts[3] == "complete" ? "completed" : "failed";
                            InFlight--;
                            return FakeHttp.Empty(200);
                        }
                        return parts[3] == "complete" ? FakeHttp.Empty(200) : FakeHttp.Error(409, "conflict", "already settled");
                }
            }
        }
        return FakeHttp.Error(404, "not_found", $"no route {r.Method} {path}");
    }

    public Orch8Worker CreateWorker(Action<Orch8WorkerOptions>? configure = null)
    {
        var options = new Orch8WorkerOptions
        {
            WorkerId = "test-worker",
            Concurrency = 4,
            PollInterval = TimeSpan.FromMilliseconds(10),
            ShutdownTimeout = TimeSpan.FromSeconds(5),
        };
        configure?.Invoke(options);
        return new Orch8Worker(new Orch8ClientOptions
        {
            BaseUrl = "http://engine.test/api/v1",
            ApiKey = "k",
            TenantId = "t",
            HttpMessageHandler = Http,
        }, options);
    }

    private static long? L(JsonNode? n) => n is null ? null : long.Parse(n.ToJsonString());

    public static async Task Eventually(Func<bool> condition, int timeoutMs = 5000, string? because = null)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(because ?? "condition not met in time");
            await Task.Delay(10);
        }
    }
}
