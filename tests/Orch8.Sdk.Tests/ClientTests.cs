using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orch8.Sdk.Tests;

public class ClientTests
{
    private const string Base = "http://engine.test/api/v1";

    private static (Orch8Client, FakeHttp) Create(Func<Recorded, HttpResponseMessage> route, int maxAttempts = 3)
    {
        var fake = new FakeHttp(route);
        var client = new Orch8Client(new Orch8ClientOptions
        {
            BaseUrl = Base + "/",
            ApiKey = "k-1",
            TenantId = "t-1",
            MaxAttempts = maxAttempts,
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
            HttpMessageHandler = fake,
        });
        return (client, fake);
    }

    [Fact]
    public async Task SendsAuthTenantAndJsonHeadersUnderApiV1()
    {
        var (client, fake) = Create(_ => FakeHttp.Json(201, "{\"id\":\"s1\"}"));
        var res = await client.Sequences.CreateAsync(new SequenceDefinition
        {
            TenantId = "t-1", Namespace = "default", Name = "onboarding",
            Blocks = new() { JsonNode.Parse("{\"type\":\"step\",\"id\":\"s1\",\"handler\":\"echo\",\"params\":{}}") },
        });
        Assert.Equal("s1", res.Id);
        var req = Assert.Single(fake.Requests);
        Assert.Equal("/api/v1/sequences", req.RawPath);
        Assert.Equal("k-1", req.Headers["x-api-key"]);
        Assert.Equal("t-1", req.Headers["x-tenant-id"]);
        Assert.StartsWith("application/json", req.Headers["Content-Type"]);
        var body = req.Json!.AsObject();
        Assert.Equal("onboarding", body["name"]!.GetValue<string>());
        Assert.False(body.ContainsKey("id"));      // unset → omitted
        Assert.False(body.ContainsKey("version"));
    }

    [Fact]
    public async Task PathIdsAreEncodedAsOneSegment()
    {
        var (client, fake) = Create(_ => FakeHttp.Json(200, "{\"id\":\"a/b c\",\"status\":\"scheduled\"}"));
        await client.Jobs.GetAsync("a/b c");
        Assert.Equal("/api/v1/jobs/a%2Fb%20c", fake.Requests.Single().RawPath);
    }

    [Fact]
    public async Task ListQueriesCarryParamsAndExtras()
    {
        var (client, fake) = Create(_ => FakeHttp.Json(200, "[]"));
        await client.Instances.ListAsync(new InstanceListQuery
        {
            State = "running", Limit = 5, SequenceId = "seq 1",
            Extra = new() { ["custom"] = JsonDocument.Parse("\"x&y\"").RootElement.Clone() },
        });
        Assert.Equal("/api/v1/instances?sequence_id=seq%201&state=running&limit=5&custom=x%26y", fake.Requests.Single().PathAndQuery);
    }

    [Theory]
    [InlineData("[{\"id\":\"j1\"}]")]
    [InlineData("{\"items\":[{\"id\":\"j1\"}],\"total\":1}")]
    [InlineData("{\"jobs\":[{\"id\":\"j1\"}]}")]
    public async Task JobListAcceptsArrayOrWrapper(string body)
    {
        var (client, _) = Create(_ => FakeHttp.Json(200, body));
        var jobs = await client.Jobs.ListAsync(new JobListQuery { Status = "scheduled" });
        Assert.Equal("j1", Assert.Single(jobs).Id);
    }

    [Fact]
    public async Task EnqueueOmitsUnsetFieldsAndFormatsTimestamps()
    {
        var (client, fake) = Create(_ => FakeHttp.Json(201,
            "{\"id\":\"j1\",\"instance_id\":\"i1\",\"handler\":\"h\",\"status\":\"scheduled\",\"created_at\":\"2026-09-26T10:00:00Z\",\"run_at\":\"2026-09-26T10:05:00Z\",\"attempts\":0}"));
        var job = await client.Jobs.EnqueueAsync(new EnqueueJobRequest
        {
            Handler = "h",
            Payload = new JsonObject { ["to"] = "a" },
            RunAt = new DateTimeOffset(2026, 10, 1, 2, 0, 0, TimeSpan.FromHours(2)),
            Retry = new JobRetryPolicy { MaxAttempts = 3, InitialBackoffMs = 100 },
        });
        var body = fake.Requests.Single().Json!.AsObject();
        Assert.Equal(new[] { "handler", "payload", "retry", "run_at" }, body.Select(kv => kv.Key).OrderBy(k => k).ToArray());
        Assert.Equal("2026-10-01T00:00:00Z", body["run_at"]!.GetValue<string>());
        Assert.False(body["retry"]!.AsObject().ContainsKey("max_backoff_ms"));
        Assert.Equal("scheduled", job.Status);
        // unknown response fields are kept
        Assert.True(job.ExtensionData!.ContainsKey("attempts"));
        Assert.Contains("\"attempts\":0", JsonSerializer.Serialize(job, Orch8Json.Options));
    }

