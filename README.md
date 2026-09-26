# Orch8 .NET SDK

.NET client, worker, and push-dispatch verifier for [Orch8](https://orch8.io), the self-hosted
durable workflow engine.

- **`Orch8.Sdk`** (net8.0): typed REST client (sequences, instances, jobs), long-poll worker,
  push-signature verifier and push receiver. Its only dependency is `Microsoft.Extensions.Logging.Abstractions`.
- **`Orch8.Sdk.Hosting`** (net8.0): `IHostedService` / DI integration for the Generic Host.

The worker implements the normative Orch8 worker wire protocol (contract version 1) and passes all
17 scenarios of the Orch8 SDK conformance kit ([orch8-io/sdk-contract](https://github.com/orch8-io/sdk-contract)). CI runs the unit tests;
conformance runs from a checkout of the kit next to this repo.

## Install

### Today: local feed from the GitHub release

The packages are not on nuget.org yet. Download them from the
[v0.1.0 GitHub release](https://github.com/orch8-io/sdk-dotnet/releases/tag/v0.1.0) into a folder
and use it as a package source:

```bash
mkdir -p ~/.nuget/local-orch8
gh release download v0.1.0 -R orch8-io/sdk-dotnet -p '*.nupkg' -D ~/.nuget/local-orch8
# or download Orch8.Sdk.0.1.0.nupkg (and Orch8.Sdk.Hosting.0.1.0.nupkg) from the release page

# register the folder as an extra source (nuget.org stays enabled for the dependencies)
dotnet nuget add source ~/.nuget/local-orch8 --name orch8-local

dotnet add package Orch8.Sdk --version 0.1.0
dotnet add package Orch8.Sdk.Hosting --version 0.1.0   # optional
```

Don't use `dotnet add package --source <folder>` for this: `--source` replaces nuget.org, so the
`Microsoft.Extensions.*` dependencies fail to restore. For a repo-local feed (e.g. for CI), commit
the `.nupkg` files and a `nuget.config` next to your solution:

```xml
<configuration>
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="orch8-local" value="./packages/orch8" />
  </packageSources>
</configuration>
```

The release also carries `.snupkg` symbol packages for debugging.

### Once on nuget.org

```bash
dotnet add package Orch8.Sdk            # client + worker + push
dotnet add package Orch8.Sdk.Hosting    # optional Generic Host integration
```

The release workflow pushes both packages to nuget.org when the `NUGET_API_KEY` repository secret
is set; without it that step is skipped.

## Client

```csharp
using System.Text.Json.Nodes;
using Orch8.Sdk;

using var client = new Orch8Client(new Orch8ClientOptions
{
    BaseUrl = "http://localhost:8080/api/v1",   // always include /api/v1
    ApiKey = Environment.GetEnvironmentVariable("ORCH8_API_KEY"),
    TenantId = "acme",
});

// Sequences
var seq = await client.Sequences.CreateAsync(new SequenceDefinition
{
    TenantId = "acme", Namespace = "default", Name = "onboarding",
    Blocks = new() { JsonNode.Parse("""{"type":"step","id":"welcome","handler":"send_email","params":{}}""") },
});

// Instances
var created = await client.Instances.CreateAsync(new CreateInstanceRequest
{
    SequenceId = seq.Id, TenantId = "acme", Namespace = "default",
    Context = new JsonObject { ["data"] = new JsonObject { ["user"] = "u1" } },
    IdempotencyKey = "signup-u1",
});
Console.WriteLine($"{created.Id} deduplicated={created.Deduplicated}");

var instance = await client.Instances.GetAsync(created.Id);
var running = await client.Instances.ListAsync(new InstanceListQuery { State = "running", Limit = 50 });
await client.Instances.SignalAsync(created.Id, SignalType.Custom("approve"), new { by = "alice" });
await client.Instances.CancelAsync(created.Id);

// Jobs
var job = await client.Jobs.EnqueueAsync(new EnqueueJobRequest
{
    Handler = "send_email",
    Payload = new JsonObject { ["to"] = "a@example.com" },
    Queue = "emails",
    Retry = new JobRetryPolicy { MaxAttempts = 4, InitialBackoffMs = 500 },
    IdempotencyKey = "welcome-a",
});
var jobs = await client.Jobs.ListAsync(new JobListQuery { Status = "scheduled", Limit = 20 });
await client.Jobs.CancelAsync(job.Id);
```

Behaviour:

- Every request sends `x-api-key` and `x-tenant-id` (when configured) and JSON bodies with snake_case
  names. Unset optional fields are omitted, never sent as `null`.
- Path ids are percent-encoded as a single segment (`a/b c` becomes `a%2Fb%20c`).
- Response models keep fields this SDK version doesn't model in `ExtensionData`, so fields the engine adds later are not lost.
- **Retries:** only safe methods (GET/HEAD) are retried, on `408/425/429/500/502/503/504` and
  transport errors, with exponential backoff (`MaxAttempts`, default 3; `RetryBaseDelay`, default 250 ms;
  `Retry-After` honoured up to `MaxRetryDelay`). POST/DELETE are never replayed.
- **Typed errors:** every 4xx/5xx becomes an `Orch8ApiException` carrying `Status`, `Code`, `Message`
  and `RequestId`, taken from the engine envelope `{"error":{"code","message","request_id"}}`.
  The subclasses are `Orch8BadRequestException` (400), `Orch8UnauthorizedException` (401),
  `Orch8ForbiddenException` (403), `Orch8NotFoundException` (404), `Orch8ConflictException` (409),
  `Orch8PayloadTooLargeException` (413), `Orch8UnprocessableException` (422),
  `Orch8RateLimitedException` (429, with `RetryAfter`) and `Orch8ServerException` (5xx).
  Connection failures and timeouts raise `Orch8TransportException`.
- You can inject an `HttpClient` (e.g. from `IHttpClientFactory`) through `Orch8ClientOptions.HttpClient`,
  or an `HttpMessageHandler` through `Orch8ClientOptions.HttpMessageHandler`.

## Worker

```csharp
using Orch8.Sdk;

using var client = new Orch8Client(Orch8ClientOptions.FromEnvironment());
await using var worker = new Orch8Worker(client, new Orch8WorkerOptions
{
    Concurrency = 10,                          // default 10
    PollInterval = TimeSpan.FromSeconds(1),    // default 1 s
    HeartbeatInterval = TimeSpan.FromSeconds(15), // default; capped by the server hint
    Queue = null,                              // set to poll a named queue
    Version = "1.4.2",                         // sent on polls (version pins)
    ShutdownTimeout = TimeSpan.FromSeconds(30),
});

worker.Register("send_email", async ctx =>
{
    var to = ctx.Params?["to"]?.GetValue<string>();
    if (to is null) throw new NonRetryableTaskException("missing 'to'");     // retryable: false
    await SendAsync(to, ctx.CancellationToken);                            // honour cancellation
    return new { message_id = "m-1" };                                     // merged into context.data
});

worker.Register("import_pages", async ctx =>
{
    var start = ctx.ResumeCheckpoint?["page"]?.GetValue<int>() ?? 0;       // resume, don't repeat work
    for (var page = start + 1; page <= 100; page++)
    {
        await ImportPageAsync(page, ctx.CancellationToken);
        await ctx.CheckpointAsync(new { page });                           // CAS seq tracked for you
    }
});

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
await worker.RunAsync(stop.Token);   // returns after a graceful drain
```

What the worker guarantees (WORKER_PROTOCOL §7):

| Concern | Behaviour |
|---|---|
| Concurrency | At most `Concurrency` tasks run at once across all handlers. Slots are reserved *before* each poll, so `limit` never exceeds free capacity and a full worker doesn't poll. |
| Cadence | One poll loop per handler. After an empty poll it waits `max(PollInterval, poll_after_ms)`. On poll errors it backs off exponentially (capped at 30 s) and resets after a success. |
| Heartbeats | Each in-flight task heartbeats every `min(HeartbeatInterval, heartbeat_interval_secs, lease_secs/2)`. |
| Checkpoints | `ctx.CheckpointAsync(value)` sends the CAS `checkpoint_seq` from the poll and then from each response. |
| Lease loss | A 404/409 on any mutation stops heartbeats, cancels `ctx.CancellationToken` and sends no complete or fail. A 409 from a checkpoint throws `LeaseLostException`. |
| Acks | `complete` is retried with the identical body on 5xx and transport errors. A 404/409 on complete or fail is never retried or reported as a handler failure. `retryable` is always sent explicitly. |
| Errors | `RetryableTaskException` is sent as `retryable: true` and `NonRetryableTaskException` as `false`. Any other exception is sent as `true`. |
| Timeouts | `timeout_ms` (measured from `created_at`) is enforced locally. The worker cancels the handler and reports `fail` with `retryable: true`. |
| Shutdown | When the token passed to `RunAsync` is cancelled, polling stops at once. In-flight tasks keep heartbeating, finish and ack, bounded by `ShutdownTimeout`. Anything still running after that is cancelled and left for lease recovery, with no ack. |

`WorkerTaskContext` exposes `Task` and shortcuts for `TaskId`, `InstanceId`, `BlockId`,
`HandlerName`, `Params`, `Context`, `Attempt`, `TimeoutMs`, `ClaimEpoch`, `ResumeCheckpoint` and
`CheckpointSeq`, plus `GetParams<T>()` and `GetResumeCheckpoint<T>()`.

### Generic Host

```csharp
using Orch8.Sdk.Hosting;

builder.Services.AddOrch8Worker(
    c => { c.BaseUrl = "http://orch8:8080/api/v1"; c.ApiKey = "..."; c.TenantId = "acme"; },
    (sp, worker) => worker.Register("send_email", ctx => sp.GetRequiredService<Mailer>().SendAsync(ctx)),
    o => o.Concurrency = 20);
// Set HostOptions.ShutdownTimeout >= Orch8WorkerOptions.ShutdownTimeout so the drain can finish.
```

## Push dispatch

A push is only a wake-up. The engine POSTs a signed envelope, and the receiver must then **claim**
through `POST /workers/tasks/poll/queue` (§8.3 D2). `Orch8PushReceiver` does both steps:

```csharp
var receiver = new Orch8PushReceiver(worker, secret: Environment.GetEnvironmentVariable("ORCH8_PUSH_SECRET")!);

// ASP.NET Core minimal API. Verify the *raw* body bytes, never re-serialized JSON.
app.MapPost("/orch8/push", async (HttpRequest req) =>
{
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    var status = receiver.Handle(
        req.Headers[Orch8PushVerifier.TimestampHeader],
        req.Headers[Orch8PushVerifier.SignatureHeader],
        ms.ToArray());
    return Results.StatusCode(status);   // 401 bad signature, 400 bad envelope, 202 accepted
});
```

To verify without the receiver:

```csharp
bool ok = Orch8PushVerifier.Verify(secret, timestampHeader, signatureHeader, rawBody,
    now: null /* UtcNow */, tolerance: TimeSpan.FromSeconds(300));
```

The verifier requires `sha256=` followed by exactly 64 hex digits (either case), an integer
timestamp within the tolerance window in both directions, and an HMAC-SHA256 over
`"<timestamp>." + rawBody`. The comparison uses `CryptographicOperations.FixedTimeEquals`. An empty
secret throws `ArgumentException`, because it is a configuration error.

## Development

Requires the .NET 8 SDK or newer. The libraries target `net8.0`. The test and adapter projects set
`<RollForward>Major</RollForward>` so they also run on machines that only have a newer runtime.

```bash
dotnet build
dotnet test                                                   # unit tests (xUnit, in-memory fake engine)

# conformance kit: needs Node >= 20 and github.com/orch8-io/sdk-contract cloned to ../sdk-contract
dotnet build conformance/Orch8.Sdk.Conformance -c Release
cd ../sdk-contract && node conformance/run.mjs --adapter "$PWD/../sdk-dotnet/bin/conformance"
```

To run the tests on a real .NET 8 runtime:

```bash
docker run --rm -v "$PWD":/src:ro mcr.microsoft.com/dotnet/sdk:8.0 bash -c \
  'mkdir /w && cd /src && tar --exclude="*/bin" --exclude="*/obj" -cf - . | tar -xf - -C /w && cd /w && dotnet test'
```

## License

MIT
