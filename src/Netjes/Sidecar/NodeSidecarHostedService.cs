using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netjes.Rendering;
using Netjes.TypeGen;

namespace Netjes.Sidecar;

/// <summary>
/// Starts the sidecar with the host, unless SSR has been replaced by another <see cref="ISsrRenderer"/> or the app
/// runs a tooling command that renders nothing (<c>generate</c>, <c>routes</c>; <c>export</c> renders every page), and
/// stops the pool it started. That one is kept: a host stopped twice at once (WebApplicationFactory does) can be
/// disposed before the second stop gets here, when the container no longer hands out services.
/// </summary>
/// <remarks>
/// When the sidecar cannot start for a reason its message says in full (a <see cref="NetjesStartupException"/>: Node.js
/// or the sidecar script is not there, or the Vite dev server does not start), an app that is the program its process
/// runs stops with exit code 1 and one line saying so, rather than fail to start: a hosted service that fails its start
/// makes the host log the exception with its stack, and <c>Run()</c> throw it again, unhandled, which aborts the
/// process. The stop waits until the host has started, since stopping during the start cancels the server's own start,
/// which fails the same way. The line waits until the host stops: once started, the host says so ("Application
/// started. Press Ctrl+C to shut down.") although the app is stopping already, and the reason is then the last thing
/// the app says. An app run in another program's process, as WebApplicationFactory runs it in a test's, fails to start
/// with the exception instead: stopped, it would leave the test a client whose requests fail once the host is
/// disposed, without saying why.
/// </remarks>
internal sealed class NodeSidecarHostedService(IServiceProvider services) : IHostedLifecycleService
{
    private SidecarPool? _started;
    private NetjesStartupException? _failure;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (services.GetRequiredService<NetjesContext>().Command is not (null or { Name: "export" })
            || services.GetRequiredService<ISsrRenderer>() is not SidecarPool pool)
        {
            return;
        }

        // Before it starts: a start that fails or is cancelled can leave processes running.
        _started = pool;
        try
        {
            await pool.StartAsync(cancellationToken);
        }
        catch (NetjesStartupException ex) when (services.GetRequiredService<NetjesContext>().IsTheProgram)
        {
            _failure = ex;
            services.GetRequiredService<PendingStartupFailure>().Exception = ex;
        }
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        if (_failure is not null)
        {
            services.GetRequiredService<CommandExit>().Set(1);
            services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        }

        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        // Once, however often the host is stopped; services only then, since a later stop can come after the dispose.
        if (Interlocked.Exchange(ref _failure, null) is { } failure)
        {
            services.GetRequiredService<ILogger<NodeSidecarHostedService>>().LogCritical("{Message}", failure.Message);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => _started?.StopAsync() ?? Task.CompletedTask;

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class NodeSidecarHealthCheck(ISsrRenderer renderer) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(renderer switch
        {
            SidecarPool pool when pool.ReadyCount == pool.Count =>
                HealthCheckResult.Healthy(pool.Count == 1 ? "The Node sidecar is ready." : $"All {pool.Count} Node sidecars are ready."),
            SidecarPool { ReadyCount: 0 } => HealthCheckResult.Unhealthy("The Node sidecar is starting or restarting."),
            // Still serving: renders go to the ready ones.
            SidecarPool pool => HealthCheckResult.Degraded($"{pool.ReadyCount} of {pool.Count} Node sidecars ready; the others are restarting."),
            _ => HealthCheckResult.Healthy("Server rendering does not use the Node sidecar."),
        });
}
