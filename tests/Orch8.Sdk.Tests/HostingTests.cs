using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orch8.Sdk.Hosting;

namespace Orch8.Sdk.Tests;

public class HostingTests
{
    [Fact]
    public async Task HostedServiceRunsWorkerAndDrainsOnStop()
    {
        var engine = new FakeEngine();
        var task = engine.Enqueue("echo");
        var services = new ServiceCollection();
        services.AddOrch8Worker(
            c => { c.BaseUrl = "http://engine.test/api/v1"; c.HttpMessageHandler = engine.Http; },
            (_, w) => w.Register("echo", _ => Task.FromResult<object?>(new { ok = true })),
            o => { o.WorkerId = "hosted"; o.PollInterval = TimeSpan.FromMilliseconds(10); });
        await using var sp = services.BuildServiceProvider();
        var hosted = Assert.Single(sp.GetServices<IHostedService>());
        Assert.IsType<Orch8WorkerHostedService>(hosted);

        await hosted.StartAsync(CancellationToken.None);
        await FakeEngine.Eventually(() => engine.Acks(task["id"]!.GetValue<string>()).Count == 1);
        await hosted.StopAsync(CancellationToken.None);
        Assert.Equal("hosted", engine.Polls[0].Json!["worker_id"]!.GetValue<string>());
    }
}
