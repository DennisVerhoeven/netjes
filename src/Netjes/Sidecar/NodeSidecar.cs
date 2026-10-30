using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Netjes.Rendering;

namespace Netjes.Sidecar;

/// <summary>
/// Runs <c>sidecar/server.mjs</c> under Node and renders pages through its loopback HTTP endpoint.
/// In development the sidecar also hosts the Vite dev server. The process is restarted if it crashes, and it
/// exits by itself when this process goes away (its stdin closes). A request that never reached its process (the
/// connection was refused or reset while connecting, or the process had exited) goes out once more, to the process
/// that is ready next.
/// </summary>
/// <param name="handler">Sends the requests; tests hand in one that fails on purpose.</param>
/// <param name="killTree">Kills a process and the processes it started; tests hand in one that fails on purpose.</param>
internal sealed partial class NodeSidecar(
    NetjesContext context, ILogger<NodeSidecar> logger, HttpMessageHandler? handler = null, Action<Process>? killTree = null)
    : ISsrRenderer, IAsyncDisposable
{
    internal const string TokenHeader = "X-Netjes-Token";
    private const string ReadyPrefix = "NETJES_READY ";
    private const string FailedPrefix = "NETJES_FAILED ";
    private const int StderrLinesKept = 20;

    /// <summary>
    /// How long a process that did not take a connection stays out of the ready ones while it is still running. One
    /// that crashed is seen exiting within milliseconds; one still running by then was refusing for a moment only (on
    /// Windows a full accept queue refuses connections), and sits out this long, counted as restarting. A shorter grace
    /// would hand a crash .NET is slow to notice back to the renders waiting for its restart, and they would fail.
    /// </summary>
    private static readonly TimeSpan RefusalGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long the exit handler waits for a process's output to end, so the failure carries the last lines it printed.
    /// A child the process left running holds the output open for as long as that child lives.
    /// </summary>
    private static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(2);

    /// <summary>How long a stopping process has to exit once its stdin closes, before its process tree is killed.</summary>
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http = new(handler ?? new HttpClientHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly Action<Process> _killTree = killTree ?? (process => process.Kill(entireProcessTree: true));
    private readonly string _token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
    private readonly Lock _gate = new();
    private readonly Queue<string> _recentStderr = new();
    private TaskCompletionSource<Endpoint> _ready = NewReadySource();
    private Process? _process;
    private bool _processReported;

    /// <summary>Why the current process could not start, when it said so (NETJES_FAILED) before it exited.</summary>
    private Exception? _processFailure;
    private long _readyAt;
    private bool _everReady;
    private bool _started;
    private bool _stopping;
    private Task? _stop;
    private int _restarts;
    private int _disposed;

    public string? ViteOrigin { get; private set; }

    public bool IsReady => _ready.Task.IsCompletedSuccessfully;

    /// <summary>
    /// Launches the process and waits for it to report ready. Refused once a stop has begun, a dispose's included: every
    /// later stop waits for that one, which ends the process it found, so nothing would end a process launched after it.
    /// Only the first call launches: a later one, or one at the same time, waits for that process as the first does
    /// (or for its restart), and after a failed start fails the same way. Another launch would replace the process
    /// that the renders and the restarts look after, and leave it running.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_stopping)
            {
                throw new InvalidOperationException("The Netjes Node sidecar has been stopped; it cannot be started again.");
            }

            if (!_started)
            {
                _started = true;
                try
                {
                    if (!TryLaunch(out var failure))
                    {
                        _ready.TrySetException(failure);
                    }
                }
                catch (Exception ex) when (_process is null)
                {
                    // No process started (Process.Start refuses an empty executable, say), so none will report ready.
                    _ready.TrySetException(ex);
                }
            }
        }

        try
        {
            await _ready.Task.WaitAsync(context.Options.StartupTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                $"The Netjes Node sidecar did not report ready within {context.Options.StartupTimeout.TotalSeconds:0}s.");
        }
    }

    public async Task<string> RenderAsync(string payloadJson, CancellationToken cancellationToken) =>
        await RenderOnceAsync(null, payloadJson, cancellationToken)
        ?? await RenderOnceAsync(null, payloadJson, cancellationToken)
        ?? throw Refused();

    /// <summary>
    /// Renders if the process is ready now, or returns null: when it is not (taken out, or restarting), when it had
    /// exited, or when it did not take the connection. Nothing was sent then. This is the pool's first try, which picks
    /// again rather than wait, whether its pick fell back to a process that was not ready or saw this one ready just
    /// before it was taken out.
    /// </summary>
    internal Task<string?> TryRenderAsync(string payloadJson, CancellationToken cancellationToken) =>
        ReadyNow() is { } ready ? RenderOnceAsync(ready, payloadJson, cancellationToken) : Task.FromResult<string?>(null);

    /// <summary>
    /// One attempt, on <paramref name="ready"/> or else on the process that is ready next. Returns null when it never
    /// reached the process (it had exited, or did not take the connection), which then no longer counts as ready.
    /// </summary>
    private async Task<string?> RenderOnceAsync(Ready? ready, string payloadJson, CancellationToken cancellationToken)
    {
        // A client span: HttpClient sends it to the sidecar as traceparent, so Node's logs can be correlated.
        using var activity = NetjesTelemetry.StartActivity("netjes.sidecar.render", null, ActivityKind.Client);
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await SendRenderAsync(ready, payloadJson, cancellationToken);
        }
        catch (Exception ex)
        {
            activity.RecordException(ex);
            throw;
        }
        finally
        {
            NetjesTelemetry.SidecarDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    private async Task<string?> SendRenderAsync(Ready? ready, string payloadJson, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(context.Options.RenderTimeout);
        try
        {
            using var response = await TryPostAsync(
                ready, "render", payloadJson, null, HttpCompletionOption.ResponseContentRead, timeout.Token);
            if (response is null)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                return body;
            }

            var error = TryParseError(body);
            throw new SsrRenderException(
                error?.Message ?? $"The Node sidecar answered {(int)response.StatusCode}: {body}", error?.Stack);
        }
        catch (NotReadyException) when (!cancellationToken.IsCancellationRequested)
        {
            throw NotReadyInTime(context.Options.RenderTimeout);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SsrRenderException(
                $"Server rendering did not finish within {context.Options.RenderTimeout.TotalSeconds:0}s.", null);
        }
        catch (HttpRequestException ex)
        {
            throw new SsrRenderException($"Could not reach the Node sidecar: {ex.Message}", null, ex);
        }
        catch (SocketException ex)
        {
            // Thrown bare by .NET's connect step.
            throw new SsrRenderException($"Could not reach the Node sidecar: {ex.Message}", null, ex);
        }
    }

    public async Task<Stream> RenderStreamAsync(string payloadJson, SsrStreamOptions options, CancellationToken cancellationToken) =>
        await StreamOnceAsync(null, payloadJson, options, cancellationToken)
        ?? await StreamOnceAsync(null, payloadJson, options, cancellationToken)
        ?? throw Refused();

    /// <summary>Like <see cref="TryRenderAsync"/>, streamed.</summary>
    internal Task<Stream?> TryRenderStreamAsync(string payloadJson, SsrStreamOptions options, CancellationToken cancellationToken) =>
        ReadyNow() is { } ready ? StreamOnceAsync(ready, payloadJson, options, cancellationToken) : Task.FromResult<Stream?>(null);

    /// <summary>Like <see cref="RenderOnceAsync"/>, streamed.</summary>
    private async Task<Stream?> StreamOnceAsync(
        Ready? ready, string payloadJson, SsrStreamOptions options, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // The sidecar answers at the shell, or for a complete document only once every boundary has resolved.
        var limit = options.WaitForAll ? context.Options.StreamTimeout : context.Options.RenderTimeout;
        timeout.CancelAfter(limit);
        HttpResponseMessage? response = null;
        try
        {
            // The sidecar answers once the shell is ready; the body keeps coming as Suspense boundaries resolve.
            response = await TryPostAsync(
                ready, "render", payloadJson, options, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response is null)
            {
                return null;
            }

            if (response.IsSuccessStatusCode)
            {
                return new ResponseStream(response, await response.Content.ReadAsStreamAsync(cancellationToken));
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            response.Dispose();
            var error = TryParseError(body);
            throw new SsrRenderException(
                error?.Message ?? $"The Node sidecar answered {(int)response.StatusCode}: {body}", error?.Stack);
        }
        catch (NotReadyException) when (!cancellationToken.IsCancellationRequested)
        {
            throw NotReadyInTime(limit);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            response?.Dispose();
            throw new SsrRenderException($"Server rendering did not finish within {limit.TotalSeconds:0}s.", null);
        }
        catch (HttpRequestException ex)
        {
            response?.Dispose();
            throw new SsrRenderException($"Could not reach the Node sidecar: {ex.Message}", null, ex);
        }
        catch (SocketException ex)
        {
            // Thrown bare by .NET's connect step, so there is no response to dispose.
            throw new SsrRenderException($"Could not reach the Node sidecar: {ex.Message}", null, ex);
        }
    }

    public async Task ResolveAsync(string renderId, string id, string? valueJson, string? error, CancellationToken cancellationToken)
    {
        // The value is already JSON: splice it in rather than serializing it twice.
        var body = new StringBuilder("{\"renderId\":").Append(JsonSerializer.Serialize(renderId))
            .Append(",\"id\":").Append(JsonSerializer.Serialize(id))
            .Append(error is null ? ",\"value\":" + (valueJson ?? "null") : ",\"error\":" + JsonSerializer.Serialize(error))
            .Append('}');
        // A refused value goes where its render goes: to the process that is ready next.
        using var response =
            await TryPostAsync(null, "resolve", body.ToString(), null, HttpCompletionOption.ResponseContentRead, cancellationToken)
            ?? await TryPostAsync(null, "resolve", body.ToString(), null, HttpCompletionOption.ResponseContentRead, cancellationToken)
            ?? throw Refused();
    }

    /// <summary>
    /// Posts JSON to <paramref name="ready"/>, or else to the process that is ready next, with the token and, for a
    /// streamed render, its options. Returns null when the process had exited or did not take the connection (see
    /// <see cref="IsConnectFailure"/>): nothing was sent, and the process is taken out of the ready ones.
    /// </summary>
    private async Task<HttpResponseMessage?> TryPostAsync(
        Ready? ready, string path, string json, SsrStreamOptions? stream, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        var (source, endpoint) = ready ?? await ReadyAsync(cancellationToken);

        // Right after a crash the old process can still be the ready one: its exit handler has not run yet. A shortcut,
        // since the connection below would be refused and take it out too, but one that does not send the request, and
        // the token with it, to a port another process may have taken by now.
        if (HasExited(endpoint.Process) && Withdraw(source, endpoint))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.Uri, path))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(TokenHeader, _token);
        if (stream is { WaitForAll: true })
        {
            request.Headers.Add("X-Netjes-Wait-All", "1");
        }

        if (stream?.RenderId is { } renderId)
        {
            request.Headers.Add("X-Netjes-Render-Id", renderId);
        }

        try
        {
            return await _http.SendAsync(request, completion, cancellationToken);
        }
        catch (Exception ex) when (IsConnectFailure(ex))
        {
            // A process that reported ready stops taking connections when it exits, which .NET may not have seen yet
            // (its port closes before .NET reaps it), or for a moment while its accept queue is full (on Windows).
            if (!Withdraw(source, endpoint, ex))
            {
                throw;
            }

            return null;
        }
    }

    /// <summary>
    /// Whether sending failed while connecting to the process, so none of the request reached it: refused, or reset
    /// before anything was written (a listener that closes resets the connections waiting in its queue). .NET reports
    /// those as a ConnectionError, or throws the SocketException bare when its connect step reads the new socket's
    /// address. Other errors while connecting, such as running out of local ports, say nothing about the process.
    /// </summary>
    internal static bool IsConnectFailure(Exception exception) => exception switch
    {
        // A refusal only ever comes from connecting.
        HttpRequestException { InnerException: SocketException { SocketErrorCode: SocketError.ConnectionRefused } } => true,
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError, InnerException: SocketException socket } =>
            socket.SocketErrorCode is SocketError.ConnectionReset or SocketError.NotConnected,
        SocketException socket => socket.SocketErrorCode is SocketError.ConnectionReset or SocketError.NotConnected,
        _ => false,
    };

    private static SsrRenderException NotReadyInTime(TimeSpan limit) =>
        new($"The Node sidecar was not ready to render within {limit.TotalSeconds:0}s: it is restarting.", null);

    private static SsrRenderException Refused() =>
        new("Could not reach the Node sidecar: on both attempts, its process had exited or did not take the connection.", null);

    /// <summary>The streamed body; disposing it releases the response (and so the connection to the sidecar).</summary>
    private sealed class ResponseStream(HttpResponseMessage response, Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Stops the process, once: every call, a dispose's included, waits for the same stop. A host can be stopped twice
    /// at once (WebApplicationFactory does), and disposed as soon as one of those stops returns.
    /// </summary>
    public Task StopAsync()
    {
        lock (_gate)
        {
            // Nothing restarts from here on, so the process stays the one this stop ends.
            _stopping = true;
            var process = _process;
            // Run outside the lock, since it calls into the process.
            return _stop ??= Task.Run(() => StopProcessAsync(process));
        }
    }

    private async Task StopProcessAsync(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (process.HasExited)
            {
                return;
            }

            // Closing stdin asks the sidecar to shut down cleanly; kill it if it does not.
            process.StandardInput.Close();
            using var grace = new CancellationTokenSource(StopGrace);
            try
            {
                await process.WaitForExitAsync(grace.Token);
            }
            catch (OperationCanceledException)
            {
                _killTree(process);
            }
        }
        catch (InvalidOperationException)
        {
            // The process was disposed already, before this stop got to it or before the kill.
        }
    }

    public async ValueTask DisposeAsync()
    {
        // The container disposes the pool once per registration (SidecarPool and ISsrRenderer), and so each sidecar.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // The process is let go of only once the stop, whoever started it, is done with it.
        try
        {
            await StopAsync();
        }
        catch (Exception ex)
        {
            // Killing the process tree failed. A stop reports that to its caller; a dispose says so and lets go anyway.
            logger.LogWarning(
                ex, "Stopping the Node sidecar {Pid} failed: {Message} It, or a process it started, may still be running.",
                _process?.Id, ex.Message);
        }
        finally
        {
            _process?.Dispose();
            _http.Dispose();
        }
    }

    // Callers hold _gate.
    private bool TryLaunch(out Exception failure)
    {
        failure = null!;
        if (!File.Exists(context.SidecarScript))
        {
            failure = new NetjesStartupException(context.Mode == NetjesMode.Development
                ? $"Cannot find the netjes sidecar at '{context.SidecarScript}'. Run `npm install` in '{context.Root}'."
                : $"Cannot find the netjes sidecar at '{context.SidecarScript}'. Run `npm run build` in '{context.Root}'.");
            return false;
        }

        var start = new ProcessStartInfo(context.Options.NodeExecutable)
        {
            WorkingDirectory = context.Root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // What Node writes to a pipe, on every system. On Windows .NET would otherwise read the pipes in the console's
            // code page, and a code frame's box drawing or a page's own non-ASCII output would come out garbled.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(context.SidecarScript);
        start.Environment["NETJES_MODE"] = context.Mode == NetjesMode.Development ? "development" : "production";
        start.Environment["NETJES_ROOT"] = context.Root;
        start.Environment["NETJES_TOKEN"] = _token;
        start.Environment["NETJES_VITE_PORT"] = context.Options.ViteDevPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["NETJES_SERVER_ENTRY"] = context.ServerEntry;
        start.Environment["NODE_ENV"] = context.Mode == NetjesMode.Development ? "development" : "production";
        start.Environment["NETJES_RENDER_TIMEOUT_MS"] =
            ((long)context.Options.RenderTimeout.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["NETJES_STREAM_TIMEOUT_MS"] =
            ((long)context.Options.StreamTimeout.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture);

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => OnStdout(process, e.Data);
        process.ErrorDataReceived += (_, e) => OnStderr(e.Data);
        // Never on the thread that raised Exited: HasExited raises it on whichever thread notices the exit first, a
        // render's included, and can hold a lock while it does.
        process.Exited += (_, _) => _ = Task.Run(() => OnExitedAsync(process));
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            process.Dispose();
            failure = new NetjesStartupException(
                $"Could not start '{context.Options.NodeExecutable}'. Is Node.js installed and on the PATH? " +
                "Set Netjes:NodeExecutable to use another executable.", ex);
            return false;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process?.Dispose();
        _process = process;
        _processReported = false;
        _processFailure = null;
        logger.LogDebug("Started Node sidecar {Pid} for {Script}", process.Id, context.SidecarScript);
        return true;
    }

    private void OnStdout(Process process, string? line)
    {
        if (line is null)
        {
            return;
        }

        // Page code shares stdout, so only the first handshake line of the current process counts.
        if (line.StartsWith(ReadyPrefix, StringComparison.Ordinal) && TryAcceptReady(process, line[ReadyPrefix.Length..]))
        {
            return;
        }

        if (line.StartsWith(FailedPrefix, StringComparison.Ordinal) && TryAcceptFailure(process, line[FailedPrefix.Length..]))
        {
            return;
        }

        logger.LogInformation("{Output}", PlainText(line));
    }

    private bool TryAcceptReady(Process process, string json)
    {
        ReadyMessage? ready;
        try
        {
            ready = JsonSerializer.Deserialize<ReadyMessage>(json);
        }
        catch (JsonException)
        {
            return false;
        }

        lock (_gate)
        {
            if (ready is null || _processReported || !ReferenceEquals(process, _process))
            {
                return false;
            }

            _processReported = true;
            _everReady = true;
            _readyAt = Stopwatch.GetTimestamp();
            ViteOrigin = ready.ViteOrigin;
            _ready.TrySetResult(new Endpoint(new Uri($"http://127.0.0.1:{ready.Port}/"), process));
        }

        logger.LogInformation(
            "Netjes sidecar ready ({Mode}){Vite}", context.Mode, ViteOrigin is null ? "" : $", Vite dev server at {ViteOrigin}");
        return true;
    }

    /// <summary>A process that cannot start says why before it exits; its exit then fails with that.</summary>
    private bool TryAcceptFailure(Process process, string json)
    {
        FailedMessage? failed;
        try
        {
            failed = JsonSerializer.Deserialize<FailedMessage>(json);
        }
        catch (JsonException)
        {
            return false;
        }

        Exception? failure = failed switch
        {
            { Reason: "vite-port-in-use", Port: { } port } => new VitePortInUseException(port),
            { Reason: "vite-failed", Message: { } message } =>
                new ViteDevServerFailedException(message, failed.Code is "ERR_MODULE_NOT_FOUND" or "MODULE_NOT_FOUND", context.Root),
            _ => null,
        };
        lock (_gate)
        {
            if (failure is null || _processReported || !ReferenceEquals(process, _process))
            {
                return false;
            }

            _processFailure = failure;
        }

        return true;
    }

    private void OnStderr(string? line)
    {
        if (line is null)
        {
            return;
        }

        line = PlainText(line);
        lock (_recentStderr)
        {
            _recentStderr.Enqueue(line);
            while (_recentStderr.Count > StderrLinesKept)
            {
                _recentStderr.Dequeue();
            }
        }

        logger.LogWarning("{Output}", line);
    }

    /// <summary>
    /// A line of the process's output without terminal escape sequences, the pattern <c>sidecar/server.mjs</c> strips
    /// from render errors: Vite logs the code frame of a page's syntax error in colour in a pipe too, and a log file or
    /// the error page (the last lines are an exit's <see cref="SsrRenderException.JsStack"/>) shows the codes as text.
    /// </summary>
    private static string PlainText(string line) => TerminalEscapes().Replace(line, "");

    [GeneratedRegex(@"(?:\x1b\[|\x9b)[0-?]*[ -/]*[@-~]|\x1b[\]PX^_][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[ -/]*[0-~]")]
    private static partial Regex TerminalEscapes();

    private async Task OnExitedAsync(Process process)
    {
        try
        {
            await HandleExitAsync(process);
        }
        catch (Exception ex)
        {
            // Nothing awaits this. The restart is scheduled before the handler logs anything, so a log sink that fails
            // does not keep the process from coming back.
            logger.LogError(ex, "Handling the Node sidecar's exit failed: {Message}", ex.Message);
        }
    }

    private async Task HandleExitAsync(Process process)
    {
        // A process that was ready stops counting as ready at once, rather than once its last output is in.
        TaskCompletionSource<Endpoint>? ready = null;
        lock (_gate)
        {
            if (!_stopping && ReferenceEquals(process, _process) && _ready.Task.IsCompletedSuccessfully)
            {
                ready = _ready;
            }
        }

        // Only its log line can throw, and the restart must not depend on the log.
        Exception? logFailure = null;
        if (ready is not null && ReferenceEquals(ready.Task.Result.Process, process))
        {
            try
            {
                Withdraw(ready, ready.Task.Result);
            }
            catch (Exception ex)
            {
                logFailure = ex;
            }
        }

        // Exited can fire before the last stderr lines are delivered; WaitForExitAsync waits for them, so the failure
        // carries what the process printed on its way out, but not for longer than OutputGrace.
        try
        {
            await process.WaitForExitAsync().WaitAsync(OutputGrace);
        }
        catch (TimeoutException)
        {
        }
        catch (InvalidOperationException)
        {
            // Disposed while stopping.
        }

        string stderr;
        int exitCode;
        TimeSpan delay;
        lock (_gate)
        {
            if (_stopping || process != _process)
            {
                return;
            }

            lock (_recentStderr)
            {
                stderr = string.Join('\n', _recentStderr);
            }

            exitCode = process.ExitCode;
            var failure = _processFailure ?? new SsrRenderException($"The Node sidecar exited with code {exitCode}.", stderr);
            // Renders waiting for this process to come up fail with its exit. Those for one that was ready wait for the
            // restart, on the source put in place when it was taken out.
            if (!_processReported || _ready.Task.IsCompleted)
            {
                var previous = _ready;
                _ready = NewReadySource();
                previous.TrySetException(failure);
            }

            if (!_everReady)
            {
                // It never came up: restarting would fail the same way, so surface the error instead.
                _ready.TrySetException(failure);
                return;
            }

            if (_processReported && RestartPolicy.IsStable(Stopwatch.GetElapsedTime(_readyAt)))
            {
                _restarts = 0;
            }

            delay = RestartPolicy.DelayAfter(_restarts++);
            _ = RestartAfterAsync(delay);
        }

        logger.LogError(
            "The Node sidecar exited with code {ExitCode}; restarting in {Delay}s. Last output:\n{Stderr}",
            exitCode, delay.TotalSeconds, stderr);
        if (logFailure is not null)
        {
            ExceptionDispatchInfo.Throw(logFailure);
        }
    }

    private async Task RestartAfterAsync(TimeSpan delay)
    {
        await Task.Delay(delay);
        Exception failure;
        TimeSpan next;
        lock (_gate)
        {
            if (_stopping)
            {
                return;
            }

            var previous = _process;
            try
            {
                if (TryLaunch(out failure))
                {
                    return;
                }
            }
            catch (Exception) when (!ReferenceEquals(_process, previous))
            {
                // The process started; what failed came after (its log line). Another launch would replace it.
                return;
            }
            catch (Exception ex)
            {
                // Retried like any launch that failed.
                failure = ex;
            }

            // Renders keep waiting (up to their timeout) while we retry, e.g. until `npm install` puts the script back.
            next = RestartPolicy.DelayAfter(_restarts++);
            _ = RestartAfterAsync(next);
        }

        try
        {
            logger.LogError("Could not restart the Node sidecar: {Message} Retrying in {Delay}s.", failure.Message, next.TotalSeconds);
        }
        catch (Exception)
        {
            // The log sink failed; the retry is scheduled regardless, and there is nowhere left to say so.
        }
    }

    private static SidecarError? TryParseError(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<SidecarErrorResponse>(body)?.Error;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The ready process, waiting for one if need be.</summary>
    private async Task<Ready> ReadyAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<Endpoint> source;
        lock (_gate)
        {
            source = _ready;
        }

        try
        {
            return new Ready(source, await source.Task.WaitAsync(cancellationToken));
        }
        catch (OperationCanceledException ex)
        {
            throw new NotReadyException(ex);
        }
    }

    /// <summary>The ready process, or null if there is none right now.</summary>
    private Ready? ReadyNow()
    {
        lock (_gate)
        {
            return _ready.Task.IsCompletedSuccessfully ? new Ready(_ready, _ready.Task.Result) : null;
        }
    }

    /// <summary>A ready process's address and process, and the source it came from, which taking it out replaces.</summary>
    private readonly record struct Ready(TaskCompletionSource<Endpoint> Source, Endpoint Endpoint);

    /// <summary>Cancelled while waiting for a ready process, so a render's time limit can be told from a slow render.</summary>
    private sealed class NotReadyException(OperationCanceledException inner)
        : OperationCanceledException(inner.Message, inner, inner.CancellationToken);

    /// <summary>
    /// Takes a process that has exited, or did not take a connection, out of the ready ones, whether a request or the
    /// exit handler noticed first: requests wait for the process that is ready next (the restart, which the exit handler
    /// schedules), and the health check and the pool see it as restarting. Returns false while stopping, when nothing
    /// comes next.
    /// </summary>
    private bool Withdraw(TaskCompletionSource<Endpoint> source, Endpoint endpoint, Exception? connectFailure = null)
    {
        lock (_gate)
        {
            if (_stopping)
            {
                return false;
            }

            if (!ReferenceEquals(source, _ready))
            {
                // Another request took it out already.
                return true;
            }

            _ready = NewReadySource();
            _ = RestoreIfRunningAsync(_ready, endpoint);
        }

        if (connectFailure is null)
        {
            logger.LogWarning("The Node sidecar {Pid} has exited; it takes no renders until it restarts.", endpoint.Pid);
        }
        else
        {
            logger.LogWarning(
                "The Node sidecar {Pid} did not take a connection ({Message}); it takes no renders until it restarts, or for {Grace}s if it keeps running.",
                endpoint.Pid, connectFailure.Message, RefusalGrace.TotalSeconds);
        }

        return true;
    }

    /// <summary>
    /// A withdrawn process that is still running after <see cref="RefusalGrace"/> was refusing for a moment only (a
    /// full accept queue refuses connections on Windows): it is ready again, rather than waiting for an exit that
    /// does not come.
    /// </summary>
    private async Task RestoreIfRunningAsync(TaskCompletionSource<Endpoint> withdrawn, Endpoint endpoint)
    {
        await Task.Delay(RefusalGrace);
        lock (_gate)
        {
            if (_stopping || !ReferenceEquals(withdrawn, _ready) || HasExited(endpoint.Process))
            {
                return;
            }

            withdrawn.TrySetResult(endpoint);
        }

        logger.LogWarning("The Node sidecar {Pid} kept running; it takes renders again.", endpoint.Pid);
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            // Disposed after it exited.
            return true;
        }
    }

    private static TaskCompletionSource<Endpoint> NewReadySource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Where a ready process listens, and the process, so its address is never taken for another's.</summary>
    private sealed record Endpoint(Uri Uri, Process Process)
    {
        /// <summary>Kept for the logs: the process is disposed once its restart has launched.</summary>
        public int Pid { get; } = Process.Id;
    }

    private sealed record ReadyMessage(
        [property: JsonPropertyName("port")] int Port,
        [property: JsonPropertyName("viteOrigin")] string? ViteOrigin);

    private sealed record FailedMessage(
        [property: JsonPropertyName("reason")] string? Reason,
        [property: JsonPropertyName("port")] int? Port,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("code")] string? Code);

    private sealed record SidecarErrorResponse([property: JsonPropertyName("error")] SidecarError? Error);

    private sealed record SidecarError(
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("stack")] string? Stack);
}
