// Conformance adapter for sdk-contract/conformance (see its README, "The adapter contract").
// Every mode is implemented on top of the SDK's public API only.
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orch8.Sdk;

namespace Orch8.Sdk.Conformance;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = Orch8Json.Options;

    public static async Task<int> Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "";
        try
        {
            switch (mode)
            {
                case "worker": return await RunWorker();
                case "push": return await RunPush();
                case "verify": return await RunVerify();
                case "client": return await RunClient();
                default:
                    Console.Error.WriteLine("usage: conformance <worker|push|verify|client>");
                    return 2;
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"adapter error: {e}");
            return 1;
        }
    }

    // ---------------------------------------------------------------------
    // Shared
    // ---------------------------------------------------------------------

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    private static CancellationTokenSource OnTerminate()
    {
        var cts = new CancellationTokenSource();
        void Handler(PosixSignalContext ctx)
        {
            ctx.Cancel = true; // we exit on our own after draining
            cts.Cancel();
        }
        // Keep registrations alive for the life of the process.
        Registrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, Handler));
        Registrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGINT, Handler));
        return cts;
    }

    private static readonly List<PosixSignalRegistration> Registrations = new();

    private static (Orch8Client Client, Orch8WorkerOptions Options) WorkerFromEnv()
    {
        var client = new Orch8Client(Orch8ClientOptions.FromEnvironment());
        var options = Orch8WorkerOptions.FromEnvironment();
        if (Env("ORCH8_CONCURRENCY") is null) options.Concurrency = 4;
        if (Env("ORCH8_POLL_INTERVAL_MS") is null) options.PollInterval = TimeSpan.FromMilliseconds(100);
        if (Env("ORCH8_SHUTDOWN_TIMEOUT_MS") is null) options.ShutdownTimeout = TimeSpan.FromSeconds(10);
        return (client, options);
    }

    private static void RegisterStandardHandlers(Orch8Worker worker)
    {
        worker.Register("echo", ctx => Task.FromResult<JsonNode>(new JsonObject { ["echo"] = ctx.Params?.DeepClone() }));

        worker.Register("fail_retryable", ctx =>
        {
            var message = ctx.Params?["message"]?.GetValue<string>() ?? "boom";
            throw new RetryableTaskException(message);
        });

        worker.Register("fail_permanent", _ => throw new NonRetryableTaskException("fatal"));

        worker.Register("crash", _ => throw new InvalidOperationException("crash"));

        worker.Register("checkpoint", async ctx =>
        {
            var start = ctx.ResumeCheckpoint?["step"]?.GetValue<int>() ?? 0;
            var steps = ctx.Params?["steps"]?.GetValue<int>() ?? 3;
            for (var i = start + 1; i <= steps; i++)
                await ctx.CheckpointAsync(new { step = i }, ctx.CancellationToken);
            return new { resumed_from = start, final_step = steps };
        });

        worker.Register("slow", async ctx =>
        {
            var ms = ctx.Params?["sleep_ms"]?.GetValue<int>() ?? 1000;
            await Task.Delay(ms, ctx.CancellationToken);
            return new { slept = ms };
        });
    }

    // ---------------------------------------------------------------------
    // worker
    // ---------------------------------------------------------------------

    private static async Task<int> RunWorker()
    {
        using var stop = OnTerminate();
        var (client, options) = WorkerFromEnv();
        using (client)
        {
            await using var worker = new Orch8Worker(client, options);
            RegisterStandardHandlers(worker);
            await worker.RunAsync(stop.Token);
        }
        return 0;
    }

    // ---------------------------------------------------------------------
    // push
    // ---------------------------------------------------------------------

    private static async Task<int> RunPush()
    {
        using var stop = OnTerminate();
        var (client, options) = WorkerFromEnv();
        var secret = Env("ORCH8_PUSH_SECRET") ?? throw new InvalidOperationException("ORCH8_PUSH_SECRET is required");
        var tolerance = TimeSpan.FromSeconds(int.TryParse(Env("ORCH8_PUSH_TOLERANCE_SECS"), out var t) ? t : 300);
        var port = int.Parse(Env("ORCH8_PUSH_PORT") ?? throw new InvalidOperationException("ORCH8_PUSH_PORT is required"));

        using (client)
        {
            await using var worker = new Orch8Worker(client, options);
            RegisterStandardHandlers(worker);
            var receiver = new Orch8PushReceiver(worker, secret, tolerance);

            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://*:{port}/");
            listener.Start();
            Console.Out.WriteLine("READY");
            Console.Out.Flush();

            var serve = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    HttpListenerContext ctx;
                    try
                    {
                        ctx = await listener.GetContextAsync().WaitAsync(stop.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception) when (!listener.IsListening)
                    {
                        break;
                    }
                    _ = Task.Run(() => HandlePush(ctx, receiver));
                }
            });

            await serve;
            listener.Stop();
            await worker.DrainAsync(options.ShutdownTimeout);
        }
        return 0;
    }

    private static async Task HandlePush(HttpListenerContext ctx, Orch8PushReceiver receiver)
    {
        try
        {
            using var ms = new MemoryStream();
            await ctx.Request.InputStream.CopyToAsync(ms);
            var status = ctx.Request.HttpMethod == "POST"
                ? receiver.Handle(ctx.Request.Headers[Orch8PushVerifier.TimestampHeader], ctx.Request.Headers[Orch8PushVerifier.SignatureHeader], ms.ToArray())
                : 405;
            ctx.Response.StatusCode = status;
            ctx.Response.ContentLength64 = 0;
            ctx.Response.Close();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"push handler error: {e.Message}");
            try { ctx.Response.Abort(); } catch { /* ignore */ }
        }
    }

    // ---------------------------------------------------------------------
    // verify
    // ---------------------------------------------------------------------

    private static async Task<int> RunVerify()
    {
        await EachLine(line =>
        {
            var v = JsonNode.Parse(line)!.AsObject();
            bool valid;
            try
            {
                var now = v["now"] is { } n ? DateTimeOffset.FromUnixTimeSeconds(n.GetValue<long>()) : (DateTimeOffset?)null;
                var tol = v["tolerance_secs"] is { } ts ? TimeSpan.FromSeconds(ts.GetValue<double>()) : (TimeSpan?)null;
                valid = Orch8PushVerifier.Verify(
                    v["secret"]?.GetValue<string>() ?? "",
                    v["timestamp"]?.GetValue<string>(),
                    v["signature"]?.GetValue<string>(),
                    Encoding.UTF8.GetBytes(v["body"]?.GetValue<string>() ?? ""),
                    now, tol);
            }
            catch (ArgumentException)
            {
                valid = false; // empty secret: configuration error
            }
            return Task.FromResult<JsonNode>(new JsonObject { ["valid"] = valid });
        });
        return 0;
    }

    // ---------------------------------------------------------------------
    // client
    // ---------------------------------------------------------------------

    private static async Task<int> RunClient()
    {
        using var client = new Orch8Client(Orch8ClientOptions.FromEnvironment());
        await EachLine(async line =>
        {
            var req = JsonNode.Parse(line)!.AsObject();
            var id = req["id"]?.DeepClone();
            var op = req["op"]?.GetValue<string>() ?? "";
            var args = req["args"] as JsonObject ?? new JsonObject();
            try
            {
                var result = await ClientOp(client, op, args);
                return new JsonObject { ["id"] = id, ["ok"] = true, ["result"] = result };
            }
            catch (Orch8ApiException e)
            {
                return new JsonObject
                {
                    ["id"] = id,
                    ["ok"] = false,
                    ["error"] = new JsonObject { ["kind"] = Kind(e), ["status"] = e.Status, ["code"] = e.Code, ["message"] = e.Message },
                };
            }
            catch (Exception e)
            {
                var kind = e is Orch8TransportException ? "transport" : "api";
                return new JsonObject
                {
                    ["id"] = id,
                    ["ok"] = false,
                    ["error"] = new JsonObject { ["kind"] = kind, ["status"] = null, ["code"] = null, ["message"] = e.Message },
                };
            }
        });
        return 0;
    }

    private static string Kind(Orch8ApiException e) => e switch
    {
        Orch8BadRequestException => "invalid_argument",
        Orch8UnauthorizedException => "unauthorized",
        Orch8ForbiddenException => "forbidden",
        Orch8NotFoundException => "not_found",
        Orch8ConflictException => "conflict",
        Orch8PayloadTooLargeException => "payload_too_large",
        Orch8UnprocessableException => "unprocessable",
        Orch8RateLimitedException => "rate_limited",
        Orch8ServerException => "server",
        _ => "api",
    };

    private static T Arg<T>(JsonObject args, string name) where T : class
        => args[name]?.Deserialize<T>(Json) ?? throw new ArgumentException($"missing arg '{name}'");

    private static string Id(JsonObject args) => args["id"]?.GetValue<string>() ?? throw new ArgumentException("missing arg 'id'");

    private static JsonNode? ToJson<T>(T value) => value is null ? null : JsonSerializer.SerializeToNode(value, Json);

    private static async Task<JsonNode?> ClientOp(Orch8Client c, string op, JsonObject a)
    {
        return op switch
        {
            "sequences.create" => ToJson(await c.Sequences.CreateAsync(Arg<SequenceDefinition>(a, "body"))),
            "sequences.get" => ToJson(await c.Sequences.GetAsync(Id(a))),
            "sequences.list" => ToJson(await c.Sequences.ListAsync(a["query"]?.Deserialize<SequenceListQuery>(Json))),
            "instances.create" => ToJson(await c.Instances.CreateAsync(Arg<CreateInstanceRequest>(a, "body"))),
            "instances.get" => ToJson(await c.Instances.GetAsync(Id(a))),
            "instances.list" => ToJson(await c.Instances.ListAsync(a["query"]?.Deserialize<InstanceListQuery>(Json))),
            "instances.signal" => ToJson(await c.Instances.SignalAsync(Id(a), Arg<SignalType>(a, "signal_type"), a["payload"]?.DeepClone())),
            "instances.cancel" => ToJson(await c.Instances.CancelAsync(Id(a))),
            "jobs.enqueue" => ToJson(await c.Jobs.EnqueueAsync(Arg<EnqueueJobRequest>(a, "body"))),
            "jobs.get" => ToJson(await c.Jobs.GetAsync(Id(a))),
            "jobs.list" => ToJson(await c.Jobs.ListAsync(a["query"]?.Deserialize<JobListQuery>(Json))),
            "jobs.cancel" => ToJson(await c.Jobs.CancelAsync(Id(a))),
            _ => throw new ArgumentException($"unknown op {op}"),
        };
    }

    private static async Task EachLine(Func<string, Task<JsonNode>> fn)
    {
        using var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        string? line;
        while ((line = await stdin.ReadLineAsync()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var result = await fn(line);
            await stdout.WriteLineAsync(result.ToJsonString());
        }
    }
}
