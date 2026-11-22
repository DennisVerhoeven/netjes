using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netjes.Sidecar;
using Netjes.Tests.Endpoints;
using Netjes.TypeGen;

namespace Netjes.Tests.Sidecar;

public sealed class NodeSidecarHostedServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("netjes-hosted-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StopAsync_StopsThePoolItStarted()
    {
        await using var app = await StartAsync();
        var pid = await SidecarPoolTests.PidAsync(app.Services.GetRequiredService<SidecarPool>().Primary);

        await app.StopAsync(Ct);

        await SidecarPoolTests.WaitForExitAsync(pid).WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task StopAsync_AfterTheHostWasDisposed_DoesNotThrow()
    {
        // WebApplicationFactory stops the host twice at once, and the app's own Run() disposes it once its stop returns:
        // the other stop can get to this service after that.
        var app = await StartAsync();
        Assert.Equal(1, app.Services.GetRequiredService<SidecarPool>().ReadyCount);
        var service = app.Services.GetServices<IHostedService>().OfType<NodeSidecarHostedService>().Single();

        await app.DisposeAsync();

        await service.StopAsync(Ct);
    }

    [Fact]
    public async Task StopAsync_InDevelopment_TellsTheSidecarThatTheAppIsStoppingFirst()
    {
        // dotnet watch starts the app again, with the Vite dev server's port, while this one may still be finishing
        // its requests: told first, the sidecar lets go of the port at once, and renders until it is stopped. First
        // means before any hosted service stops, the server's included: told only when its own service stops, the
        // sidecar would hear it after the services the host stops before that one.
        var logs = new ListLoggerProvider();
        var told = new LogSignal("stdin: stopping");
        var probe = new StopProbe(told.Logged);
        await using var app = await StartAsync("Development", "stdin-sidecar.mjs", logs, s =>
        {
            s.AddSingleton<ILoggerProvider>(told);
            s.AddSingleton<IHostedService>(probe);
        });

        await app.StopAsync(Ct);

        Assert.True(probe.ToldBeforeItStopped, "The sidecar read `stopping` only after a hosted service had stopped.");
        Assert.Equal(["stdin: stopping", "stdin ended"], StdinLines(logs));
    }

    [Fact]
    public async Task StopAsync_InProduction_TellsTheSidecarNothing()
    {
        // No Vite dev server, so no port to let go of: the process only sees its stdin close.
        var logs = new ListLoggerProvider();
        await using var app = await StartAsync("Production", "stdin-sidecar.mjs", logs);

        await app.StopAsync(Ct);

        Assert.Equal(["stdin ended"], StdinLines(logs));
    }

    [Fact]
    public async Task StartAsync_WhenTheVitePortIsTaken_SaysSoInOneLineAndStopsTheApp()
    {
        // Started with dotnet run or from an IDE, the app would otherwise fail to start in two stack traces and abort.
        // An app built in this test counts as the program the process runs: its name is the entry assembly's.
        var failing = FailingApp("Development", o => o.SidecarScript = Script("vite-port-taken-sidecar.mjs"));
        await using var app = failing.App;

        await RunAsync(app);

        var failure = Assert.Single(failing.Logs.Entries, e => e.Level >= LogLevel.Error);
        Assert.Equal(LogLevel.Critical, failure.Level);
        Assert.StartsWith("Port 5173 is in use, and the Vite dev server needs it.", failure.Message);
        Assert.Null(failure.Exception);
        Assert.Equal(1, failing.ExitCode);
    }

    [Fact]
    public async Task StartAsync_WhenTheAppCannotStart_SaysWhyAfterTheHostsOwnMessages()
    {
        // Once started, the host says so ("Application started. Press Ctrl+C to shut down."), although the app is
        // already stopping: the reason it stops is the last thing it says, rather than above that. The console
        // lifetime, which dotnet run has, writes those messages.
        var failing = FailingApp(
            "Development",
            o => o.SidecarScript = Script("vite-port-taken-sidecar.mjs"),
            services: s => s.AddSingleton<IHostLifetime, Microsoft.Extensions.Hosting.Internal.ConsoleLifetime>());
        await using var app = failing.App;

        await RunAsync(app);

        var messages = failing.Logs.Entries.Select(e => e.Message).ToList();
        Assert.Contains("Application started. Press Ctrl+C to shut down.", messages);
        Assert.StartsWith("Port 5173 is in use, and the Vite dev server needs it.", messages[^1]);
    }

    [Fact]
    public async Task StartAsync_WhenTheVitePortIsTaken_InAnotherProgramsProcess_FailsWithTheReason()
    {
        // WebApplicationFactory runs the app in the test runner's process, under the app's own name: stopped instead,
        // the app would leave the test a client whose requests fail as the host is disposed, without saying why.
        var failing = FailingApp("Development", o => o.SidecarScript = Script("vite-port-taken-sidecar.mjs"), applicationName: "SomeApp");
        await using var app = failing.App;

        var ex = await Assert.ThrowsAsync<VitePortInUseException>(() => app.StartAsync(Ct));

        Assert.StartsWith("Port 5173 is in use, and the Vite dev server needs it.", ex.Message);
        Assert.False(app.Lifetime.ApplicationStopping.IsCancellationRequested);
        Assert.Null(failing.ExitCode);
    }

    [Fact]
    public async Task StartAsync_WhenNodeCannotBeStarted_SaysSoInOneLineAndStopsTheApp()
    {
        var failing = FailingApp("Development", o =>
        {
            o.SidecarScript = Script("fake-sidecar.mjs");
            o.NodeExecutable = "netjes-test-no-such-node";
        });
        await using var app = failing.App;

        await RunAsync(app);

        var failure = Assert.Single(failing.Logs.Entries, e => e.Level >= LogLevel.Error);
        Assert.Equal(LogLevel.Critical, failure.Level);
        Assert.Equal(
            "Could not start 'netjes-test-no-such-node'. Is Node.js installed and on the PATH? Set Netjes:NodeExecutable " +
            "to use another executable.",
            failure.Message);
        Assert.Null(failure.Exception);
        Assert.Equal(1, failing.ExitCode);
    }

    [Fact]
    public async Task StartAsync_WhenTheViteDevServerDoesNotStart_SaysWhyInOneLineAndStopsTheApp()
    {
        // An error in vite.config, say: the sidecar exits before it is ready, after Node's report of the error.
        var failing = FailingApp("Development", o => o.SidecarScript = Script("vite-failed-sidecar.mjs"));
        await using var app = failing.App;

        await RunAsync(app);

        var failure = Assert.Single(failing.Logs.Entries, e => e.Level >= LogLevel.Error);
        Assert.Equal(LogLevel.Critical, failure.Level);
        Assert.StartsWith("The Vite dev server did not start: Build failed with 1 error:", failure.Message);
        Assert.EndsWith("Fix it, then start the app again.", failure.Message);
        Assert.Null(failure.Exception);
        Assert.Equal(1, failing.ExitCode);
    }

    [Fact]
    public async Task StartAsync_BeforeNpmInstall_SaysSoInOneLineAndStopsTheApp()
    {
        // Development finds the sidecar in node_modules, which a fresh clone does not have yet.
        var failing = FailingApp("Development", _ => { });
        await using var app = failing.App;

        await RunAsync(app);

        var failure = Assert.Single(failing.Logs.Entries, e => e.Level >= LogLevel.Error);
        Assert.Equal(LogLevel.Critical, failure.Level);
        Assert.Equal(
            $"Cannot find the netjes sidecar at '{Path.Combine(_root, "node_modules", "netjes", "sidecar", "server.mjs")}'. " +
            $"Run `npm install` in '{_root}'.",
            failure.Message);
        Assert.Equal(1, failing.ExitCode);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Script(string name) => Path.Combine(AppContext.BaseDirectory, "Sidecar", name);

    /// <summary>Starts the app and waits for it to stop by itself, as <c>Run()</c> does.</summary>
    private static async Task RunAsync(WebApplication app)
    {
        await app.StartAsync(Ct);
        await app.WaitForShutdownAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
    }

    /// <summary>An app that will not start, with its logs and the exit code it sets.</summary>
    private sealed class Failing(WebApplication app, ListLoggerProvider logs, Func<int?> exitCode)
    {
        public WebApplication App { get; } = app;
        public ListLoggerProvider Logs { get; } = logs;
        public int? ExitCode => exitCode();
    }

    private Failing FailingApp(
        string environment, Action<NetjesOptions> configure, string? applicationName = null, Action<IServiceCollection>? services = null)
    {
        int? exitCode = null;
        var logs = new ListLoggerProvider();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
            ContentRootPath = _root,
            ApplicationName = applicationName,
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders().AddProvider(logs);
        builder.Services.AddSingleton(new CommandExit(code => exitCode = code));
        builder.Services.AddNetjes(o =>
        {
            o.TypesFile = null;
            configure(o);
        });
        services?.Invoke(builder.Services);
        return new Failing(builder.Build(), logs, () => exitCode);
    }

    /// <summary>A log sink whose <see cref="Logged"/> completes once a message equal to <paramref name="message"/> is logged.</summary>
    private sealed class LogSignal(string message) : ILoggerProvider
    {
        private readonly TaskCompletionSource _logged = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Logged => _logged.Task;

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private void Saw(string logged)
        {
            if (logged == message)
            {
                _logged.TrySetResult();
            }
        }

        private sealed class Logger(LogSignal signal) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                signal.Saw(formatter(state, exception));
        }
    }

    /// <summary>
    /// A hosted service registered after Netjes's, so the host stops it before the sidecar's: hosted services stop one
    /// at a time, in the reverse order of their registration. Its stop waits, ten seconds at most, for
    /// <paramref name="told"/>.
    /// </summary>
    private sealed class StopProbe(Task told) : IHostedService
    {
        /// <summary>Whether <c>told</c> completed before this service's stop returned.</summary>
        public bool ToldBeforeItStopped { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await told.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                ToldBeforeItStopped = true;
            }
            catch (TimeoutException)
            {
                // Not yet: the sidecar's own stop, which comes after this one, would be the first to tell it.
            }
        }
    }

    /// <summary>What the sidecar of <c>stdin-sidecar.mjs</c> said it read on its stdin, as its output was logged.</summary>
    private static List<string> StdinLines(ListLoggerProvider logs) =>
        [.. logs.Entries.Select(e => e.Message).Where(m => m.StartsWith("stdin", StringComparison.Ordinal))];

    /// <summary>
    /// A started app in <paramref name="environment"/>, with the sidecar script <paramref name="script"/>, and what
    /// <paramref name="services"/> registers after Netjes's own services.
    /// </summary>
    private async Task<WebApplication> StartAsync(
        string environment, string script, ListLoggerProvider logs, Action<IServiceCollection>? services = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment, ContentRootPath = _root });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders().AddProvider(logs);
        builder.Services.AddNetjes(o =>
        {
            o.SidecarScript = Script(script);
            o.TypesFile = null;
        });
        services?.Invoke(builder.Services);
        var app = builder.Build();
        await app.StartAsync(Ct);
        return app;
    }

    private async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production", ContentRootPath = _root });
        builder.WebHost.UseTestServer();
        builder.Services.AddNetjes(o =>
        {
            o.SidecarScript = Path.Combine(AppContext.BaseDirectory, "Sidecar", "fake-sidecar.mjs");
            o.Mode = NetjesMode.Production;
            o.TypesFile = null;
        });
        var app = builder.Build();
        await app.StartAsync(Ct);
        return app;
    }
}
