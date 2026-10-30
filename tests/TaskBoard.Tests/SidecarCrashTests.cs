using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Npgsql;
using Testcontainers.PostgreSql;

namespace TaskBoard.Tests;

/// <summary>
/// The Node sidecar dies while it streams a document. The app runs as in production: its own process
/// (<c>dotnet TaskBoard.dll</c> in the Production environment, behind a trusted proxy that says https), the built client
/// and server, the real sidecar, and its own Postgres. The task page streams its activity feed after the shell
/// (Page.Defer); a lock on the activity table holds that query back, so the document is still open when the sidecar is
/// killed. The response must be cut rather than end as if it were whole, a request while the sidecar restarts must still
/// get the page, the sidecar must come back, and the next render must be complete.
/// <para>
/// Needs the production build (<c>npm run build</c>) and finds the sidecar with <c>pgrep</c>, so it runs on Linux and
/// macOS. The app listens on <c>TASKBOARD_TEST_PORT</c>, or a free port when that is not set.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
public sealed class SidecarCrashTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17.11-alpine").Build();
    private readonly ConcurrentQueue<string> _output = new();
    private Process? _app;
    private Uri? _address;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Finds the sidecar process with pgrep.");
        await _postgres.StartAsync(Ct);
        using (var migrate = Start("migrate"))
        {
            await migrate.WaitForExitAsync(Ct);
            Assert.True(migrate.ExitCode == 0, Output());
        }

        _app = Start();
        _address = await ListeningAsync(_app);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is { HasExited: false })
        {
            _app.Kill(entireProcessTree: true);
            await _app.WaitForExitAsync(CancellationToken.None);
        }

        _app?.Dispose();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task ASidecarKilledMidStream_CutsTheResponse_AndTheNextRenderIsWhole()
    {
        var owner = NewBrowser();
        await owner.SignUpAsync("Owner");
        var slug = ProjectsTests.NewSlug();
        await owner.ActionAsync("projects.create", new { name = "Streaming", slug });
        var (_, created) = await owner.ActionAsync("tasks.create", new { projectSlug = slug, title = "Survive a crash" });
        var url = $"/projects/{slug}/tasks/{(int)created["result"]!["number"]!}";
        var activity = $"created {slug.ToUpperInvariant()}-1";
        var whole = await owner.Http.GetStringAsync(url, Ct);
        Assert.Contains(activity, whole, StringComparison.Ordinal);
        // React closes the document once every boundary is in; the deferred value's own script follows it.
        Assert.Contains("</body></html>", whole, StringComparison.Ordinal);

        var received = new StringBuilder();
        HttpStatusCode status;
        Exception? cut;
        var sidecar = Assert.Single(SidecarProcesses());
        await using (await HoldActivityAsync())
        {
            using var response = await owner.Http.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead, Ct);
            status = response.StatusCode;
            await using var body = await response.Content.ReadAsStreamAsync(Ct);
            await ReadUntilAsync(body, received, "Loading the history");
            using (var process = Process.GetProcessById(sidecar))
            {
                process.Kill();
                await process.WaitForExitAsync(Ct);
            }

            cut = await Record.ExceptionAsync(() => ReadToEndAsync(body, received));
        }

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(cut is HttpRequestException or IOException, $"The response ended as if whole ({cut}):\n{received}");
        Assert.DoesNotContain("</html>", received.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(activity, received.ToString(), StringComparison.Ordinal);

        // A request while the sidecar restarts gets the page either way: it waits for the restart if the exit has not
        // been seen yet, and once no sidecar is ready it gets the page to render in the browser (ADR 0013).
        var during = await owner.Http.GetAsync(url, Ct);
        Assert.True(during.StatusCode == HttpStatusCode.OK, $"{during.StatusCode}\n{Output()}");
        Assert.Contains("Survive a crash", await during.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // The sidecar comes back, and the next render is whole and from the server, the streamed feed included.
        await HealthyAsync(owner.Http);
        var next = await owner.Http.GetAsync(url, Ct);
        var html = await next.Content.ReadAsStringAsync(Ct);
        Assert.True(next.StatusCode == HttpStatusCode.OK, $"{next.StatusCode}\n{Output()}");
        Assert.DoesNotContain("No Node sidecar was ready", html, StringComparison.Ordinal);
        Assert.Contains("Survive a crash", html, StringComparison.Ordinal);
        Assert.Contains(activity, html, StringComparison.Ordinal);
        Assert.Contains("</body></html>", html, StringComparison.Ordinal);
        Assert.NotEqual(sidecar, Assert.Single(SidecarProcesses()));
    }

    /// <summary>Waits for the health check to report the restarted sidecar ready (it restarts after a second).</summary>
    private async Task HealthyAsync(HttpClient http)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var status = HttpStatusCode.ServiceUnavailable;
        var body = "";
        try
        {
            while (!(status == HttpStatusCode.OK && body == "Healthy"))
            {
                await Task.Delay(100, deadline.Token);
                using var health = await http.GetAsync("/healthz", deadline.Token);
                status = health.StatusCode;
                body = await health.Content.ReadAsStringAsync(deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            Assert.Fail($"/healthz: {status} {body}\n{Output()}");
        }
    }

    /// <summary>Takes a lock that the activity query waits for (and nothing else on the task page does) until disposed.</summary>
    private async Task<IAsyncDisposable> HoldActivityAsync()
    {
        var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync(Ct);
        var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var command = new NpgsqlCommand("LOCK TABLE activity IN ACCESS EXCLUSIVE MODE", connection, transaction))
        {
            await command.ExecuteNonQueryAsync(Ct);
        }

        return new Release(async () =>
        {
            await transaction.RollbackAsync(CancellationToken.None);
            await connection.DisposeAsync();
        });
    }

    private sealed class Release(Func<Task> release) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await release();
    }

    private Process Start(params string[] args)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet"));
        var start = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = TaskBoardApp.ExampleRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "TaskBoard.dll"));
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{Environment.GetEnvironmentVariable("TASKBOARD_TEST_PORT") ?? "0"}";
        start.Environment["ConnectionStrings__TaskBoard"] = _postgres.GetConnectionString();
        // The test is the proxy: it says the visitor came over https, so the Secure auth cookie is set and read.
        start.Environment["ForwardedHeaders__KnownNetworks__0"] = "127.0.0.1/32";
        start.Environment["AuthLimits__SignInsPerAddress"] = "100000";
        start.Environment["AuthLimits__SignUpsPerAddress"] = "100000";
        var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) _output.Enqueue(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) _output.Enqueue(e.Data);
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private string Output() => string.Join('\n', _output);

    private async Task<Uri> ListeningAsync(Process app)
    {
        const string prefix = "Now listening on: ";
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (_output.FirstOrDefault(line => line.Contains(prefix, StringComparison.Ordinal)) is { } line)
            {
                return new Uri(line[(line.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length)..].Trim());
            }

            Assert.False(app.HasExited, $"The app exited:\n{Output()}");
            await Task.Delay(100, Ct);
        }

        throw new TimeoutException($"The app did not start listening:\n{Output()}");
    }

    private Browser NewBrowser()
    {
        var http = new HttpClient(new CookieJar(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false }))
        {
            BaseAddress = _address,
        };
        http.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        return new Browser(http);
    }

    /// <summary>Cookies by name, sent back whatever their Secure flag: the trusted proxy in front speaks https to the browser.</summary>
    private sealed class CookieJar(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly ConcurrentDictionary<string, string> _cookies = new(StringComparer.Ordinal);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!_cookies.IsEmpty)
            {
                request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}")));
            }

            var response = await base.SendAsync(request, cancellationToken);
            foreach (var setCookie in response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [])
            {
                var pair = setCookie.Split(';', 2)[0];
                var separator = pair.IndexOf('=', StringComparison.Ordinal);
                _cookies[pair[..separator]] = pair[(separator + 1)..];
            }

            return response;
        }
    }

    /// <summary>Reads until the text so far contains <paramref name="marker"/>; the body must not end first.</summary>
    private static async Task ReadUntilAsync(Stream body, StringBuilder text, string marker)
    {
        var buffer = new byte[16 * 1024];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (!text.ToString().Contains(marker, StringComparison.Ordinal))
        {
            var read = await body.ReadAsync(buffer, deadline.Token);
            Assert.True(read > 0, $"The document ended before '{marker}':\n{text}");
            text.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
    }

    private static async Task ReadToEndAsync(Stream body, StringBuilder text)
    {
        var buffer = new byte[16 * 1024];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        int read;
        while ((read = await body.ReadAsync(buffer, deadline.Token)) > 0)
        {
            text.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
    }

    /// <summary>The production sidecars of the app this test started (they are its child processes).</summary>
    private List<int> SidecarProcesses()
    {
        using var pgrep = Process.Start(new ProcessStartInfo("pgrep", ["-P", _app!.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), "-f", "netjes-sidecar.mjs"])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        var output = pgrep.StandardOutput.ReadToEnd();
        pgrep.WaitForExit();
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)];
    }
}
