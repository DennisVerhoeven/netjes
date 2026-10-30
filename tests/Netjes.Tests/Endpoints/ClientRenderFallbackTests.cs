using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netjes.Export;
using Netjes.Routing;
using Netjes.Sidecar;
using Netjes.TypeGen;

namespace Netjes.Tests.Endpoints;

/// <summary>
/// Documents while no Node sidecar is ready: .NET writes a shell with the payload, and the browser renders the page.
/// The sidecar here (down-sidecar.mjs) reports ready once, so the app starts, then exits and never comes back.
/// </summary>
public partial class ClientRenderFallbackTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Pages = ["app/page.tsx", "app/layout.tsx", "app/posts/[id]/page.tsx", "app/not-found.tsx", "app/error.tsx"];

    /// <summary>What <c>npm run build</c> leaves: the client manifest, with the client entry's CSS and a layout chunk.</summary>
    private static void WriteBuild(string root) => WriteBuild(root, Pages);

    private static void WriteBuild(string root, IReadOnlyList<string> pages)
    {
        var client = Path.Combine(root, "dist", "client");
        Directory.CreateDirectory(Path.Combine(client, ".vite"));
        var chunks = pages.Select((file, i) =>
            $"\"{file}\": {{ \"file\": \"assets/chunk{i}.js\", \"src\": \"{file}\", \"isDynamicEntry\": true }}");
        // The payload's version is a hash of this text, which the golden file pins, and a raw literal keeps the line
        // endings of the checkout: so the same "\n" on every one.
        File.WriteAllText(Path.Combine(client, ".vite", "manifest.json"), $$"""
            {
              "node_modules/netjes/dist/client-entry.js": {
                "file": "assets/client-entry-AAA.js", "src": "node_modules/netjes/dist/client-entry.js", "isEntry": true,
                "css": ["assets/client-entry-BBB.css"]
              },
              {{string.Join(",\n", chunks)}}
            }
            """.ReplaceLineEndings("\n"));
    }

    private static async Task<TestApp> StartAsync(
        Action<WebApplication>? map = null,
        Action<NetjesOptions>? configure = null,
        Action<IServiceCollection>? services = null,
        Action<WebApplication>? configureApp = null,
        Action<WebApplication>? beforeNetjes = null)
    {
        var app = await TestApp.StartAsync(
            [],
            map,
            o =>
            {
                // Only the test with the fallback off waits for a sidecar.
                o.RenderTimeout = TimeSpan.FromSeconds(1);
                configure?.Invoke(o);
            },
            services,
            environment: "Production",
            prepareRoot: WriteBuild,
            configureApp: configureApp,
            sidecar: "down-sidecar.mjs",
            beforeNetjes: beforeNetjes);
        await NoSidecarReadyAsync(app.App.Services);
        return app;
    }

    /// <summary>Waits until the pool has seen its process exit, which down-sidecar.mjs does right after reporting ready.</summary>
    private static async Task NoSidecarReadyAsync(IServiceProvider services)
    {
        var pool = services.GetRequiredService<SidecarPool>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (pool.ReadyCount > 0)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    /// <summary>The payload the shell carries for the browser.</summary>
    private static JsonNode PayloadIn(string html)
    {
        var match = PayloadRegex().Match(html);
        Assert.True(match.Success, $"no payload in {html}");
        return JsonNode.Parse(match.Groups[1].Value)!;
    }

    [Fact]
    public async Task Document_WhenNoSidecarIsReady_GetsAShellForTheBrowserToRender()
    {
        await using var app = await StartAsync(map: a => a.MapPageLoader("/posts/[id]", (string id) => Page.Props(new { Title = $"Post {id}" })));

        var response = await app.GetDocumentAsync("/posts/7");
        var html = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">", html);
        Assert.Contains("<link rel=\"stylesheet\" href=\"/assets/client-entry-BBB.css\">", html);
        Assert.Contains("<link rel=\"modulepreload\" href=\"/assets/chunk1.js\">", html);
        Assert.Contains("<script type=\"module\" src=\"/assets/client-entry-AAA.js\" async></script></head>", html);
        // An empty body: the payload, and a comment that tells the client entry the payload is complete.
        Assert.Matches("<body><script id=\"__NETJES_DATA__\" type=\"application/json\">[^<]*</script><!--[^<]*--></body></html>$", html);
        var payload = PayloadIn(html);
        Assert.Equal("app/posts/[id]/page.tsx", (string?)payload["page"]);
        Assert.Equal("Post 7", (string?)payload["props"]!["title"]);
        Assert.Equal(200, (int?)payload["status"]);
        Assert.True((bool?)payload["document"]!["clientRender"]);
    }

    [Fact]
    public async Task Shell_CarriesTheCspNonceOnEverythingItLoads()
    {
        await using var app = await StartAsync(configure: o => o.ContentSecurityPolicy = "script-src 'nonce-{nonce}' 'strict-dynamic'; object-src 'none'");

        var response = await app.GetDocumentAsync("/");
        var html = await response.Content.ReadAsStringAsync(Ct);

        var nonce = Regex.Match(response.Headers.GetValues("Content-Security-Policy").Single(), "'nonce-([^']+)'").Groups[1].Value;
        Assert.NotEmpty(nonce);
        var tags = Regex.Matches(html, "<(script|link|meta property=\"csp-nonce\")[^>]*>").Select(m => m.Value).ToList();
        // The bootstrap, the stylesheet, the preloads, the client entry, the payload and Vite's meta.
        Assert.True(tags.Count >= 6, string.Join('\n', tags));
        Assert.All(tags, tag => Assert.Equal(nonce, WebUtility.HtmlDecode(Regex.Match(tag, "nonce=\"([^\"]*)\"").Groups[1].Value)));
        Assert.Equal(nonce, (string?)PayloadIn(html)["document"]!["nonce"]);
    }

    [Fact]
    public async Task Shell_HasTheTitleDescriptionAndRobotsFromTheLoader()
    {
        await using var app = await StartAsync(map: a => a.MapPageLoader("/posts/[id]", (string id) => Page.Props(new { Id = id }).WithMetadata(new PageMetadata
        {
            Title = "Fish & <chips>",
            Description = "Say \"hi\"",
            Robots = new PageMetadataRobots(Index: false),
        })));

        var html = await (await app.GetDocumentAsync("/posts/7")).Content.ReadAsStringAsync(Ct);

        var head = html[..html.IndexOf("</head>", StringComparison.Ordinal)];
        Assert.Contains("<title>Fish &amp; &lt;chips&gt;</title>", head);
        Assert.Contains("<meta name=\"description\" content=\"Say &quot;hi&quot;\">", head);
        Assert.Contains("<meta name=\"robots\" content=\"noindex, follow\">", head);
        Assert.Equal("Fish & <chips>", (string?)PayloadIn(html)["metadata"]!["title"]);
    }

    [Fact]
    public async Task Shell_WithoutMetadataFromTheLoader_HasNoTitle()
    {
        await using var app = await StartAsync();

        var html = await (await app.GetDocumentAsync("/")).Content.ReadAsStringAsync(Ct);

        Assert.DoesNotContain("<title>", html);
        Assert.DoesNotContain("name=\"description\"", html);
    }

    [Fact]
    public async Task Document_WithTheFallbackOff_WaitsForARestartAndFailsAsBefore()
    {
        await using var app = await StartAsync(configure: o => o.ClientRenderFallback = false);

        var response = await app.GetDocumentAsync("/");
        var html = await response.Content.ReadAsStringAsync(Ct);

        // The page and then the error page each wait a RenderTimeout for a sidecar; then the plain error page.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("<h1>500 Internal Server Error</h1>", html);
        Assert.DoesNotContain("__NETJES_DATA__", html);
    }

    [Fact]
    public async Task DataRequest_WhenNoSidecarIsReady_IsTheUsualPayload()
    {
        await using var app = await StartAsync(map: a => a.MapPageLoader("/posts/[id]", (string id) => Page.Props(new { Title = $"Post {id}" })));

        var payload = await app.GetDataJsonAsync("/posts/7");

        Assert.Equal("Post 7", (string?)payload["props"]!["title"]);
        Assert.Null(payload["document"]);
    }

    [Fact]
    public async Task Document_ForALoaderThatReturnsNotFound_GetsTheNotFoundPageWithItsStatus()
    {
        await using var app = await StartAsync(map: a => a.MapPageLoader("/posts/[id]", (string id) => Page.NotFound()));

        var response = await app.GetDocumentAsync("/posts/7");
        var payload = PayloadIn(await response.Content.ReadAsStringAsync(Ct));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("app/not-found.tsx", (string?)payload["page"]);
        Assert.Equal("notFound", (string?)payload["pageKind"]);
        Assert.Equal(404, (int?)payload["status"]);
    }

    [Fact]
    public async Task Document_ForAnUnknownUrl_GetsTheNotFoundPage()
    {
        await using var app = await StartAsync();

        var response = await app.GetDocumentAsync("/no/such/page");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("app/not-found.tsx", (string?)PayloadIn(await response.Content.ReadAsStringAsync(Ct))["page"]);
    }

    [Fact]
    public async Task Document_ForALoaderThatThrows_GetsTheErrorPageWithItsStatus()
    {
        await using var app = await StartAsync(map: a => a.MapPageLoader("/posts/[id]", Func<string, PageResult> (string id) => throw new InvalidOperationException("db down")));

        var response = await app.GetDocumentAsync("/posts/7");
        var payload = PayloadIn(await response.Content.ReadAsStringAsync(Ct));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("app/error.tsx", (string?)payload["page"]);
        Assert.Equal("error", (string?)payload["pageKind"]);
        // Production: no details.
        Assert.Equal("Internal Server Error", (string?)payload["props"]!["message"]);
        Assert.True((bool?)payload["document"]!["clientRender"]);
    }

    [Fact]
    public async Task Document_ForALoaderThatRedirects_IsRedirectedAsBefore()
    {
        await using var app = await StartAsync(map: a => a.MapPageLoader("/posts/[id]", (string id) => Page.Redirect("/")));

        var response = await app.GetDocumentAsync("/posts/7");

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Document_WhenNoSidecarIsReady_IsNeverStoredInTheOutputCache()
    {
        var loads = 0;
        await using var app = await StartAsync(
            map: a => a.MapPageLoader("/", () => Page.Props(new { Load = Interlocked.Increment(ref loads) })).CachePage(TimeSpan.FromMinutes(5)),
            services: s => s.AddOutputCache(),
            configureApp: a => a.UseOutputCache());

        var first = await app.GetDocumentAsync("/");
        var second = await app.GetDocumentAsync("/");

        Assert.Equal(2, loads);
        Assert.Equal(2, (int?)PayloadIn(await second.Content.ReadAsStringAsync(Ct))["props"]!["load"]);
        Assert.Equal("no-store", first.Headers.CacheControl?.ToString());
    }

    /// <summary>
    /// A returning visitor, with the antiforgery cookie from a first visit: ASP.NET Core sends the response that stores
    /// it its own <c>Cache-Control</c>, in place of the page's.
    /// </summary>
    private static async Task<HttpResponseMessage> ReturningVisitAsync(TestApp app, string url, bool data)
    {
        var first = await app.GetDataAsync(url);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", first.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        if (data)
        {
            request.Headers.Add(NetjesHeaders.Data, "1");
        }

        return await app.Client.SendAsync(request, Ct);
    }

    private static void MapPageWithItsOwnCacheControl(WebApplication app) => app.MapPageLoader("/", (HttpContext http) =>
    {
        http.Response.Headers.CacheControl = "public, max-age=600";
        return Page.Props();
    });

    /// <summary>
    /// The shell is a degraded page: a CDN or browser that kept it would go on showing it after the sidecars are back,
    /// so it is never kept, whatever the page allows (as for draft mode).
    /// </summary>
    [Fact]
    public async Task Document_WhenNoSidecarIsReady_GoesOutNoStore_OverridingTheHeaderThePageSet()
    {
        await using var app = await StartAsync(map: MapPageWithItsOwnCacheControl);

        var response = await ReturningVisitAsync(app, "/", data: false);

        Assert.Contains("__NETJES_DATA__", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    public static TheoryData<string, string, bool> CdnHeaderCases => CdnCacheHeaders.SourcesCasingsAndCaching();

    /// <summary>
    /// A CDN that reads its own header ahead of <c>Cache-Control</c> would go on serving the shell after the sidecars
    /// are back, so it goes out without them, wherever the page or a middleware set them. Client navigations, which
    /// need no sidecar, keep them.
    /// </summary>
    [Theory]
    [MemberData(nameof(CdnHeaderCases))]
    public async Task Document_WhenNoSidecarIsReady_GoesOutWithoutTheHeadersACdnReadsAheadOfCacheControl(
        string source, string casing, bool outputCache)
    {
        await using var app = await StartAsync(
            map: a =>
            {
                var page = a.MapPageLoader("/", (HttpContext http) =>
                {
                    CdnCacheHeaders.FromLoader(http, source, casing);
                    return Page.Props();
                });
                if (outputCache)
                {
                    page.CachePage(TimeSpan.FromMinutes(5));
                }
            },
            services: outputCache ? s => s.AddOutputCache() : null,
            configureApp: a =>
            {
                if (outputCache)
                {
                    a.UseOutputCache();
                }

                CdnCacheHeaders.AfterNetjes(a, source, casing);
            },
            beforeNetjes: CdnCacheHeaders.BeforeNetjes(source, casing));

        var document = await app.GetDocumentAsync("/");
        var data = await app.GetDataAsync("/");

        Assert.Contains("__NETJES_DATA__", await document.Content.ReadAsStringAsync(Ct));
        Assert.Equal("no-store", document.Headers.CacheControl?.ToString());
        Assert.Empty(CdnCacheHeaders.In(document));
        Assert.Equal(CdnCacheHeaders.Names.Length, CdnCacheHeaders.In(data).Count);
    }

    [Fact]
    public async Task DataRequest_WhenNoSidecarIsReady_KeepsTheHeaderThePageSet()
    {
        await using var app = await StartAsync(map: MapPageWithItsOwnCacheControl);

        var response = await ReturningVisitAsync(app, "/", data: true);

        Assert.Equal("public, max-age=600", response.Headers.CacheControl?.ToString());
    }

    private const string FallbackWarning = "the page was sent for the browser to render";

    [Fact]
    public async Task Document_WhenNoSidecarIsReady_LogsOneWarningWithTheRoute()
    {
        await using var app = await StartAsync(map: a => a.MapPageLoader("/posts/[id]", (string id) => Page.Props(new { Id = id })));

        await app.GetDocumentAsync("/posts/7");
        await app.GetDocumentAsync("/posts/8");
        await app.GetDataAsync("/posts/9");

        var warnings = app.Logs.Where(l => l.Message.Contains(FallbackWarning, StringComparison.Ordinal)).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w => Assert.Equal(LogLevel.Warning, w.Level));
        Assert.Equal("No Node sidecar was ready to render /posts/[id] (/posts/7); the page was sent for the browser to render.", warnings[0].Message);
    }

    [Fact]
    public async Task Document_WhenNoSidecarIsReady_IsCounted()
    {
        var counted = new List<(long Value, object? Route)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == NetjesTelemetry.Name && instrument.Name == "netjes.sidecar.fallbacks")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            lock (counted)
            {
                counted.Add((value, tags.ToArray().FirstOrDefault(t => t.Key == "netjes.route").Value));
            }
        });
        listener.Start();
        await using var app = await StartAsync(map: a => a.MapPageLoader("/posts/[id]", (string id) => Page.Props(new { Id = id })));

        await app.GetDocumentAsync("/posts/7");
        await app.GetDocumentAsync("/posts/8");
        await app.GetDataAsync("/posts/9");

        lock (counted)
        {
            Assert.Equal([(1L, (object?)"/posts/[id]"), (1L, "/posts/[id]")], counted);
        }
    }

    [Fact]
    public async Task Document_InDevelopment_NeverFallsBack()
    {
        // In development the sidecar also runs the Vite dev server that serves the client, so a shell would load nothing.
        await using var app = await TestApp.StartAsync(["app/page.tsx"], configure: o => o.RenderTimeout = TimeSpan.FromSeconds(1), sidecar: "down-sidecar.mjs");
        await NoSidecarReadyAsync(app.App.Services);

        var response = await app.GetDocumentAsync("/");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("__NETJES_DATA__", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Export_WhenNoSidecarIsReady_FailsAsBefore()
    {
        int? exitCode = null;
        await using var app = await TestApp.StartAsync(
            [],
            configure: o =>
            {
                o.RenderTimeout = TimeSpan.FromSeconds(1);
                o.Command = new NetjesCommandOptions { Name = "export", Output = "out" };
            },
            services: s =>
            {
                // Ahead of the export, which renders as soon as the app has started.
                s.Insert(0, ServiceDescriptor.Singleton<IHostedService>(sp => new WaitForNoSidecar(sp)));
                s.AddSingleton(new CommandExit(code => exitCode = code));
                s.AddSingleton(sp => new ExportClient(() => sp.GetRequiredService<IServer>() is TestServer server
                    ? server.CreateClient()
                    : throw new InvalidOperationException("Expected the test server.")));
            },
            environment: "Production",
            // No dynamic page, which the export would refuse before it renders anything.
            prepareRoot: root => WriteBuild(root, ["app/page.tsx", "app/not-found.tsx"]),
            sidecar: "down-sidecar.mjs");

        Assert.Equal(1, exitCode);
        Assert.Contains(app.Logs, l => l.Category == typeof(StaticExportService).FullName && l.Level == LogLevel.Error);
        Assert.False(File.Exists(Path.Combine(app.Root, "out", "index.html")));
        Assert.DoesNotContain(app.Logs, l => l.Message.Contains(FallbackWarning, StringComparison.Ordinal));
    }

    /// <summary>Holds the host's start until no sidecar is ready, so the export runs against none.</summary>
    private sealed class WaitForNoSidecar(IServiceProvider services) : IHostedLifecycleService
    {
        public Task StartedAsync(CancellationToken cancellationToken) => NoSidecarReadyAsync(services);

        public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task Shell_BootstrapIsTheDocumentsOwn_SoAHashBasedPolicyAllowsIt()
    {
        await using var app = await StartAsync();

        var html = await (await app.GetDocumentAsync("/")).Content.ReadAsStringAsync(Ct);

        // The package's tests tie DEFERRED_BOOTSTRAP to the hash docs/architecture.md gives.
        var source = await File.ReadAllTextAsync(Path.Combine(RepoRoot(), "packages", "netjes", "src", "deferred.ts"), Ct);
        var bootstrap = Regex.Match(source, "DEFERRED_BOOTSTRAP =\\s*\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(bootstrap);
        Assert.Contains($"<script>{bootstrap}</script>", html[..html.IndexOf("</head>", StringComparison.Ordinal)]);
    }

    /// <summary>
    /// The shell's head is .NET's copy of what <c>Document</c> renders. The package's Vitest suite renders this file's
    /// payload with <c>Document</c>, as a sidecar would, and compares the two heads
    /// (packages/netjes/test/client-render-shell.test.tsx), so a change to either fails one of the two tests until the
    /// other follows. Set UPDATE_GOLDEN=1 to rewrite the file after an intended change to the shell or the payload.
    /// </summary>
    [Fact]
    public async Task Shell_ForAFixturePage_MatchesTheGoldenFile()
    {
        await using var app = await StartAsync(
            configure: o => o.ContentSecurityPolicy = "script-src 'nonce-{nonce}' 'strict-dynamic'; object-src 'none'",
            // The same payload every run: a fixed clock, and no antiforgery token.
            services: s => s.AddSingleton<TimeProvider>(new FixedTime(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero))),
            map: a => a.MapPageLoader("/posts/[id]", (string id) => Page.Props(new { Id = id }).WithMetadata(new PageMetadata
            {
                Title = "Fish & <chips>",
                Description = "Say \"hi\"",
                Robots = new PageMetadataRobots(Index: false),
            })).WithMetadata(new OmitAntiforgeryTokenMetadata()));

        var response = await app.GetDocumentAsync("/posts/7");
        var html = await response.Content.ReadAsStringAsync(Ct);

        // Each document has a nonce of its own.
        var nonce = Regex.Match(response.Headers.GetValues("Content-Security-Policy").Single(), "'nonce-([^']+)'").Groups[1].Value;
        Assert.NotEmpty(nonce);
        AssertGolden(Path.Combine("client-render", "shell.html"), html.Replace(nonce, "fixture+nonce/A==", StringComparison.Ordinal) + "\n");
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static void AssertGolden(string name, string actual)
    {
        var path = Path.Combine(RepoRoot(), "packages", "netjes", "test", "fixtures", name);
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
        }

        Assert.True(File.Exists(path), $"{path} is missing; run with UPDATE_GOLDEN=1.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Netjes.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public async Task Health_WhileDocumentsFallBack_StaysUnhealthy()
    {
        await using var app = await StartAsync(services: s => s.AddHealthChecks().AddNetjes(), map: a => a.MapHealthChecks("/healthz"));

        var page = await app.GetDocumentAsync("/");
        var health = await app.GetDocumentAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
        Assert.Equal("Unhealthy", await health.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>The deferred values a shell hands the page, in order: (id, value as JSON, error).</summary>
    private static List<(string Id, string? Json, string? Error)> ValuesIn(string html) =>
        [.. ValueScriptRegex().Matches(html).Select(m => (
            JsonSerializer.Deserialize<string>(m.Groups["id"].Value)!,
            m.Groups["json"].Success ? JsonSerializer.Deserialize<string>(m.Groups["json"].Value) : null,
            m.Groups["error"].Success ? JsonSerializer.Deserialize<string>(m.Groups["error"].Value) : null))];

    [Fact]
    public async Task Shell_ForAPageWithDeferredValues_StreamsEachValueAfterThePayload()
    {
        var slow = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await StartAsync(
            configure: o => o.ContentSecurityPolicy = "script-src 'nonce-{nonce}' 'strict-dynamic'",
            map: a => a.MapPageLoader("/", () => Page.Props(new
            {
                Fast = Page.Defer(Task.FromResult(42)),
                Slow = Page.Defer(slow.Task),
                Broken = Page.Defer(Task.FromException<int>(new InvalidOperationException("secret"))),
            })));

        var response = await app.Client.GetAsync("/", HttpCompletionOption.ResponseHeadersRead, Ct);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(Ct));
        // The shell goes out first, while the slow value is still on its way.
        var shell = new System.Text.StringBuilder();
        var buffer = new char[4096];
        while (!shell.ToString().Contains("-->", StringComparison.Ordinal))
        {
            var read = await reader.ReadAsync(buffer, Ct);
            Assert.NotEqual(0, read);
            shell.Append(buffer, 0, read);
        }

        Assert.DoesNotContain(ValuesIn(shell.ToString()), v => v.Id == "d1");
        slow.SetResult("</script><b>&");
        var html = shell + await reader.ReadToEndAsync(Ct);

        var payload = PayloadIn(html);
        Assert.Equal("""{"$netjes":"deferred","id":"d1"}""", payload["props"]!["slow"]!.ToJsonString());
        // Each value as JSON.parse reads it. Values that are done when the loop looks go in the order they were deferred,
        // so the slow one can come before the failed one.
        Assert.Equal(
            [("d0", "42", null), ("d1", "</script><b>&", null), ("d2", null, "Something went wrong")],
            ValuesIn(html)
                .Select(v => (v.Id, v.Json is null ? null : JsonSerializer.Deserialize<JsonElement>(v.Json).ToString(), v.Error))
                .OrderBy(v => v.Id, StringComparer.Ordinal));
        // Inside a script element, nothing of the value may close it.
        Assert.Equal(Regex.Count(html, "<script"), Regex.Count(html, "</script>"));
        var nonce = (string?)payload["document"]!["nonce"];
        Assert.All(
            Regex.Matches(html, "<script[^>]*>self.__netjes_d").Select(m => m.Value),
            tag => Assert.Equal(nonce, WebUtility.HtmlDecode(Regex.Match(tag, "nonce=\"([^\"]*)\"").Groups[1].Value)));
        Assert.EndsWith("</script></body></html>", html);
    }

    [Fact]
    public async Task Shell_ForAValueThatDoesNotArriveInTime_ReportsItAsFailedAndEnds()
    {
        await using var app = await StartAsync(
            configure: o => o.StreamTimeout = TimeSpan.FromMilliseconds(500),
            map: a => a.MapPageLoader("/", () => Page.Props(new { Never = Page.Defer(new TaskCompletionSource<int>().Task) })));

        var html = await (await app.GetDocumentAsync("/")).Content.ReadAsStringAsync(Ct);

        Assert.Equal([("d0", null, "The value did not arrive in time")], ValuesIn(html));
        Assert.EndsWith("</script></body></html>", html);
    }

    [GeneratedRegex("""self\.__netjes_d\((?<id>"(?:[^"\\]|\\.)*"),(?:JSON\.parse\((?<json>"(?:[^"\\]|\\.)*")\)|null,(?<error>"(?:[^"\\]|\\.)*"))\)</script>""")]
    private static partial Regex ValueScriptRegex();

    [GeneratedRegex("<script id=\"__NETJES_DATA__\" type=\"application/json\"[^>]*>([^<]*)</script>")]
    private static partial Regex PayloadRegex();
}
