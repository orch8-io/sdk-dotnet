using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Orch8.Sdk.Hosting;

/// <summary>
/// Runs an <see cref="Orch8Worker"/> for the lifetime of the host. On host shutdown polling stops
/// immediately and in-flight tasks are drained for up to <see cref="Orch8WorkerOptions.ShutdownTimeout"/>
/// (raise <c>HostOptions.ShutdownTimeout</c> accordingly).
/// </summary>
public sealed class Orch8WorkerHostedService : BackgroundService
{
    private readonly Orch8Worker _worker;

    /// <summary>Creates the hosted service for <paramref name="worker"/>.</summary>
    public Orch8WorkerHostedService(Orch8Worker worker) => _worker = worker ?? throw new ArgumentNullException(nameof(worker));

    /// <summary>The hosted worker.</summary>
    public Orch8Worker Worker => _worker;

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _worker.RunAsync(stoppingToken);
}

/// <summary>DI registration helpers.</summary>
public static class Orch8ServiceCollectionExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="Orch8Client"/>, a singleton
    /// <see cref="Orch8Worker"/> configured by <paramref name="configureWorker"/> (register handlers there),
    /// and a hosted service that runs it.
    /// </summary>
    public static IServiceCollection AddOrch8Worker(
        this IServiceCollection services,
        Action<Orch8ClientOptions> configureClient,
        Action<IServiceProvider, Orch8Worker> configureWorker,
        Action<Orch8WorkerOptions>? configureWorkerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureClient);
        ArgumentNullException.ThrowIfNull(configureWorker);
        services.AddSingleton(_ =>
        {
            var options = new Orch8ClientOptions();
            configureClient(options);
            return new Orch8Client(options);
        });
        services.AddSingleton(sp =>
        {
            var options = new Orch8WorkerOptions();
            configureWorkerOptions?.Invoke(options);
            var worker = new Orch8Worker(sp.GetRequiredService<Orch8Client>(), options);
            configureWorker(sp, worker);
            return worker;
        });
        services.AddHostedService(sp => new Orch8WorkerHostedService(sp.GetRequiredService<Orch8Worker>()));
        return services;
    }
}