    [Fact]
    public async Task JobCancelAcceptsEmptyBody()
    {
        var (client, fake) = Create(_ => FakeHttp.Empty(204));
        Assert.Null(await client.Jobs.CancelAsync("j1"));
        Assert.Equal("DELETE", fake.Requests.Single().Method);
    }

    [Fact]
    public async Task SignalsSerializeBuiltInAndCustomTypes()
    {
        var (client, fake) = Create(_ => FakeHttp.Json(201, "{\"signal_id\":\"sig\"}"));
        var res = await client.Instances.SignalAsync("i1", SignalType.Custom("approve"), new { by = "alice" });
        await client.Instances.CancelAsync("i1");
        await client.Instances.SignalAsync("i1", SignalType.Pause);
        var bodies = fake.Requests.Select(r => r.Body).ToArray();
        Assert.Equal("{\"signal_type\":{\"custom\":\"approve\"},\"payload\":{\"by\":\"alice\"}}", bodies[0]);
        Assert.Equal("{\"signal_type\":\"cancel\"}", bodies[1]);
        Assert.Equal("{\"signal_type\":\"pause\"}", bodies[2]);
        Assert.Equal("sig", res.SignalId);
        Assert.Equal(SignalType.Custom("approve"), JsonSerializer.Deserialize<SignalType>("{\"custom\":\"approve\"}", Orch8Json.Options));
        Assert.Equal(SignalType.UpdateContext, JsonSerializer.Deserialize<SignalType>("\"update_context\"", Orch8Json.Options));
    }

    [Fact]
    public async Task InstanceCreateSurfacesDeduplicated()
    {
        var (client, fake) = Create(_ => FakeHttp.Json(200, "{\"id\":\"i1\",\"deduplicated\":true}"));
        var res = await client.Instances.CreateAsync(new CreateInstanceRequest { SequenceId = "s", TenantId = "t-1", IdempotencyKey = "dup" });
        Assert.True(res.Deduplicated);
        var body = fake.Requests.Single().Json!.AsObject();
        Assert.Equal("default", body["namespace"]!.GetValue<string>());
        Assert.False(body.ContainsKey("context"));
    }

    [Theory]
    [InlineData(400, "invalid_argument", typeof(Orch8BadRequestException))]
    [InlineData(401, "unauthorized", typeof(Orch8UnauthorizedException))]
    [InlineData(403, "forbidden", typeof(Orch8ForbiddenException))]
    [InlineData(404, "not_found", typeof(Orch8NotFoundException))]
    [InlineData(409, "already_exists", typeof(Orch8ConflictException))]
    [InlineData(413, "payload_too_large", typeof(Orch8PayloadTooLargeException))]
    [InlineData(422, "unprocessable_entity", typeof(Orch8UnprocessableException))]
    [InlineData(429, "rate_limited", typeof(Orch8RateLimitedException))]
    [InlineData(500, "internal", typeof(Orch8ServerException))]
    [InlineData(503, "unavailable", typeof(Orch8ServerException))]
    [InlineData(418, "teapot", typeof(Orch8ApiException))]
    public async Task ErrorsAreTypedFromTheEnvelope(int status, string code, Type expected)
    {
        var (client, _) = Create(_ => FakeHttp.Error(status, code, "went wrong"), maxAttempts: 1);
        var e = await Assert.ThrowsAnyAsync<Orch8ApiException>(() => client.Jobs.GetAsync("x"));
        Assert.IsType(expected, e);
        Assert.Equal(status, e.Status);
        Assert.Equal(code, e.Code);
        Assert.Equal("went wrong", e.Message);
    }

