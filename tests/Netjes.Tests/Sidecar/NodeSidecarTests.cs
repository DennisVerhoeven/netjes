using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Sockets;
using Netjes.Rendering;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Netjes.Sidecar;
using Netjes.Tests.Endpoints;

namespace Netjes.Tests.Sidecar;

public class NodeSidecarTests
{
    /// <summary>
    /// For tests whose process leaves a child holding its output: an exit handler that blocks until that output ends
    /// hangs them for good, Dispose included, so they fail after this long instead.
    /// </summary>
    internal const int LingeringChildTimeout = 30_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NodeSidecar Create(
        string script,
        TimeSpan? renderTimeout = null,
        Action<NetjesOptions>? configure = null,
        HttpMessageHandler? handler = null,
        ILoggerFactory? loggers = null,
        Action<Process>? killTree = null)
    {
        var options = new NetjesOptions
        {
            Mode = NetjesMode.Production,
            SidecarScript = Path.IsPathRooted(script) ? script : Path.Combine(AppContext.BaseDirectory, "Sidecar", script),
            RenderTimeout = renderTimeout ?? TimeSpan.FromSeconds(10),
            StartupTimeout = TimeSpan.FromSeconds(20),
        };
        configure?.Invoke(options);
        var environment = new HostingEnvironment { ContentRootPath = AppContext.BaseDirectory, EnvironmentName = "Production" };
        return new NodeSidecar(
            new NetjesContext(Options.Create(options), environment), (loggers ?? NullLoggerFactory.Instance).CreateLogger<NodeSidecar>(), handler,
            killTree);
    }

    [Fact]
    public async Task StartAsync_ThenRenderAsync_ReturnsTheDocumentFromNode()
    {
        await using var sidecar = Create("fake-sidecar.mjs");

        await sidecar.StartAsync(Ct);
        var html = await sidecar.RenderAsync("""{"page":"app/page.tsx"}""", Ct);

        Assert.True(sidecar.IsReady);
        Assert.Equal("<html>app/page.tsx production</html>", html);
    }

    private static async Task<NodeSidecar> StartFakeAsync(Action<NetjesOptions>? configure = null)
    {
        var sidecar = Create("fake-sidecar.mjs", configure: configure);
        await sidecar.StartAsync(Ct);
        return sidecar;
    }

    [Fact]
    public async Task RenderStreamAsync_HandsOverTheShellBeforeTheRestArrives()
    {
        await using var sidecar = await StartFakeAsync();
        var started = System.Diagnostics.Stopwatch.StartNew();

        await using var stream = await sidecar.RenderStreamAsync("""{"page":"stream"}""", new SsrStreamOptions(WaitForAll: true), Ct);
        var buffer = new byte[256];
        var first = System.Text.Encoding.UTF8.GetString(buffer, 0, await stream.ReadAsync(buffer, Ct));
        var firstAfter = started.Elapsed;
        var rest = await new StreamReader(stream).ReadToEndAsync(Ct);

        Assert.Equal("<html>shell wait=1", first);
        Assert.True(firstAfter < TimeSpan.FromSeconds(1.2), $"first chunk after {firstAfter}");
        Assert.Equal(" rest</html>", rest);
    }

    [Fact]
    public async Task RenderStreamAsync_WaitingForEverything_IsBoundedByTheStreamTimeout()
    {
        // A crawler's complete document takes as long as its slowest Suspense boundary, beyond the shell's timeout.
        await using var sidecar = Create("fake-sidecar.mjs", renderTimeout: TimeSpan.FromMilliseconds(500), configure: o => o.StreamTimeout = TimeSpan.FromSeconds(10));
        await sidecar.StartAsync(Ct);

        await using var stream = await sidecar.RenderStreamAsync("""{"page":"slow"}""", new SsrStreamOptions(WaitForAll: true), Ct);

        Assert.Equal("<html>slow</html>", await new StreamReader(stream).ReadToEndAsync(Ct));
    }

    [Fact]
    public async Task ResolveAsync_DeliversDeferredValuesToTheRender()
    {
        await using var sidecar = await StartFakeAsync();

        await using var stream = await sidecar.RenderStreamAsync("""{"page":"deferred"}""", new SsrStreamOptions(false, "r1"), Ct);
        await sidecar.ResolveAsync("r1", "d0", """{"likes":3}""", null, Ct);
        var html = await new StreamReader(stream).ReadToEndAsync(Ct);

        Assert.Equal("""<html>shell d0={"likes":3}</html>""", html);
    }

