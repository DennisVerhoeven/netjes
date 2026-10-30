using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Netjes.Rendering;
using Netjes.Sidecar;

namespace Netjes.Tests.Sidecar;

public class SidecarPoolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SidecarPool Create(
        int processes, NetjesMode mode = NetjesMode.Production, Action<Process>? killTree = null, Action<NetjesOptions>? configure = null)
    {
        var options = new NetjesOptions
        {
            Mode = mode,
            SidecarScript = Path.Combine(AppContext.BaseDirectory, "Sidecar", "fake-sidecar.mjs"),
            SidecarProcesses = processes,
            RenderTimeout = TimeSpan.FromSeconds(10),
            StartupTimeout = TimeSpan.FromSeconds(20),
        };
        configure?.Invoke(options);
        var environment = new HostingEnvironment { ContentRootPath = AppContext.BaseDirectory, EnvironmentName = "Production" };
        return new SidecarPool(new NetjesContext(Options.Create(options), environment), NullLoggerFactory.Instance, killTree);
    }

    [Fact]
    public async Task RenderAsync_SpreadsConcurrentRendersOverTheProcesses()
    {
        await using var pool = Create(processes: 2);
        await pool.StartAsync(Ct);

        var pages = await Task.WhenAll(
            pool.RenderAsync("""{"page":"pid"}""", Ct),
            pool.RenderAsync("""{"page":"pid"}""", Ct));

        Assert.Equal(2, pages.Distinct().Count());
    }

    [Fact]
    public async Task RenderAsync_PrefersTheProcessWithFewerRendersInFlight()
    {
        await using var pool = Create(processes: 2);
        await pool.StartAsync(Ct);
        await using var streaming = await pool.RenderStreamAsync("""{"page":"deferred"}""", new SsrStreamOptions(false, "r1"), Ct);

        var pids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            pids.Add(await pool.RenderAsync("""{"page":"pid"}""", Ct));
        }

        Assert.Single(pids.Distinct());
        await pool.ResolveAsync("r1", "d0", "1", null, Ct);
    }

    [Fact]
    public async Task ResolveAsync_ReachesTheProcessRenderingThatPage()
    {
        await using var pool = Create(processes: 2);
        await pool.StartAsync(Ct);

        // The second render goes to the other process, since the first is still streaming.
        await using var first = await pool.RenderStreamAsync("""{"page":"deferred"}""", new SsrStreamOptions(false, "r1"), Ct);
        await using var second = await pool.RenderStreamAsync("""{"page":"deferred"}""", new SsrStreamOptions(false, "r2"), Ct);
        await pool.ResolveAsync("r2", "d0", "2", null, Ct);
        await pool.ResolveAsync("r1", "d0", "1", null, Ct);

        Assert.Equal("<html>shell d0=1</html>", await new StreamReader(first).ReadToEndAsync(Ct));
        Assert.Equal("<html>shell d0=2</html>", await new StreamReader(second).ReadToEndAsync(Ct));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ResolveAsync_AsSoonAsTheRenderIsAskedFor_ReachesIt(int processes)
    {
        // PageRenderer asks for the render, then delivers values while it is still starting (complete documents need
        // them first): the render has its process from the call on.
        await using var pool = Create(processes);
        await pool.StartAsync(Ct);

        var rendering = pool.RenderStreamAsync("""{"page":"deferred"}""", new SsrStreamOptions(false, "r1"), Ct);
        await pool.ResolveAsync("r1", "d0", "42", null, Ct);
        await using var stream = await rendering;

        Assert.Equal("<html>shell d0=42</html>", await new StreamReader(stream).ReadToEndAsync(Ct).WaitAsync(TimeSpan.FromSeconds(3), Ct));
    }

    [Fact]
    public async Task RenderAsync_WhileOneProcessRestarts_UsesAReadyOne()
    {
        await using var pool = Create(processes: 2);
        await pool.StartAsync(Ct);

        await CrashAsync(pool.Primary);
        // The crashed process comes back after a second; none of these wait for it.
        var renders = Task.Run(async () =>
        {
            for (var i = 0; i < 4; i++)
            {
                Assert.Equal("<html>app/page.tsx production</html>", await pool.RenderAsync("""{"page":"app/page.tsx"}""", Ct));
            }
        }, Ct);

        await renders.WaitAsync(TimeSpan.FromMilliseconds(800), Ct);
    }

    [Fact]
    public async Task RenderAsync_ToAProcessWhoseCrashIsNotNoticedYet_GoesToAReadyOne()
    {
        NetjesOptions? options = null;
        await using var pool = Create(processes: 2, configure: o => options = o);
        await pool.StartAsync(Ct);
        var ready = await CrashUnnoticedWithoutARestartAsync(pool, options!);

        // The crashed process looks ready until its exit is noticed, and refuses the renders that pick it then. Each
        // goes to the ready process; one that waited for the restart would fail.
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(ready, await pool.RenderAsync("""{"page":"pid"}""", Ct));
        }
    }

    [Fact]
    public async Task RenderStreamAsync_WithoutDeferredValues_ToAProcessWhoseCrashIsNotNoticedYet_GoesToAReadyOne()
    {
        NetjesOptions? options = null;
        await using var pool = Create(processes: 2, configure: o => options = o);
        await pool.StartAsync(Ct);
        var ready = await CrashUnnoticedWithoutARestartAsync(pool, options!);

        for (var i = 0; i < 4; i++)
        {
            await using var stream = await pool.RenderStreamAsync("""{"page":"pid"}""", new SsrStreamOptions(false), Ct);
            Assert.Equal(ready, await new StreamReader(stream).ReadToEndAsync(Ct));
        }
    }

    /// <summary>
    /// Crashes the first process as .NET can see a crash under load (its port is gone at once, its exit is noticed
    /// half a second later), and keeps it from coming back: its restart finds no Node to start. A render that waits for
    /// the restart then fails at the render timeout, rather than being answered late, however long a refused
    /// connection takes (on Windows, a second or two). Returns what the other process answers to <c>pid</c>.
    /// </summary>
    private static async Task<string> CrashUnnoticedWithoutARestartAsync(SidecarPool pool, NetjesOptions options)
    {
        // Started together, the two go to different processes.
        var pids = await Task.WhenAll(pool.RenderAsync("""{"page":"pid"}""", Ct), pool.RenderAsync("""{"page":"pid"}""", Ct));
        var crashed = await pool.Primary.RenderAsync("""{"page":"pid"}""", Ct);
        options.NodeExecutable = "netjes-test-no-such-node";
        await Assert.ThrowsAsync<SsrRenderException>(() => pool.Primary.RenderAsync("""{"page":"exit-slowly"}""", Ct));
        return Assert.Single(pids, pid => pid != crashed);
    }

    [Fact(Timeout = NodeSidecarTests.LingeringChildTimeout)]
    public async Task RenderAsync_AfterAProcessHasExited_GoesToAReadyOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var pool = Create(processes: 2);
        await pool.StartAsync(ct);
        var pid = await PidAsync(pool.Primary);

        // Its restart waits for an output a child keeps open. Whether the exit handler or a render that picks the
        // process notices the exit first, the process is out of the ready ones from then on.
        await Assert.ThrowsAsync<SsrRenderException>(() => pool.Primary.RenderAsync("""{"page":"exit-leaving-child"}""", ct));
        await WaitForExitAsync(pid);
        var renders = Task.Run(async () =>
        {
            for (var i = 0; i < 4; i++)
            {
                Assert.Equal("<html>app/page.tsx production</html>", await pool.RenderAsync("""{"page":"app/page.tsx"}""", ct));
            }
        }, ct);

        // Its restart comes three seconds after the exit; none of these wait for it.
        await renders.WaitAsync(TimeSpan.FromMilliseconds(800), ct);
    }

    [Fact(Timeout = NodeSidecarTests.LingeringChildTimeout)]
    public async Task Crash_TakesTheProcessOutOfTheReadyOnes_BeforeItsOutputEnds()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var pool = Create(processes: 2);
        await pool.StartAsync(ct);
        var check = new NodeSidecarHealthCheck(pool);
        var pid = await PidAsync(pool.Primary);

        // A child keeps the output open, so the exit handler waits the full two seconds for it to end.
        await Assert.ThrowsAsync<SsrRenderException>(() => pool.Primary.RenderAsync("""{"page":"exit-leaving-child"}""", ct));
        await WaitForExitAsync(pid);
        var seen = System.Diagnostics.Stopwatch.StartNew();
        while (pool.ReadyCount == 2 && seen.Elapsed < TimeSpan.FromSeconds(1))
        {
            await Task.Delay(10, ct);
        }

        Assert.Equal(1, pool.ReadyCount);
        Assert.Equal(HealthStatus.Degraded, (await check.CheckHealthAsync(new HealthCheckContext(), ct)).Status);
    }

    /// <summary>
    /// Crashes the process and waits until .NET has seen it exit, which is after its port has closed. A render sent
    /// before then can still be queued by the dying process and reset with it, and such a render is not sent again.
    /// </summary>
    internal static async Task CrashAsync(NodeSidecar sidecar)
    {
        var pid = await PidAsync(sidecar);
        await Assert.ThrowsAsync<SsrRenderException>(() => sidecar.RenderAsync("""{"page":"crash"}""", Ct));
        await WaitForExitAsync(pid);
    }

    internal static async Task<int> PidAsync(NodeSidecar sidecar) =>
        int.Parse((await sidecar.RenderAsync("""{"page":"pid"}""", Ct))["<html>".Length..^"</html>".Length], System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Waits until the process is gone, which is when .NET reports it exited.</summary>
    internal static async Task WaitForExitAsync(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            await process.WaitForExitAsync(Ct);
        }
        catch (ArgumentException)
        {
            // Gone already.
        }
    }

    [Fact]
    public async Task ResolveAsync_ForARenderOnAProcessWhoseCrashIsNotNoticedYet_ReachesIt()
    {
        await using var pool = Create(processes: 2);
        await pool.StartAsync(Ct);
        await Assert.ThrowsAsync<SsrRenderException>(() => pool.RenderAsync("""{"page":"exit-slowly"}""", Ct));

        // r1 goes to the ready process and r2 to the crashed one, where its value is sent too: r2 waits for the restart.
        var documents = new[] { "r1", "r2" }.Select(async renderId =>
        {
            var rendering = pool.RenderStreamAsync("""{"page":"deferred"}""", new SsrStreamOptions(false, renderId), Ct);
            await pool.ResolveAsync(renderId, "d0", $"\"{renderId}\"", null, Ct);
            await using var stream = await rendering;
            return await new StreamReader(stream).ReadToEndAsync(Ct);
        }).ToArray();

        Assert.Equal(
            ["""<html>shell d0="r1"</html>""", """<html>shell d0="r2"</html>"""],
            await Task.WhenAll(documents).WaitAsync(TimeSpan.FromSeconds(8), Ct));
    }

    [Fact]
    public async Task DisposeAsync_WithoutAStop_StopsItsProcessesAtOnce()
    {
        // A host disposed without being stopped. Each process is slow to exit, so its stop waits out the three-second
        // grace and then kills it. Each kill first waits for all three stops to get that far, which they do together
        // only if they wait out their graces together: one after another, the first kill waits for stops not begun.
        using var killing = new CountdownEvent(3);
        var gaveUp = 0;
        var pool = Create(processes: 3, killTree: process =>
        {
            killing.Signal();
            // Not for ever, so that a pool disposing them one after another still ends its processes, and fails.
            if (!killing.Wait(TimeSpan.FromSeconds(10)))
            {
                Interlocked.Increment(ref gaveUp);
            }

            process.Kill(entireProcessTree: true);
        });
        await pool.StartAsync(Ct);
        // Started together, each goes to a process with none in flight.
        var answers = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => pool.RenderAsync("""{"page":"keep-running"}""", Ct)));
        Assert.Equal(3, answers.Distinct().Count());

        await pool.DisposeAsync();

        // Every process got as far as the kill, and no kill had to wait out the others.
        Assert.True(killing.IsSet);
        Assert.Equal(0, gaveUp);
    }

    [Fact]
    public async Task Development_RunsOneProcess_TheOneHostingVite()
    {
        await using var pool = Create(processes: 4, NetjesMode.Development);

        Assert.Equal(1, pool.Count);
    }

    [Theory]
    [InlineData(1, HealthStatus.Unhealthy, "The Node sidecar is starting or restarting.")]
    [InlineData(2, HealthStatus.Degraded, "1 of 2 Node sidecars ready; the others are restarting.")]
    public async Task Refusal_TakesTheProcessOutOfTheReadyOnes(int processes, HealthStatus status, string description)
    {
        await using var pool = Create(processes);
        await pool.StartAsync(Ct);
        var check = new NodeSidecarHealthCheck(pool);

        // The process keeps running: only the refusal can take it out.
        await pool.Primary.RenderAsync("""{"page":"stop-listening"}""", Ct);
        Assert.Null(await pool.Primary.TryRenderAsync("""{"page":"app/page.tsx"}""", Ct));

        Assert.Equal(processes - 1, pool.ReadyCount);
        var health = await check.CheckHealthAsync(new HealthCheckContext(), Ct);
        Assert.Equal(status, health.Status);
        Assert.Equal(description, health.Description);
    }

    [Fact]
    public async Task Health_IsDegradedWhileSomeProcessesAreDown()
    {
        await using var pool = Create(processes: 2);
        var check = new NodeSidecarHealthCheck(pool);
        Assert.Equal(HealthStatus.Unhealthy, (await check.CheckHealthAsync(new HealthCheckContext(), Ct)).Status);
        await pool.StartAsync(Ct);
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext(), Ct)).Status);

        await Assert.ThrowsAsync<SsrRenderException>(() => pool.RenderAsync("""{"page":"crash"}""", Ct));
        // The exit is noticed a moment after the render fails; the restart only comes after a second.
        var health = await check.CheckHealthAsync(new HealthCheckContext(), Ct);
        for (var waited = 0; health.Status != HealthStatus.Degraded && waited < 800; waited += 20)
        {
            await Task.Delay(20, Ct);
            health = await check.CheckHealthAsync(new HealthCheckContext(), Ct);
        }

        Assert.Equal(HealthStatus.Degraded, health.Status);
        Assert.Equal("1 of 2 Node sidecars ready; the others are restarting.", health.Description);
    }
}