    [Fact]
    public async Task NonEnvelopeErrorBodyStillProducesTypedError()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway) { Content = new StringContent("upstream down") }, maxAttempts: 1);
        var e = await Assert.ThrowsAsync<Orch8ServerException>(() => client.Jobs.GetAsync("x"));
        Assert.Null(e.Code);
        Assert.Equal("upstream down", e.Message);
    }

    // fixtures/transport.json
    [Fact]
    public async Task SafeRequestRecoversAfterTransientResponses()
    {
        var statuses = new Queue<int>(new[] { 429, 503, 200 });
        var (client, fake) = Create(_ =>
        {
            var s = statuses.Dequeue();
            return s == 200 ? FakeHttp.Json(200, "{\"id\":\"j\"}") : FakeHttp.Error(s, "x", "transient");
        });
        Assert.Equal("j", (await client.Jobs.GetAsync("j")).Id);
        Assert.Equal(3, fake.Requests.Count);
    }

    [Fact]
    public async Task SafeRequestGivesUpAfterMaxAttempts()
    {
        var (client, fake) = Create(_ => FakeHttp.Error(503, "unavailable", "down"), maxAttempts: 2);
        await Assert.ThrowsAsync<Orch8ServerException>(() => client.Jobs.GetAsync("j"));
        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task UnsafeRequestIsNeverReplayed()
    {
        var (client, fake) = Create(_ => FakeHttp.Error(503, "unavailable", "down"));
        await Assert.ThrowsAsync<Orch8ServerException>(() => client.Jobs.EnqueueAsync("h", new { }));
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task NonRetryableStatusIsNotRetried()
    {
        var (client, fake) = Create(_ => FakeHttp.Error(404, "not_found", "nope"));
        await Assert.ThrowsAsync<Orch8NotFoundException>(() => client.Jobs.GetAsync("j"));
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task ProtocolRelativePathIsRejected()
    {
        var (client, fake) = Create(_ => FakeHttp.Json(200, "{}"));
        await Assert.ThrowsAsync<Orch8InvalidPathException>(() => client.SendAsync<JsonNode>(HttpMethod.Get, "//untrusted.test/path"));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task EmptySuccessBodyMapsToNoValue()
    {
        var (client, _) = Create(_ => FakeHttp.Empty(204));
        Assert.Null(await client.SendAsync<JsonNode>(HttpMethod.Get, "/anything"));
    }

    [Fact]
    public async Task ConnectionFailuresAreTransportErrorsAndRetriedForGet()
    {
        var calls = 0;
        var fake = new FakeHttp(_ =>
        {
            calls++;
            if (calls < 3) throw new HttpRequestException("connection refused");
            return Task.FromResult(FakeHttp.Json(200, "{\"id\":\"j\"}"));
        });
        using var client = new Orch8Client(new Orch8ClientOptions { BaseUrl = Base, HttpMessageHandler = fake, RetryBaseDelay = TimeSpan.FromMilliseconds(1) });
        Assert.Equal("j", (await client.Jobs.GetAsync("j")).Id);
        Assert.Equal(3, calls);

        var failing = new FakeHttp((Func<Recorded, HttpResponseMessage>)(_ => throw new HttpRequestException("connection refused")));
        using var client2 = new Orch8Client(new Orch8ClientOptions { BaseUrl = Base, HttpMessageHandler = failing, RetryBaseDelay = TimeSpan.FromMilliseconds(1) });
        await Assert.ThrowsAsync<Orch8TransportException>(() => client2.Jobs.EnqueueAsync("h", null));
        Assert.Single(failing.Requests);
    }

    [Fact]
    public async Task RequestTimeoutIsATransportError()
    {
        using var client = new Orch8Client(new Orch8ClientOptions { BaseUrl = Base, HttpMessageHandler = new DelayHandler(), MaxAttempts = 1, Timeout = TimeSpan.FromMilliseconds(50) });
        await Assert.ThrowsAsync<Orch8TransportException>(() => client.Jobs.GetAsync("j"));
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var client = new Orch8Client(new Orch8ClientOptions { BaseUrl = Base, HttpMessageHandler = new DelayHandler() });
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.Jobs.GetAsync("j", cts.Token));
    }

    [Fact]
    public void EnvironmentConfiguration()
    {
        Environment.SetEnvironmentVariable("ORCH8_BASE_URL", "http://e/api/v1");
        Environment.SetEnvironmentVariable("ORCH8_MAX_ATTEMPTS", "7");
        try
        {
            var o = Orch8ClientOptions.FromEnvironment();
            Assert.Equal("http://e/api/v1", o.BaseUrl);
            Assert.Equal(7, o.MaxAttempts);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORCH8_BASE_URL", null);
            Environment.SetEnvironmentVariable("ORCH8_MAX_ATTEMPTS", null);
        }
    }

    private sealed class DelayHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage();
        }
    }
}