    [Fact]
    public async Task RenderStreamAsync_WhenRenderingFails_ThrowsWithTheJavaScriptStack()
    {
        await using var sidecar = await StartFakeAsync();

        var ex = await Assert.ThrowsAsync<SsrRenderException>(() => sidecar.RenderStreamAsync("""{"page":"fail"}""", new SsrStreamOptions(false), Ct));

        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task Sidecar_GetsTheStreamTimeout()
    {
        await using var sidecar = await StartFakeAsync(o => o.StreamTimeout = TimeSpan.FromSeconds(42));

        var html = await sidecar.RenderAsync("""{"page":"stream-timeout"}""", Ct);

        Assert.Contains("stream-timeout=42000", html);
    }

    [Fact]
    public async Task RenderAsync_WhenRenderingFails_ThrowsWithTheJavaScriptStack()
    {
        await using var sidecar = Create("fake-sidecar.mjs");
        await sidecar.StartAsync(Ct);

        var ex = await Assert.ThrowsAsync<SsrRenderException>(() => sidecar.RenderAsync("""{"page":"fail"}""", Ct));

        Assert.Equal("boom", ex.Message);
        Assert.Contains("app/fail/page.tsx", ex.JsStack);
    }

    [Fact]
    public async Task RenderAsync_SlowerThanTheTimeout_ThrowsTimeout()
    {
        await using var sidecar = Create("fake-sidecar.mjs", renderTimeout: TimeSpan.FromMilliseconds(300));
        await sidecar.StartAsync(Ct);

        var ex = await Assert.ThrowsAsync<SsrRenderException>(() => sidecar.RenderAsync("""{"page":"slow"}""", Ct));

        Assert.Contains("did not finish", ex.Message);
    }

    [Fact]
    public async Task Crash_IsRestartedAndRenderingRecovers()
    {
        await using var sidecar = Create("fake-sidecar.mjs");
        await sidecar.StartAsync(Ct);

        await SidecarPoolTests.CrashAsync(sidecar);
        var html = await sidecar.RenderAsync("""{"page":"app/after/page.tsx"}""", Ct);

        Assert.Equal("<html>app/after/page.tsx production</html>", html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Render_WaitingForARestartLongerThanItsTimeout_SaysTheSidecarIsRestarting(bool streamed)
    {
        await using var sidecar = Create("fake-sidecar.mjs", renderTimeout: TimeSpan.FromMilliseconds(500));
        await sidecar.StartAsync(Ct);
        await SidecarPoolTests.CrashAsync(sidecar);

        // The restart comes after a second.
        Func<Task> render = streamed
            ? () => sidecar.RenderStreamAsync("""{"page":"x"}""", new SsrStreamOptions(false), Ct)
            : () => sidecar.RenderAsync("""{"page":"x"}""", Ct);
        var ex = await Assert.ThrowsAsync<SsrRenderException>(render);

        Assert.StartsWith("The Node sidecar was not ready to render within ", ex.Message);
        Assert.EndsWith("s: it is restarting.", ex.Message);
    }

    // `exit-slowly` closes the port at once and exits half a second later. That is how a crash looks to .NET until
    // it has reaped the process, which under load can be after the next render has picked up the old address.

    [Fact]
    public async Task Crash_NoticedLate_TheNextRenderWaitsForTheRestart()
    {
        await using var sidecar = await StartFakeAsync();

        await Assert.ThrowsAsync<SsrRenderException>(() => sidecar.RenderAsync("""{"page":"exit-slowly"}""", Ct));
        var html = await sidecar.RenderAsync("""{"page":"app/after/page.tsx"}""", Ct);

        Assert.Equal("<html>app/after/page.tsx production</html>", html);
    }

    [Fact]
    public async Task Crash_NoticedLate_WithARestartSlowerThanTheGrace_TheNextRenderStillWaitsForIt()
    {
        await using var sidecar = await StartFakeAsync();
        // A first crash is restarted after a second, and a second one within 30 s after two.
        await SidecarPoolTests.CrashAsync(sidecar);
        await sidecar.RenderAsync("""{"page":"app/back/page.tsx"}""", Ct);
        await Assert.ThrowsAsync<SsrRenderException>(() => sidecar.RenderAsync("""{"page":"exit-slowly"}""", Ct));

        // Refused, this waits for the restart, beyond the two seconds after which a process still running takes renders.
        var html = await sidecar.RenderAsync("""{"page":"app/after/page.tsx"}""", Ct);

        Assert.Equal("<html>app/after/page.tsx production</html>", html);
    }

    [Fact]
    public async Task Crash_NoticedLate_AStreamAndItsValuesWaitForTheRestart()
    {
        await using var sidecar = await StartFakeAsync();
        await Assert.ThrowsAsync<SsrRenderException>(() => sidecar.RenderAsync("""{"page":"exit-slowly"}""", Ct));

        // As PageRenderer does: the render is asked for, then its value is delivered while the render is starting.
        var rendering = sidecar.RenderStreamAsync("""{"page":"deferred"}""", new SsrStreamOptions(false, "r1"), Ct);
        await sidecar.ResolveAsync("r1", "d0", "7", null, Ct);
        await using var stream = await rendering;

        Assert.Equal("<html>shell d0=7</html>", await new StreamReader(stream).ReadToEndAsync(Ct));
    }

    [Fact(Timeout = LingeringChildTimeout)]
    public async Task Crash_LeavingAChildThatKeepsItsOutputOpen_IsStillRestarted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var sidecar = await StartFakeAsync();
        var pid = await SidecarPoolTests.PidAsync(sidecar);

        await Assert.ThrowsAsync<SsrRenderException>(() => sidecar.RenderAsync("""{"page":"exit-leaving-child"}""", ct));
        // Gone first. An exiting process can let go of the render's connection before its port, and a render that gets
        // into the port's queue in between is reset with it, as one sent to a crashing process is, and not sent again.
        await SidecarPoolTests.WaitForExitAsync(pid);
        var html = await sidecar.RenderAsync("""{"page":"app/after/page.tsx"}""", ct);

        Assert.Equal("<html>app/after/page.tsx production</html>", html);
    }

    [Fact]
    public async Task Crash_TheRenderTheProcessReceived_IsNotSentAgain()
    {
        // Declared first, so it is deleted after the sidecar has stopped.
        using var dir = new TempDirectory();
        await using var sidecar = await StartFakeAsync();
        var marker = Path.Combine(dir.FullName, "rendered");
        var payload = $$"""{"page":"exit-slowly","marker":{{System.Text.Json.JsonSerializer.Serialize(marker)}}}""";

        await Assert.ThrowsAsync<SsrRenderException>(() => sidecar.RenderAsync(payload, Ct));

        // What the restarted process answers when the same render reaches it.
        Assert.Equal("<html>sent again</html>", await sidecar.RenderAsync(payload, Ct));
    }

    [Fact]
    public async Task RefusedByAProcessThatKeepsRunning_TheRenderReachesItOnceItListensAgain()
    {
        await using var sidecar = await StartFakeAsync();
        var pid = await sidecar.RenderAsync("""{"page":"pid"}""", Ct);

        await sidecar.RenderAsync("""{"page":"pause-listening"}""", Ct);
        var html = await sidecar.RenderAsync("""{"page":"pid"}""", Ct);

        Assert.Equal(pid, html);
        Assert.True(sidecar.IsReady);
    }

    [Theory]
    // What .NET throws while connecting: an HttpRequestException(ConnectionError) around the socket error.
    [InlineData("refused while connecting", true)]
    [InlineData("reset while connecting", true)]
    [InlineData("not connected while connecting", true)]
    // A refusal only ever comes from connecting, however it is reported.
    [InlineData("refused, reported otherwise", true)]
    // The connect step reads the new socket's address outside that wrapping, so a reset there surfaces bare.
    [InlineData("bare not connected", true)]
    [InlineData("bare reset", true)]
    // The request was written: a listener that closes resets the connections waiting in its queue.
    [InlineData("reset after sending", false)]
    [InlineData("response ended", false)]
    // Only a ConnectionError says the socket error came from connecting.
    [InlineData("reset, not while connecting", false)]
    [InlineData("not connected, not while connecting", false)]
    // Out of local ports: nothing is wrong with the process, so it must not be withdrawn.
    [InlineData("no address left while connecting", false)]
    [InlineData("no buffer space while connecting", false)]
    [InlineData("address in use while connecting", false)]
    [InlineData("bare no address left", false)]
    [InlineData("connection error without a socket error", false)]
    public void IsConnectFailure_OnlyForFailuresThatSentNothing(string failure, bool connectFailure)
    {
        Exception exception = failure switch
        {
            "refused while connecting" => Connecting(SocketError.ConnectionRefused),
            "reset while connecting" => Connecting(SocketError.ConnectionReset),
            "not connected while connecting" => Connecting(SocketError.NotConnected),
            "refused, reported otherwise" => new HttpRequestException("Connection refused", new SocketException((int)SocketError.ConnectionRefused)),
            "bare not connected" => new SocketException((int)SocketError.NotConnected),
            "bare reset" => new SocketException((int)SocketError.ConnectionReset),
            "reset after sending" => new HttpRequestException(HttpRequestError.Unknown, "An error occurred while sending the request.",
                new IOException("Connection reset by peer", new SocketException((int)SocketError.ConnectionReset))),
            "response ended" => new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely."),
            "reset, not while connecting" => new HttpRequestException(HttpRequestError.Unknown, "Connection reset by peer",
                new SocketException((int)SocketError.ConnectionReset)),
            "not connected, not while connecting" => new HttpRequestException(HttpRequestError.Unknown, "Transport endpoint is not connected",
                new SocketException((int)SocketError.NotConnected)),
            "no address left while connecting" => Connecting(SocketError.AddressNotAvailable),
            "no buffer space while connecting" => Connecting(SocketError.NoBufferSpaceAvailable),
            "address in use while connecting" => Connecting(SocketError.AddressAlreadyInUse),
            "bare no address left" => new SocketException((int)SocketError.AddressNotAvailable),
            "connection error without a socket error" => new HttpRequestException(HttpRequestError.ConnectionError, "No connection"),
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, null),
        };

        Assert.Equal(connectFailure, NodeSidecar.IsConnectFailure(exception));

        static HttpRequestException Connecting(SocketError error) =>
            new(HttpRequestError.ConnectionError, $"{error} (127.0.0.1:1)", new SocketException((int)error));
    }

    [Theory]
    [InlineData(SocketError.ConnectionReset, true)]
    [InlineData(SocketError.NotConnected, false)]
    public async Task ConnectFailure_OtherThanARefusal_IsSentAgain(SocketError error, bool wrapped)
    {
        var failure = new SocketException((int)error);
        var handler = new FailingHandler(send => send > 1 ? null
            : wrapped ? new HttpRequestException(HttpRequestError.ConnectionError, failure.Message, failure) : failure);
        await using var sidecar = Create("fake-sidecar.mjs", handler: handler);
        await sidecar.StartAsync(Ct);

        // The process is still running, so the render reaches it once it takes renders again.
        var html = await sidecar.RenderAsync("""{"page":"app/page.tsx"}""", Ct);

        Assert.Equal("<html>app/page.tsx production</html>", html);
        Assert.Equal(2, handler.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SocketError_ThatIsNotAConnectFailure_IsReportedAsUnreachable_AndTheProcessStaysReady(bool streamed)
    {
        var handler = new FailingHandler(_ => new SocketException((int)SocketError.AddressNotAvailable));
        await using var sidecar = Create("fake-sidecar.mjs", handler: handler);
        await sidecar.StartAsync(Ct);

        Func<Task> render = streamed
            ? () => sidecar.RenderStreamAsync("""{"page":"x"}""", new SsrStreamOptions(false), Ct)
            : () => sidecar.RenderAsync("""{"page":"x"}""", Ct);
        var ex = await Assert.ThrowsAsync<SsrRenderException>(render);

        Assert.StartsWith("Could not reach the Node sidecar: ", ex.Message);
        Assert.IsType<SocketException>(ex.InnerException);
        Assert.Equal(1, handler.Sends);
        Assert.True(sidecar.IsReady);
    }

    /// <summary>Sends through a real handler, unless <paramref name="fail"/> has an exception for that send (counted from 1).</summary>
    private sealed class FailingHandler(Func<int, Exception?> fail) : DelegatingHandler(new HttpClientHandler())
    {
        private int _sends;

        public int Sends => Volatile.Read(ref _sends);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            fail(Interlocked.Increment(ref _sends)) is { } failure ? throw failure : await base.SendAsync(request, cancellationToken);
    }

    [Fact]
    public async Task Refusal_IsLogged()
    {
        var logs = new ListLoggerProvider();
        using var loggers = new LoggerFactory([logs]);
        await using var sidecar = Create("fake-sidecar.mjs", loggers: loggers);
        await sidecar.StartAsync(Ct);
        var pid = await SidecarPoolTests.PidAsync(sidecar);
        await sidecar.RenderAsync("""{"page":"stop-listening"}""", Ct);

        Assert.Null(await sidecar.TryRenderAsync("""{"page":"x"}""", Ct));

        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning
            && e.Message.StartsWith($"The Node sidecar {pid} did not take a connection (", StringComparison.Ordinal)
            && e.Message.EndsWith("); it takes no renders until it restarts, or for 2s if it keeps running.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Exit_IsLoggedWhenTheProcessIsTakenOut_BeforeTheExitItself()
    {
        var logs = new ListLoggerProvider();
        using var loggers = new LoggerFactory([logs]);
        await using var sidecar = Create("fake-sidecar.mjs", loggers: loggers);
        await sidecar.StartAsync(Ct);
        var pid = await SidecarPoolTests.PidAsync(sidecar);

        await Assert.ThrowsAsync<SsrRenderException>(() => sidecar.RenderAsync("""{"page":"crash"}""", Ct));
        var exit = await WaitForLogAsync(logs, e => e.Message.StartsWith("The Node sidecar exited with code 1;", StringComparison.Ordinal));

        Assert.NotNull(exit);
        var entries = logs.Entries.ToList();
        var takenOut = entries.FindIndex(e => e.Level == LogLevel.Warning
            && e.Message == $"The Node sidecar {pid} has exited; it takes no renders until it restarts.");
        Assert.InRange(takenOut, 0, entries.IndexOf(exit) - 1);
    }

    [Fact]
    public async Task RefusedOnBothAttempts_SaysWhatHappened()
    {
        await using var sidecar = await StartFakeAsync();
        await sidecar.RenderAsync("""{"page":"stop-listening"}""", Ct);

        // Out at the first refusal, and back two seconds later, still refusing.
        var ex = await Assert.ThrowsAsync<SsrRenderException>(() => sidecar.RenderAsync("""{"page":"x"}""", Ct));

        Assert.Equal("Could not reach the Node sidecar: on both attempts, its process had exited or did not take the connection.", ex.Message);
    }

    [Fact]
    public async Task StartAsync_WhenTheScriptExitsBeforeReady_FailsWithItsOutput()
    {
        await using var sidecar = Create("broken-sidecar.mjs");

        var ex = await Assert.ThrowsAsync<SsrRenderException>(() => sidecar.StartAsync(Ct));

        Assert.Contains("exited with code 3", ex.Message);
        Assert.Contains("Unexpected token", ex.JsStack);
    }

    [Fact]
    public async Task StartAsync_WhenTheOutputIsInTerminalColours_LogsAndKeepsItAsPlainText()
    {
        // Vite's logger colours its errors in a pipe too; a log file or the error page would show the codes as text.
        var logs = new ListLoggerProvider();
        using var loggers = new LoggerFactory([logs]);
        await using var sidecar = Create("coloured-sidecar.mjs", loggers: loggers);

        var ex = await Assert.ThrowsAsync<SsrRenderException>(() => sidecar.StartAsync(Ct));

        string[] plain = ["[PARSE_ERROR] Expected `>` but found `<`", "    ╭─[ app/about/page.tsx:10:7 ]"];
        Assert.Equal(string.Join('\n', plain), ex.JsStack);
        Assert.Equal(plain, logs.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message));
        Assert.Contains(logs.Entries, e => e is { Level: LogLevel.Information, Message: "starting" });
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains('\x1b', StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_WhenTheVitePortIsTaken_SaysWhosePortItIsAndHowToMoveIt()
    {
        var logs = new ListLoggerProvider();
        using var loggers = new LoggerFactory([logs]);
        await using var sidecar = Create("vite-port-taken-sidecar.mjs", configure: o => o.Mode = NetjesMode.Development, loggers: loggers);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => sidecar.StartAsync(Ct));

        Assert.Equal(
            "Port 5173 is in use, and the Vite dev server needs it. Stop what is using it, or move Vite to another port " +
            "with Netjes__ViteDevPort=5174 in the environment.",
            ex.Message);
        // The protocol line is not output to pass on.
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("NETJES_FAILED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_WhenTheViteDevServerDoesNotStart_SaysWhatViteReported()
    {
        var logs = new ListLoggerProvider();
        using var loggers = new LoggerFactory([logs]);
        await using var sidecar = Create("vite-failed-sidecar.mjs", configure: o => o.Mode = NetjesMode.Development, loggers: loggers);

        var ex = await Assert.ThrowsAsync<ViteDevServerFailedException>(() => sidecar.StartAsync(Ct));

        Assert.Equal(
            "The Vite dev server did not start: Build failed with 1 error:\n\n[PARSE_ERROR] Unexpected token\n" +
            "   ╭─[ vite.config.ts:5:20 ]\n 5 │   plugins: [netjes(],\n───╯\nFix it, then start the app again.",
            ex.Message);
        // Vite's own line about it is passed on; the protocol line is not.
        Assert.Contains(logs.Entries, e => e is { Level: LogLevel.Warning, Message: "failed to load config from /app/vite.config.ts" });
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("NETJES_FAILED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_WhenViteCannotFindAPackage_SaysToInstallThePackages()
    {
        await using var sidecar = Create("vite-package-missing-sidecar.mjs", configure: o => o.Mode = NetjesMode.Development);

        var ex = await Assert.ThrowsAsync<ViteDevServerFailedException>(() => sidecar.StartAsync(Ct));

        Assert.Equal(
            "The Vite dev server did not start: Cannot find package 'some-plugin' imported from " +
            "/app/node_modules/.vite-temp/vite.config.ts.timestamp-1.mjs\n" +
            $"Run `npm install` in '{AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)}', then start the app again.",
            ex.Message);
    }

    [Fact]
    public async Task StartAsync_WhenACommonJsViteConfigCannotFindAPackage_SaysToInstallThePackages()
    {
        // Node's require reports MODULE_NOT_FOUND where import reports ERR_MODULE_NOT_FOUND.
        await using var sidecar = Create("vite-cjs-package-missing-sidecar.mjs", configure: o => o.Mode = NetjesMode.Development);

        var ex = await Assert.ThrowsAsync<ViteDevServerFailedException>(() => sidecar.StartAsync(Ct));

        Assert.Equal(
            "The Vite dev server did not start: Cannot find module 'some-plugin'\nRequire stack:\n- /app/vite.config.cjs\n" +
            "- /app/node_modules/vite/dist/node/chunks/node.js\n" +
            $"Run `npm install` in '{AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)}', then start the app again.",
            ex.Message);
    }

    [Fact]
    public async Task StartAsync_WhenTheScriptIsMissing_ExplainsHowToBuild()
    {
        await using var sidecar = Create("missing.mjs");

        var ex = await Assert.ThrowsAsync<NetjesStartupException>(() => sidecar.StartAsync(Ct));

        Assert.Contains("npm run build", ex.Message);
    }

    [Fact]
    public async Task StopAsync_ShutsTheProcessDown()
    {
        await using var sidecar = Create("fake-sidecar.mjs");
        await sidecar.StartAsync(Ct);

        await sidecar.StopAsync();

        var ex = await Assert.ThrowsAsync<SsrRenderException>(() => sidecar.RenderAsync("""{"page":"x"}""", Ct));
        Assert.Contains("Could not reach", ex.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_AfterAStop_IsRefused_AndLaunchesNothing(bool disposed)
    {
        var logs = new ListLoggerProvider();
        using var loggers = new LoggerFactory([logs]);
        await using var sidecar = Create("fake-sidecar.mjs", loggers: loggers);
        await sidecar.StartAsync(Ct);
        await (disposed ? sidecar.DisposeAsync().AsTask() : sidecar.StopAsync());

        // Every later stop and the dispose wait for the stop that ended the first process, so none would end this one.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sidecar.StartAsync(Ct));

        Assert.Equal("The Netjes Node sidecar has been stopped; it cannot be started again.", ex.Message);
        Assert.Single(logs.Entries, e => e.Message.StartsWith("Started Node sidecar ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_Again_LaunchesNothing_AndKeepsTheProcessItStarted()
    {
        var logs = new ListLoggerProvider();
        using var loggers = new LoggerFactory([logs]);
        await using var sidecar = Create("fake-sidecar.mjs", loggers: loggers);
        await sidecar.StartAsync(Ct);
        var pid = await SidecarPoolTests.PidAsync(sidecar);

        await sidecar.StartAsync(Ct);

        Assert.Single(logs.Entries, e => e.Message.StartsWith("Started Node sidecar ", StringComparison.Ordinal));
        Assert.Equal(pid, await SidecarPoolTests.PidAsync(sidecar));
    }

    [Fact]
    public async Task StartAsync_TwiceAtOnce_LaunchesOneProcess_AndBothWaitForIt()
    {
        var logs = new ListLoggerProvider();
        using var loggers = new LoggerFactory([logs]);
        await using var sidecar = Create("fake-sidecar.mjs", loggers: loggers);

        await Task.WhenAll(sidecar.StartAsync(Ct), sidecar.StartAsync(Ct));

        Assert.True(sidecar.IsReady);
        Assert.Single(logs.Entries, e => e.Message.StartsWith("Started Node sidecar ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_AgainAfterAFailedStart_FailsTheSameWay_AndLaunchesNothing()
    {
        var logs = new ListLoggerProvider();
        using var loggers = new LoggerFactory([logs]);
        await using var sidecar = Create("broken-sidecar.mjs", loggers: loggers);
        var first = await Assert.ThrowsAsync<SsrRenderException>(() => sidecar.StartAsync(Ct));

        var again = await Assert.ThrowsAsync<SsrRenderException>(() => sidecar.StartAsync(Ct));

        Assert.Same(first, again);
        Assert.Single(logs.Entries, e => e.Message.StartsWith("Started Node sidecar ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_AgainAfterALaunchThatThrew_FailsTheSameWay_AtOnce()
    {
        // Process.Start throws an InvalidOperationException of its own for an empty executable, not a Win32Exception.
        var logs = new ListLoggerProvider();
        using var loggers = new LoggerFactory([logs]);
        await using var sidecar = Create("fake-sidecar.mjs", configure: o => o.NodeExecutable = "", loggers: loggers);
        var first = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => sidecar.StartAsync(Ct));

        var again = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => sidecar.StartAsync(Ct));

        // Not a wait for a process that never started, then "did not report ready".
        Assert.Same(first, again);
        Assert.DoesNotContain(logs.Entries, e => e.Message.StartsWith("Started Node sidecar ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StopAsync_TwiceAtOnceWhileADisposeIsStopping_WaitsForThatStopAndEndsTheProcess()
    {
        // WebApplicationFactory stops the host twice at once, and the app's own Run() disposes it once its stop returns:
        // stops and a dispose overlap, in whichever order the threads get there.
        var sidecar = await StartFakeAsync();
        var pid = await SidecarPoolTests.PidAsync(sidecar);
        await sidecar.RenderAsync("""{"page":"keep-running"}""", Ct);

        var disposing = sidecar.DisposeAsync().AsTask();
        // Late enough that a grace of their own would end after the dispose had killed the process and let go of it.
        await Task.Delay(1000, Ct);
        var stops = new[] { sidecar.StopAsync(), sidecar.StopAsync() };

        await disposing.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var endedWithTheDispose = stops.Select(stop => stop.IsCompleted).ToArray();
        await Task.WhenAll(stops).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // One stop: they end with the dispose's, rather than each waiting out a grace of its own.
        Assert.Equal([true, true], endedWithTheDispose);
        await SidecarPoolTests.WaitForExitAsync(pid).WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task DisposeAsync_TwiceAfterAFailedStart_DoesNotThrow()
    {
        // The container disposes the pool once per registration (SidecarPool and ISsrRenderer), and so each sidecar.
        var sidecar = Create("broken-sidecar.mjs");
        await Assert.ThrowsAsync<SsrRenderException>(() => sidecar.StartAsync(Ct));

        await sidecar.DisposeAsync();
        await sidecar.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_Twice_DoesNotThrow()
    {
        var sidecar = Create("fake-sidecar.mjs");
        await sidecar.StartAsync(Ct);

        await sidecar.DisposeAsync();
        await sidecar.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_WhenTheKillFails_LetsGoOfTheSidecarAndLogsIt()
    {
        // Killing a process tree fails part way when a process in it may not be signalled: that one is a Win32Exception,
        // and the tree's kill throws them together. This kill ends the sidecar's process first, so nothing outlives the test.
        var logs = new ListLoggerProvider();
        using var loggers = new LoggerFactory([logs]);
        Process? killed = null;
        var sidecar = Create("fake-sidecar.mjs", loggers: loggers, killTree: process =>
        {
            killed = process;
            process.Kill(entireProcessTree: true);
            throw new AggregateException(
                "Not all processes in the process tree could be terminated.", new Win32Exception(1, "Operation not permitted"));
        });
        await sidecar.StartAsync(Ct);
        var pid = await SidecarPoolTests.PidAsync(sidecar);
        // Slow to exit, so the stop gets as far as the kill.
        await sidecar.RenderAsync("""{"page":"keep-running"}""", Ct);

        await sidecar.DisposeAsync();

        // The process handle is let go of: a disposed Process has no process associated with it.
        Assert.NotNull(killed);
        Assert.Throws<InvalidOperationException>(() => killed.HasExited);
        // Its HTTP client is gone with it.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => sidecar.RenderAsync("""{"page":"x"}""", Ct));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning
            && e.Message == $"Stopping the Node sidecar {pid} failed: Not all processes in the process tree could be terminated. " +
                "(Operation not permitted) It, or a process it started, may still be running.");
    }

    [Fact]
    public async Task StrayReadyLinesFromPageCode_AreIgnored()
    {
        await using var sidecar = Create("fake-sidecar.mjs");
        await sidecar.StartAsync(Ct);

        await sidecar.RenderAsync("""{"page":"log-ready"}""", Ct);
        await Task.Delay(200, Ct);
        var html = await sidecar.RenderAsync("""{"page":"app/after/page.tsx"}""", Ct);

        Assert.Null(sidecar.ViteOrigin);
        Assert.Equal("<html>app/after/page.tsx production</html>", html);
    }

    [Fact]
    public async Task Relaunch_WhenTheScriptIsTemporarilyMissing_IsRetried()
    {
        using var dir = new TempDirectory();
        var script = Path.Combine(dir.FullName, "sidecar.mjs");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Sidecar", "fake-sidecar.mjs"), script);
        await using var sidecar = Create(script, renderTimeout: TimeSpan.FromSeconds(15));
        await sidecar.StartAsync(Ct);

        // Crash it, and have the script vanish (as during `npm install`) before the first restart attempt.
        File.Move(script, script + ".away");
        await SidecarPoolTests.CrashAsync(sidecar);
        await Task.Delay(1500, Ct);
        File.Move(script + ".away", script);
        var html = await sidecar.RenderAsync("""{"page":"app/back/page.tsx"}""", Ct);

        Assert.Equal("<html>app/back/page.tsx production</html>", html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TryRender_OnAProcessThatIsOut_ReturnsAtOnce_AndRecordsNothing(bool streamed)
    {
        using var dir = new TempDirectory();
        var script = Path.Combine(dir.FullName, "sidecar.mjs");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Sidecar", "fake-sidecar.mjs"), script);
        await using var sidecar = Create(script, renderTimeout: TimeSpan.FromSeconds(2));
        await sidecar.StartAsync(Ct);
        // Without its script the process cannot restart, so once it is out it stays out.
        File.Move(script, script + ".away");
        await SidecarPoolTests.CrashAsync(sidecar);
        await WaitUntilOutAsync(sidecar);

        // As the pool's first pick asks: it picks again rather than wait, and nothing was sent, so nothing is timed.
        using var telemetry = new SidecarTelemetry();
        object? result = streamed
            ? await sidecar.TryRenderStreamAsync("""{"page":"x"}""", new SsrStreamOptions(false), Ct)
            : await sidecar.TryRenderAsync("""{"page":"x"}""", Ct);

        Assert.Null(result);
        Assert.Equal((0, 0), (telemetry.Spans, telemetry.Durations));
    }

    /// <summary>Counts the sidecar render spans and duration samples recorded in the calling test's own flow.</summary>
    private sealed class SidecarTelemetry : IDisposable
    {
        private static readonly AsyncLocal<bool> Counting = new();
        private readonly ActivityListener _activities;
        private readonly MeterListener _meters = new();
        private int _spans;
        private int _durations;

        public SidecarTelemetry()
        {
            Counting.Value = true;
            _activities = new ActivityListener
            {
                ShouldListenTo = source => source.Name == NetjesTelemetry.Name,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStarted = activity =>
                {
                    if (Counting.Value && activity.OperationName == "netjes.sidecar.render")
                    {
                        Interlocked.Increment(ref _spans);
                    }
                },
            };
            ActivitySource.AddActivityListener(_activities);
            _meters.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == NetjesTelemetry.Name && instrument.Name == "netjes.sidecar.render.duration")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _meters.SetMeasurementEventCallback<double>((_, _, _, _) =>
            {
                if (Counting.Value)
                {
                    Interlocked.Increment(ref _durations);
                }
            });
            _meters.Start();
        }

        public int Spans => Volatile.Read(ref _spans);

        public int Durations => Volatile.Read(ref _durations);

        public void Dispose()
        {
            Counting.Value = false;
            _activities.Dispose();
            _meters.Dispose();
        }
    }

    [Theory]
    [InlineData("has exited; it takes no renders")]
    [InlineData("exited with code")]
    public async Task Crash_WithALogSinkThatFails_IsStillRestarted(string failingLine)
    {
        var logs = new ListLoggerProvider();
        using var loggers = new LoggerFactory([logs, new ThrowingLoggerProvider(failingLine)]);
        await using var sidecar = Create("fake-sidecar.mjs", loggers: loggers);
        await sidecar.StartAsync(Ct);

        await SidecarPoolTests.CrashAsync(sidecar);
        // Once the exit handler has taken it out: a render that noticed the exit first would log the warning itself,
        // and a sink that fails makes any request that logs through it fail.
        await WaitUntilOutAsync(sidecar);
        var html = await sidecar.RenderAsync("""{"page":"app/after/page.tsx"}""", Ct);

        Assert.Equal("<html>app/after/page.tsx production</html>", html);
        Assert.NotNull(await WaitForLogAsync(logs, e => e.Level == LogLevel.Error
            && e.Message.StartsWith("Handling the Node sidecar's exit failed: ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Restart_WithALogSinkThatFails_KeepsTrying()
    {
        using var dir = new TempDirectory();
        var script = Path.Combine(dir.FullName, "sidecar.mjs");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Sidecar", "fake-sidecar.mjs"), script);
        var logs = new ListLoggerProvider();
        using var loggers = new LoggerFactory([logs, new ThrowingLoggerProvider("Could not restart the Node sidecar: ")]);
        await using var sidecar = Create(script, renderTimeout: TimeSpan.FromSeconds(15), loggers: loggers);
        await sidecar.StartAsync(Ct);
        File.Move(script, script + ".away");
        await SidecarPoolTests.CrashAsync(sidecar);

        // The first restart finds no script, and the sink fails on the line that says so.
        Assert.NotNull(await WaitForLogAsync(logs, e => e.Message.StartsWith("Could not restart the Node sidecar: ", StringComparison.Ordinal)));
        File.Move(script + ".away", script);
        var html = await sidecar.RenderAsync("""{"page":"app/back/page.tsx"}""", Ct);

        Assert.Equal("<html>app/back/page.tsx production</html>", html);
    }

    [Fact]
    public async Task Restart_WhoseStartedLineFails_KeepsTheProcessItStarted()
    {
        var logs = new ListLoggerProvider();
        var sink = new ThrowingLoggerProvider("Started Node sidecar ") { Armed = false };
        using var loggers = new LoggerFactory([logs, sink]);
        await using var sidecar = Create("fake-sidecar.mjs", loggers: loggers);
        await sidecar.StartAsync(Ct);
        sink.Armed = true;

        // The restart starts a process, then fails on the line that says so: the launch itself went through.
        await SidecarPoolTests.CrashAsync(sidecar);
        var pid = await SidecarPoolTests.PidAsync(sidecar);
        // Longer than the two seconds a retry, if one were scheduled, would wait before replacing that process.
        await Task.Delay(TimeSpan.FromSeconds(2.5), Ct);

        Assert.Equal(pid, await SidecarPoolTests.PidAsync(sidecar));
        Assert.Equal(2, logs.Entries.Count(e => e.Message.StartsWith("Started Node sidecar ", StringComparison.Ordinal)));
        Assert.DoesNotContain(logs.Entries, e => e.Message.StartsWith("Could not restart the Node sidecar", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restart_ThatThrows_IsRetried()
    {
        NetjesOptions? options = null;
        var logs = new ListLoggerProvider();
        using var loggers = new LoggerFactory([logs]);
        await using var sidecar = Create("fake-sidecar.mjs", TimeSpan.FromSeconds(15), o => options = o, loggers: loggers);
        await sidecar.StartAsync(Ct);
        var node = options!.NodeExecutable;
        // With no executable, Process.Start throws instead of reporting a launch that failed.
        options.NodeExecutable = "";
        await SidecarPoolTests.CrashAsync(sidecar);

        Assert.NotNull(await WaitForLogAsync(logs, e => e.Message.StartsWith("Could not restart the Node sidecar: ", StringComparison.Ordinal)));
        options.NodeExecutable = node;
        var html = await sidecar.RenderAsync("""{"page":"app/back/page.tsx"}""", Ct);

        Assert.Equal("<html>app/back/page.tsx production</html>", html);
    }

    /// <summary>Waits until the process no longer counts as ready, up to five seconds.</summary>
    private static async Task WaitUntilOutAsync(NodeSidecar sidecar)
    {
        for (var waited = 0; sidecar.IsReady && waited < 5000; waited += 10)
        {
            await Task.Delay(10, Ct);
        }

        Assert.False(sidecar.IsReady);
    }

    /// <summary>The first entry that matches, waiting up to five seconds for it.</summary>
    private static async Task<LogEntry?> WaitForLogAsync(ListLoggerProvider logs, Func<LogEntry, bool> match)
    {
        for (var waited = 0; waited < 5000; waited += 20)
        {
            if (logs.Entries.FirstOrDefault(match) is { } entry)
            {
                return entry;
            }

            await Task.Delay(20, Ct);
        }

        return logs.Entries.FirstOrDefault(match);
    }

    /// <summary>A log sink that fails on lines containing <paramref name="text"/>, as a broken one does.</summary>
    private sealed class ThrowingLoggerProvider(string text) : ILoggerProvider
    {
        /// <summary>Whether it fails at all yet.</summary>
        public bool Armed { get; set; } = true;

        public ILogger CreateLogger(string categoryName) => new ThrowingLogger(this, text);

        public void Dispose()
        {
        }

        private sealed class ThrowingLogger(ThrowingLoggerProvider provider, string text) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (provider.Armed && formatter(state, exception).Contains(text, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The log sink is down.");
                }
            }
        }
    }

    /// <summary>A directory for one test, deleted when it is disposed.</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string FullName { get; } = Directory.CreateTempSubdirectory("netjes-sidecar-").FullName;

        public void Dispose() => Directory.Delete(FullName, recursive: true);
    }
}
