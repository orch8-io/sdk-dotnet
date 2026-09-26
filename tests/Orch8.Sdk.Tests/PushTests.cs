using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orch8.Sdk.Tests;

public class PushTests
{
    public static IEnumerable<object?[]> Vectors()
    {
        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "push_signatures.json")))!;
        foreach (var c in doc["cases"]!.AsArray())
        {
            yield return new object?[]
            {
                c!["name"]!.GetValue<string>(),
                c["secret"]!.GetValue<string>(),
                c["timestamp"]?.GetValue<string>(),
                c["signature"]?.GetValue<string>(),
                c["body"]!.GetValue<string>(),
                c["now"]!.GetValue<long>(),
                c["tolerance_secs"]?.GetValue<int>() ?? 300,
                c["valid"]!.GetValue<bool>(),
            };
        }
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void SignatureVectors(string name, string secret, string? ts, string? sig, string body, long now, int tolerance, bool valid)
    {
        var result = Orch8PushVerifier.Verify(secret, ts, sig, Encoding.UTF8.GetBytes(body),
            DateTimeOffset.FromUnixTimeSeconds(now), TimeSpan.FromSeconds(tolerance));
        Assert.True(valid == result, name);
    }

    [Fact]
    public void SignRoundTrips()
    {
        var body = Encoding.UTF8.GetBytes("{\"a\":1}");
        var sig = Orch8PushVerifier.Sign("s3cret", "1767225600", body);
        Assert.StartsWith("sha256=", sig);
        Assert.Equal(71, sig.Length);
        Assert.True(Orch8PushVerifier.Verify("s3cret", "1767225600", sig, body, DateTimeOffset.FromUnixTimeSeconds(1767225600)));
        Assert.False(Orch8PushVerifier.Verify("s3cret", "1767225600", sig, body)); // stale vs. real clock
    }

    [Fact]
    public void EmptySecretIsAConfigurationError()
    {
        Assert.Throws<ArgumentException>(() => Orch8PushVerifier.Verify("", "1", "sha256=00", Array.Empty<byte>()));
    }

    [Fact]
    public void RejectsMalformedTimestamps()
    {
        var body = Encoding.UTF8.GetBytes("{}");
        foreach (var ts in new[] { "+1767225600", " 1767225600", "1767225600.0", "-", "99999999999999999999999" })
        {
            var sig = Orch8PushVerifier.Sign("s", ts, body);
            Assert.False(Orch8PushVerifier.Verify("s", ts, sig, body, DateTimeOffset.FromUnixTimeSeconds(1767225600)), ts);
        }
    }

    [Fact]
    public async Task ReceiverRejectsBadSignaturesAndClaimsOnValidPush()
    {
        var engine = new FakeEngine();
        var task = engine.Enqueue("echo", new JsonObject { ["via"] = "push" });
        await using var worker = engine.CreateWorker();
        worker.Register("echo", ctx => Task.FromResult<object?>(new { echo = ctx.Params }));
        var now = DateTimeOffset.FromUnixTimeSeconds(1767225600);
        var receiver = new Orch8PushReceiver(worker, "whsec", clock: () => now);
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { task_id = task["id"]!.GetValue<string>(), handler_name = "echo", queue_name = "push-q" }));
        var ts = "1767225600";

        Assert.Equal(401, receiver.Handle(ts, null, body));
        Assert.Equal(401, receiver.Handle(ts, Orch8PushVerifier.Sign("wrong", ts, body), body));
        Assert.Equal(401, receiver.Handle("1767220000", Orch8PushVerifier.Sign("whsec", "1767220000", body), body));
        var tampered = (byte[])body.Clone();
        tampered[^2] ^= 1;
        Assert.Equal(401, receiver.Handle(ts, Orch8PushVerifier.Sign("whsec", ts, body), tampered));
        var garbage = Encoding.UTF8.GetBytes("not json");
        Assert.Equal(400, receiver.Handle(ts, Orch8PushVerifier.Sign("whsec", ts, garbage), garbage));
        await Task.Delay(100);
        Assert.Empty(engine.Polls);

        Assert.Equal(202, receiver.Handle(ts, Orch8PushVerifier.Sign("whsec", ts, body), body));
        var id = task["id"]!.GetValue<string>();
        await FakeEngine.Eventually(() => engine.Acks(id).Count == 1);
        var poll = Assert.Single(engine.Polls);
        Assert.Equal("/api/v1/workers/tasks/poll/queue", poll.RawPath);
        Assert.Equal("push-q", poll.Json!["queue_name"]!.GetValue<string>());
        Assert.Equal(1, poll.Json!["limit"]!.GetValue<int>());
        Assert.Equal("{\"echo\":{\"via\":\"push\"}}", engine.Acks(id).Single().Json!["output"]!.ToJsonString());
    }

    [Fact]
    public async Task ReceiverDoesNotClaimForUnregisteredHandler()
    {
        var engine = new FakeEngine();
        await using var worker = engine.CreateWorker();
        worker.Register("echo", _ => Task.CompletedTask);
        var receiver = new Orch8PushReceiver(worker, "whsec");
        Assert.Equal(0, await receiver.ClaimAsync(new PushEnvelope { HandlerName = "other", QueueName = "q" }));
        Assert.Empty(engine.Polls);
    }

    [Fact]
    public void EnvelopeParsesAndKeepsUnknownFields()
    {
        var env = PushEnvelope.Parse(Encoding.UTF8.GetBytes("{\"task_id\":\"t\",\"handler_name\":\"h\",\"queue_name\":\"q\",\"attempt\":2,\"new_field\":true}"));
        Assert.Equal("h", env.HandlerName);
        Assert.Equal("q", env.QueueName);
        Assert.Equal(2, env.Attempt);
        Assert.True(env.ExtensionData!.ContainsKey("new_field"));
    }
}
