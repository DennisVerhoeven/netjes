using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Netjes.Export;
using Netjes.Rendering;
using Netjes.Tests.Endpoints;
using Netjes.TypeGen;

namespace Netjes.Tests.Export;

public class StaticExportTests
{
    private string _root = "";
    private int? _exitCode;

    /// <summary>Where the export writes, relative to the app root unless rooted. Default <c>out</c>.</summary>
    private string _output = "out";

    private string _out => Path.GetFullPath(_output, _root);

    /// <summary>The app's dist directory (<c>Netjes:DistDirectory</c>), which holds the client build. Default <c>dist</c>.</summary>
    private string _dist = "dist";

    public sealed record PostProps(string Slug);

    /// <summary>Runs the export command against a production build (a manifest listing <paramref name="files"/>).</summary>
    private Task<TestApp> ExportAsync(
        IEnumerable<string>? files = null,
        Action<WebApplication>? map = null,
        Action<IServiceCollection>? services = null,
        Action<string>? prepareRoot = null,
        Action<FakeSsrRenderer>? renderer = null,
        Action<NetjesOptions>? configure = null,
        Action<IEndpointConventionBuilder>? mapPages = null)
    {
        var pages = (files ?? ["app/page.tsx", "app/about/page.tsx"]).ToList();
        return TestApp.StartAsync(
            [],
            map: map,
            pages: mapPages,
            renderer: renderer,
            environment: "Production",
            prepareRoot: root =>
            {
                _root = root;
                WriteBuild(root, pages, _dist);
                prepareRoot?.Invoke(root);
            },
            configure: o =>
            {
                o.Command = new NetjesCommandOptions { Name = "export", Output = _output };
                o.DistDirectory = _dist;
                configure?.Invoke(o);
            },
            services: s =>
            {
                s.AddSingleton(new CommandExit(code => _exitCode = code));
                // TestServer has no network address; send the export's requests through it.
                s.AddSingleton(sp => new ExportClient(() => sp.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>() is TestServer server
                    ? server.CreateClient()
                    : throw new InvalidOperationException("Expected the test server.")));
                services?.Invoke(s);
            });
    }

    private static void WriteBuild(string root, IReadOnlyList<string> pages, string dist = "dist")
    {
        var client = Path.Combine(root, dist, "client");
        Directory.CreateDirectory(Path.Combine(client, ".vite"));
        Directory.CreateDirectory(Path.Combine(client, "assets"));
        var entries = pages.Select((file, i) => $"\"{file}\": {{ \"file\": \"assets/page{i}.js\", \"src\": \"{file}\", \"isDynamicEntry\": true }}");
        File.WriteAllText(
            Path.Combine(client, ".vite", "manifest.json"),
            $$"""
            {
              "../../packages/netjes/dist/client-entry.js": { "file": "assets/client-entry-AAA.js", "isEntry": true, "src": "../../packages/netjes/dist/client-entry.js" },
              {{string.Join(",\n", entries)}}
            }
            """);
        File.WriteAllText(Path.Combine(client, "assets", "index-CCC.js"), "export {}");
        File.WriteAllText(Path.Combine(client, "favicon.svg"), "<svg/>");
    }

    private string Out(string relative) => Path.Combine(_out, relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task Export_WritesEveryParameterlessPageAndThe404Page()
    {
        await using var app = await ExportAsync(["app/page.tsx", "app/about/page.tsx", "app/not-found.tsx"]);

        Assert.Equal(0, _exitCode);
        Assert.Equal("<!DOCTYPE html><html><body>rendered</body></html>", await File.ReadAllTextAsync(Out("index.html"), TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Out("about/index.html")));
        Assert.True(File.Exists(Out("404.html")));
        Assert.Contains(app.Renderer.Payloads, p => p.Contains("\"page\":\"app/not-found.tsx\""));
    }

    [Fact]
    public async Task Export_WhenNodeCannotBeStarted_ExportsNothing_AndSaysWhyInOneLine()
    {
        await using var app = await TestApp.StartAsync(
            [],
            environment: "Production",
            sidecar: "fake-sidecar.mjs",
            prepareRoot: root =>
            {
                _root = root;
                WriteBuild(root, ["app/page.tsx"]);
            },
            configure: o =>
            {
                o.Command = new NetjesCommandOptions { Name = "export", Output = _output };
                o.NodeExecutable = "netjes-test-no-such-node";
            },
            services: s =>
            {
                s.AddSingleton(new CommandExit(code => _exitCode = code));
                s.AddSingleton(sp => new ExportClient(() => ((TestServer)sp.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()).CreateClient()));
            });
        // As Run() does: the app stops by itself.
        await app.App.WaitForShutdownAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(1, _exitCode);
        var failure = Assert.Single(app.Logs, e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Error);
        Assert.StartsWith("Could not start 'netjes-test-no-such-node'.", failure.Message);
        Assert.False(Directory.Exists(_out));
    }

    [Fact]
    public async Task Export_CopiesTheClientBuildWithoutTheManifest()
    {
        await using var app = await ExportAsync();

        Assert.True(File.Exists(Out("assets/index-CCC.js")));
        Assert.True(File.Exists(Out("favicon.svg")));
        Assert.False(Directory.Exists(Out(".vite")));
    }

    [Fact]
    public async Task Export_OmitsTheAntiforgeryToken()
    {
        await using var app = await ExportAsync();

        Assert.NotEmpty(app.Renderer.Payloads);
        Assert.All(app.Renderer.Payloads, p => Assert.DoesNotContain("antiforgery", p));
    }

    [Fact]
    public async Task Export_PageWithADeferredValue_IsWrittenCompleteWithTheValueInPlace()
    {
        await using var app = await ExportAsync(
            ["app/page.tsx", "app/streaming/page.tsx", "app/streaming/loading.tsx"],
            map: a => a.MapPageLoader("/streaming", () =>
                Page.Props(new { Report = Page.Defer(Task.Delay(20).ContinueWith(_ => "42 items", TaskScheduler.Default)) })),
            renderer: r => r.StreamHandler = (_, options) => RenderLikeTheSidecarAsync(r, options));

        var html = await File.ReadAllTextAsync(Out("streaming/index.html"), TestContext.Current.CancellationToken);

        Assert.Equal(0, _exitCode);
        Assert.Contains(app.Renderer.Payloads, p => p.Contains("\"loading\":\"app/streaming/loading.tsx\""));
        Assert.Contains("<main><p>Report ready: \"42 items\"</p></main>", html);
        Assert.DoesNotContain("Loading the report", html);
        Assert.DoesNotContain("$RC", html);
        // The home page and the 404 page too: a static file gains nothing from being streamed.
        Assert.Equal(3, app.Renderer.StreamOptions.Count);
        Assert.All(app.Renderer.StreamOptions, o => Assert.True(o.WaitForAll));
    }

    /// <summary>
    /// What the sidecar sends for a page whose report is a deferred value under a loading.tsx boundary. Streamed: the
    /// shell with the fallback, then the report hidden after it with React's inline script to swap it in, which only a
    /// browser running JavaScript (and allowing the script) ever shows. Waiting for everything: the report in place.
    /// </summary>
    private static async Task<Stream> RenderLikeTheSidecarAsync(FakeSsrRenderer renderer, SsrStreamOptions options)
    {
        const string start = "<!DOCTYPE html><html><body><main>";
        const string end = "</main></body></html>";
        if (options.RenderId is not { } renderId)
        {
            return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(start + "rendered" + end));
        }

        if (options.WaitForAll)
        {
            var value = await DeliveredAsync(renderer, renderId, "d0");
            return new MemoryStream(System.Text.Encoding.UTF8.GetBytes($"{start}<p>Report ready: {value}</p>{end}"));
        }

        var pipe = new System.IO.Pipelines.Pipe();
        await pipe.Writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(
            start + "<!--$?--><template id=\"B:0\"></template><p>Loading the report…</p><!--/$--></main>"));
        _ = Task.Run(async () =>
        {
            var value = await DeliveredAsync(renderer, renderId, "d0");
            await pipe.Writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(
                $"<div hidden id=\"S:0\"><p>Report ready: {value}</p></div><script>$RC(\"B:0\",\"S:0\")</script></body></html>"));
            await pipe.Writer.CompleteAsync();
        });
        return pipe.Reader.AsStream();
    }

    /// <summary>The value .NET delivered to the render for a deferred id, as JSON.</summary>
    private static async Task<string> DeliveredAsync(FakeSsrRenderer renderer, string renderId, string id)
    {
        while (true)
        {
            if (renderer.Resolved.FirstOrDefault(r => r.RenderId == renderId && r.Id == id) is { RenderId: not null } delivered)
            {
                return delivered.Json ?? $"failed: {delivered.Error}";
            }

            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Export_RendersDynamicPagesForEachOfTheirStaticParams()
    {
        await using var app = await ExportAsync(
            ["app/blog/[slug]/page.tsx", "app/docs/[...path]/page.tsx"],
            map: a =>
            {
                a.MapPageLoader("/blog/[slug]", (string slug) => Page.Props(new PostProps(slug)))
                    .StaticParams(_ => [new { slug = "hello" }, new { slug = "a b" }]);
                a.MapPageLoader("/docs/[...path]", (string path) => Page.Props(new PostProps(path)))
                    .StaticParams(_ => [new { path = new[] { "guides", "deploy" } }]);
            });

        Assert.Equal(0, _exitCode);
        Assert.True(File.Exists(Out("blog/hello/index.html")));
        Assert.True(File.Exists(Out("blog/a b/index.html")));
        Assert.True(File.Exists(Out("docs/guides/deploy/index.html")));
        Assert.Contains(app.Renderer.Payloads, p => p.Contains("\"slug\":\"a b\""));
    }

    [Fact]
    public async Task Export_StaticParams_CanUseServices()
    {
        await using var app = await ExportAsync(
            ["app/blog/[slug]/page.tsx"],
            map: a => a.MapPageLoader("/blog/[slug]", (string slug) => Page.Props(new PostProps(slug)))
                .StaticParams<Slugs>(slugs => slugs.All.Select(s => new { slug = s })),
            services: s => s.AddSingleton(new Slugs(["one", "two"])));

        Assert.True(File.Exists(Out("blog/one/index.html")));
        Assert.True(File.Exists(Out("blog/two/index.html")));
    }

    public sealed record Slugs(IReadOnlyList<string> All);

    [Theory]
    [InlineData("../../escaped")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("/about")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("c:evil")]
    [InlineData("nul\0byte")]
    [InlineData("")]
    public async Task Export_ParamThatIsNoSafePathSegment_FailsBeforeWritingAnything(string slug)
    {
        await using var app = await ExportAsync(
            ["app/tags/[slug]/page.tsx"],
            map: a => a.MapPageLoader("/tags/[slug]", (string slug) => Page.Props(new PostProps(slug)))
                .StaticParams(_ => [new { slug }]));

        Assert.Equal(1, _exitCode);
        Assert.Contains(app.Logs, l => l.Message.Contains("/tags/[slug]"));
        Assert.False(Directory.Exists(_out));
        Assert.False(Directory.Exists(Path.Combine(_root, "escaped")));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a\\b")]
    public async Task Export_CatchAllPartThatIsNoSafePathSegment_Fails(string part)
    {
        await using var app = await ExportAsync(
            ["app/docs/[...path]/page.tsx"],
            map: a => a.MapPageLoader("/docs/[...path]", (string path) => Page.Props(new PostProps(path)))
                .StaticParams(_ => [new { path = new[] { "guides", part } }]));

        Assert.Equal(1, _exitCode);
        Assert.False(Directory.Exists(_out));
    }

    [Theory]
    [InlineData("Hello", "hello")]
    [InlineData("hello.", "hello")]
    [InlineData("hello ", "hello")]
    [InlineData("café", "café")]
    public async Task Export_TwoPagesWithOneOutputFile_FailBeforeWritingAnything(string first, string second)
    {
        await using var app = await ExportAsync(
            ["app/blog/[slug]/page.tsx"],
            map: a => a.MapPageLoader("/blog/[slug]", (string slug) => Page.Props(new PostProps(slug)))
                .StaticParams(_ => [new { slug = first }, new { slug = second }]));

        Assert.Equal(1, _exitCode);
        Assert.Contains(app.Logs, l => l.Message.Contains($"/blog/{Uri.EscapeDataString(first)} ")
            && l.Message.Contains($"/blog/{Uri.EscapeDataString(second)} ") && l.Message.Contains("same output file"));
        Assert.False(Directory.Exists(_out));
    }

    [Theory]
    [InlineData("con")]
    [InlineData("NUL")]
    [InlineData("aux")]
    [InlineData("Prn")]
    [InlineData("com1")]
    [InlineData("lpt9")]
    [InlineData("com¹")]
    [InlineData("nul.txt")]
    [InlineData("con.")]
    public async Task Export_PathSegmentThatIsAWindowsDeviceName_FailsBeforeWritingAnything(string slug)
    {
        await using var app = await ExportAsync(
            ["app/tags/[slug]/page.tsx"],
            map: a => a.MapPageLoader("/tags/[slug]", (string slug) => Page.Props(new PostProps(slug)))
                .StaticParams(_ => [new { slug }]));

        Assert.Equal(1, _exitCode);
        Assert.Contains(app.Logs, l => l.Message.Contains("(/tags/[slug])") && l.Message.Contains("Windows device"));
        Assert.False(Directory.Exists(_out));
    }

    [Theory]
    [InlineData("console")]
    [InlineData("auxiliary")]
    [InlineData("com10")]
    [InlineData("lpt")]
    [InlineData("nul-points")]
    public async Task Export_NameThatOnlyResemblesAWindowsDevice_IsExported(string slug)
    {
        await using var app = await ExportAsync(
            ["app/tags/[slug]/page.tsx"],
            map: a => a.MapPageLoader("/tags/[slug]", (string slug) => Page.Props(new PostProps(slug)))
                .StaticParams(_ => [new { slug }]));

        Assert.Equal(0, _exitCode);
        Assert.True(File.Exists(Out($"tags/{slug}/index.html")));
    }

    [Fact]
    public async Task Export_StaticParamsListingAPageTwice_IsNoCollision()
    {
        await using var app = await ExportAsync(
            ["app/blog/[slug]/page.tsx"],
            map: a => a.MapPageLoader("/blog/[slug]", (string slug) => Page.Props(new PostProps(slug)))
                .StaticParams(_ => [new { slug = "hello" }, new { slug = "hello" }]));

        Assert.Equal(0, _exitCode);
        Assert.True(File.Exists(Out("blog/hello/index.html")));
    }

    [Theory]
    [InlineData("...")]
    [InlineData(" ")]
    public async Task Export_SlugThatTrimsToNothing_CollidesWithTheParentPage(string slug)
    {
        // Windows drops trailing dots and spaces, so blog/.../index.html is blog/index.html there.
        await using var app = await ExportAsync(
            ["app/blog/page.tsx", "app/blog/[slug]/page.tsx"],
            map: a => a.MapPageLoader("/blog/[slug]", (string slug) => Page.Props(new PostProps(slug)))
                .StaticParams(_ => [new { slug }]));

        Assert.Equal(1, _exitCode);
        Assert.Contains(app.Logs, l => l.Message.Contains("(/blog)") && l.Message.Contains("(/blog/[slug])") && l.Message.Contains("same output file"));
        Assert.False(Directory.Exists(_out));
    }

    [Theory]
    [InlineData("about")]
    [InlineData("About")]
    public async Task Export_StaticParamsLandingOnAnotherPage_FailNamingBothRoutes(string slug)
    {
        await using var app = await ExportAsync(
            ["app/blog/about/page.tsx", "app/blog/[slug]/page.tsx"],
            map: a => a.MapPageLoader("/blog/[slug]", (string slug) => Page.Props(new PostProps(slug)))
                .StaticParams(_ => [new { slug }]));

        Assert.Equal(1, _exitCode);
        Assert.Contains(app.Logs, l => l.Message.Contains("(/blog/about)") && l.Message.Contains("(/blog/[slug])"));
        Assert.False(Directory.Exists(_out));
    }

    [Fact]
    public async Task Export_RedirectingPage_BecomesARefreshToItsTarget()
    {
        await using var app = await ExportAsync(["app/old/page.tsx"], map: a => a.MapPageLoader("/old", () => Page.Redirect("/new place")));

        var html = await File.ReadAllTextAsync(Out("old/index.html"), TestContext.Current.CancellationToken);

        Assert.Equal(0, _exitCode);
        Assert.Contains("""<meta http-equiv="refresh" content="0; url=/new place">""", html);
        Assert.Contains("""<a href="/new place">""", html);
    }

    [Theory]
    [InlineData("javascript%3Aalert(document.domain)")]
    [InlineData("javascript%3aalert(1)")]
    public async Task Export_RedirectToAnEncodedScheme_FailsOrStaysOnTheSite(string location)
    {
        await using var app = await ExportAsync(
            ["app/go/[slug]/page.tsx"],
            map: a => a.MapPageLoader("/go/[slug]", (string slug) => Page.Redirect(location)).StaticParams(_ => [new { slug = "x" }]));

        if (_exitCode != 0)
        {
            Assert.False(File.Exists(Out("go/x/index.html")));
            return;
        }

        var target = new Uri(new Uri("https://site.test/go/"), HrefOf(await File.ReadAllTextAsync(Out("go/x/index.html"), TestContext.Current.CancellationToken)));
        Assert.Equal("https", target.Scheme);
        Assert.Equal("site.test", target.Host);
    }

    [Theory]
    [InlineData("/search?q=a%26b%23c")]
    [InlineData("/%2Fevil.example/")]
    [InlineData("/%5Cevil.example/")]
    public async Task Export_RedirectTarget_IsWrittenAsSent(string location)
    {
        await using var app = await ExportAsync(["app/go/page.tsx"], map: a => a.MapPageLoader("/go", () => Page.LocalRedirect(location)));

        var html = await File.ReadAllTextAsync(Out("go/index.html"), TestContext.Current.CancellationToken);

        Assert.Equal(0, _exitCode);
        Assert.Equal(location, HrefOf(html));
        Assert.Contains($"""<meta http-equiv="refresh" content="0; url={location}">""", html);
        Assert.Equal("site.test", new Uri(new Uri("https://site.test/go/"), HrefOf(html)).Host);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hello")]
    [InlineData("vbscript:msgbox(1)")]
    public async Task Export_RedirectTargetThatIsNoPathOrHttpUrl_FailsTheExport(string location)
    {
        // Page.Redirect refuses these; the export checks again rather than trust every way a redirect is made.
        await using var app = await ExportAsync(
            ["app/go/page.tsx"],
            map: a => a.MapPageLoader("/go", () => new PageResult(PageResult.Kind.Redirect, location: location)));

        Assert.Equal(1, _exitCode);
        Assert.False(File.Exists(Out("go/index.html")));
        Assert.Contains(app.Logs, l => l.Message.Contains("/go") && l.Message.Contains("http(s)"));
    }

    [Theory]
    [InlineData("'javascript:alert(document.domain)'")]
    [InlineData(" \"javascript:alert(1)\"")]
    [InlineData("'//example.invalid/'")]
    public async Task Export_RedirectTargetStartingWithAQuote_FailsTheExport(string location)
    {
        // A meta refresh drops a quote after url=, so it would go where the link and the live server do not.
        await using var app = await ExportAsync(["app/go/page.tsx"], map: a => a.MapPageLoader("/go", () => Page.Redirect(location)));

        Assert.Equal(1, _exitCode);
        Assert.False(File.Exists(Out("go/index.html")));
        Assert.Contains(app.Logs, l => l.Message.Contains("/go") && l.Message.Contains("quote"));
    }

    /// <summary>The refresh page's link target, as a browser reads it (HTML-decoded).</summary>
    private static string HrefOf(string html) =>
        System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(html, "<a href=\"([^\"]*)\">").Groups[1].Value);

    [Fact]
    public async Task Export_AuthorizationFallbackPolicy_FailsInsteadOfExportingLoginRedirects()
    {
        await using var app = await ExportAsync(
            services: s => s.AddAuthorization(o => o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser().Build()));

        Assert.Equal(1, _exitCode);
        Assert.Contains(app.Logs, l => l.Message.Contains("fallback"));
        Assert.False(Directory.Exists(_out));
    }

    [Fact]
    public async Task Export_FallbackPolicy_SparesPagesThatAllowAnonymous()
    {
        await using var app = await ExportAsync(
            ["app/page.tsx", "app/secret/page.tsx"],
            map: a =>
            {
                a.MapPageLoader("/", () => Page.Props()).AllowAnonymous();
                a.MapPageLoader("/secret", () => Page.Props());
            },
            services: s => s.AddAuthorization(o => o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser().Build()));

        Assert.Equal(1, _exitCode);
        Assert.Contains(app.Logs, l => l.Message.Contains("/secret") && l.Message.Contains("fallback"));
        Assert.DoesNotContain(app.Logs, l => l.Message.Contains("Cannot export: / "));
    }

    [Fact]
    public async Task Export_RedirectNetjesDidNotMake_FailsTheExport()
    {
        await using var app = await ExportAsync(["app/page.tsx", "app/old/page.tsx"], map: a => a.MapPageLoader("/old", () => Results.Redirect("/login")));

        Assert.Equal(1, _exitCode);
        Assert.Contains(app.Logs, l => l.Message.Contains("/old") && l.Message.Contains("redirect"));
    }

    [Fact]
    public async Task Export_RedirectToTheExportServer_FailsTheExport()
    {
        await using var app = await ExportAsync(["app/page.tsx", "app/old/page.tsx"], map: a => a.MapPageLoader("/old", () => Page.Redirect("http://localhost/elsewhere")));

        Assert.Equal(1, _exitCode);
        Assert.Contains(app.Logs, l => l.Message.Contains("/old") && l.Message.Contains("export server"));
    }

    [Fact]
    public async Task Export_ExcludedPage_IsSkipped()
    {
        await using var app = await ExportAsync(
            ["app/page.tsx", "app/admin/page.tsx"],
            map: a => a.MapPageLoader("/admin", () => Page.Props()).ExcludeFromExport());

        Assert.Equal(0, _exitCode);
        Assert.True(File.Exists(Out("index.html")));
        Assert.False(File.Exists(Out("admin/index.html")));
    }

    [Fact]
    public async Task Export_PageActionExcludedFromExport_SkipsThePage()
    {
        await using var app = await ExportAsync(
            ["app/page.tsx", "app/contact/page.tsx"],
            map: a => a.MapPageAction("/contact", () => Page.Redirect("/")).ExcludeFromExport());

        Assert.Equal(0, _exitCode);
        Assert.True(File.Exists(Out("index.html")));
        Assert.False(File.Exists(Out("contact/index.html")));
    }

    [Theory]
    [InlineData("dynamic", "/blog/[slug]", ".StaticParams")]
    [InlineData("action", "/contact", "form posts")]
    [InlineData("auth", "/account", "authorization")]
    public async Task Export_PageThatNeedsAServer_FailsBeforeWritingAnything(string kind, string pattern, string reason)
    {
        await using var app = await ExportAsync(
            ["app/page.tsx", "app/blog/[slug]/page.tsx", "app/contact/page.tsx", "app/account/page.tsx"],
            map: a =>
            {
                if (kind != "dynamic") a.MapPageLoader("/blog/[slug]", (string slug) => Page.Props()).ExcludeFromExport();
                if (kind == "action") a.MapPageAction("/contact", () => Page.Redirect("/"));
                if (kind == "auth") a.MapPageLoader("/account", () => Page.Props()).RequireAuthorization();
            },
            services: s => s.AddAuthorization());

        Assert.Equal(1, _exitCode);
        Assert.Contains(app.Logs, l => l.Message.Contains(pattern) && l.Message.Contains(reason));
        Assert.False(Directory.Exists(_out));
    }

    [Fact]
    public async Task Export_PageThatFailsToRender_FailsTheExport()
    {
        await using var app = await ExportAsync(["app/page.tsx", "app/broken/page.tsx"], map: a => a.MapPageLoader("/broken", () => Page.NotFound()));

        Assert.Equal(1, _exitCode);
        Assert.Contains(app.Logs, l => l.Message.Contains("/broken") && l.Message.Contains("404"));
    }

    /// <summary>The category of the warnings about loaders that read the query string, which the export requests without one.</summary>
    private const string QueryString = "Netjes.Export.QueryString";

    private static List<LogEntry> QueryStringWarnings(TestApp app) => [.. app.Logs.Where(l => l.Category == QueryString)];

    [Fact]
    public async Task Export_PageWhoseLoaderReadsTheQueryString_IsWrittenAndWarned()
    {
        await using var app = await ExportAsync(
            ["app/page.tsx", "app/search/page.tsx"],
            map: a => a.MapPageLoader("/search", (string? q, int page = 1) => Page.Props(new { q, page })));

        Assert.Equal(0, _exitCode);
        Assert.True(File.Exists(Out("search/index.html")));
        var warning = Assert.Single(QueryStringWarnings(app));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.StartsWith("app/search/page.tsx reads 'q' and 'page' from the query string", warning.Message);
        Assert.Contains(" /search ", warning.Message);
        Assert.Contains(".StaticParams(...)", warning.Message);
        Assert.Contains(".ExcludeFromExport()", warning.Message);
    }

    public sealed record SearchQuery(string? Q, int? Page);

    [Fact]
    public async Task Export_QueryParametersBoundByAttributeArrayAsParametersOrUnderStaticParams_AreWarned()
    {
        await using var app = await ExportAsync(
            ["app/find/page.tsx", "app/search/page.tsx", "app/tags/page.tsx", "app/blog/[slug]/page.tsx"],
            map: a =>
            {
                a.MapPageLoader("/find", ([FromQuery(Name = "term")] string? search) => Page.Props(new { search }));
                a.MapPageLoader("/search", ([AsParameters] SearchQuery query) => Page.Props(new { query.Q, query.Page }));
                a.MapPageLoader("/tags", (string[] tags) => Page.Props(new { tags }));
                a.MapPageLoader("/blog/[slug]", (string slug, string? highlight) => Page.Props(new PostProps(slug)))
                    .StaticParams(_ => [new { slug = "hello" }, new { slug = "world" }]);
            });

        Assert.Equal(0, _exitCode);
        var warnings = QueryStringWarnings(app).Select(w => w.Message).ToList();
        Assert.Equal(4, warnings.Count);
        Assert.Contains(warnings, w => w.StartsWith("app/find/page.tsx reads 'term' from the query string", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.StartsWith("app/search/page.tsx reads 'Q' and 'Page' from the query string", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.StartsWith("app/tags/page.tsx reads 'tags' from the query string", StringComparison.Ordinal));
        // One warning for the loader, not one for each URL it is exported for, and the route value is no query.
        var post = Assert.Single(warnings, w => w.StartsWith("app/blog/[slug]/page.tsx ", StringComparison.Ordinal));
        Assert.StartsWith("app/blog/[slug]/page.tsx reads 'highlight' from the query string", post);
        Assert.Contains("each URL of /blog/[slug]", post);
        Assert.DoesNotContain("'slug'", post);
    }

    [Fact]
    public async Task Export_LayoutLoaderThatReadsTheQueryString_IsWarnedOnceNamingThePagesBelowIt()
    {
        await using var app = await ExportAsync(
            ["app/page.tsx", "app/docs/layout.tsx", "app/docs/page.tsx", "app/docs/intro/page.tsx", "app/docs/draft/page.tsx"],
            map: a =>
            {
                a.MapLayoutLoader("/docs", (string? version) => Page.Props(new { version }));
                a.MapPageLoader("/docs/draft", () => Page.Props()).ExcludeFromExport();
            });

        Assert.Equal(0, _exitCode);
        var warning = Assert.Single(QueryStringWarnings(app));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.StartsWith("app/docs/layout.tsx reads 'version' from the query string", warning.Message);
        Assert.Contains("the 2 pages below it (/docs, /docs/intro)", warning.Message);
    }

    [Theory]
    [InlineData(new string[0], "the page below it (/docs)")]
    [InlineData(new[] { "a", "b", "c", "d" }, "the 5 pages below it (/docs, /docs/a, /docs/b, and 2 more)")]
    public async Task Export_LayoutLoaderThatReadsTheQueryString_NamesOnePageOrTheFirstThree(string[] slugs, string pages)
    {
        await using var app = await ExportAsync(
            ["app/docs/layout.tsx", "app/docs/page.tsx", "app/docs/[slug]/page.tsx"],
            map: a =>
            {
                a.MapLayoutLoader("/docs", (string? version) => Page.Props(new { version }));
                var docs = a.MapPageLoader("/docs/[slug]", (string slug) => Page.Props(new PostProps(slug)));
                if (slugs.Length == 0) docs.ExcludeFromExport();
                else docs.StaticParams(_ => slugs.Select(slug => new { slug }));
            });

        Assert.Equal(0, _exitCode);
        Assert.Contains(pages, Assert.Single(QueryStringWarnings(app)).Message);
    }

    public sealed record RequiredQuery(string? Q, int Page);

    [Theory]
    [InlineData("int", "'page'")]
    [InlineData("string", "'q'")]
    [InlineData("FromQuery", "'p'")]
    [InlineData("StringValues", "'tags'")]
    [InlineData("AsParameters", "'Page'")]
    public async Task Export_PageWhoseLoaderRequiresAQueryStringValue_IsWarnedThatTheExportFails(string kind, string keys)
    {
        // Without a default value or a nullable type, minimal APIs answer 400 to a request without the value; only the
        // keys of such parameters are named ('page', not 'q').
        await using var app = await ExportAsync(
            ["app/page.tsx", "app/search/page.tsx"],
            map: a => _ = kind switch
            {
                "int" => a.MapPageLoader("/search", (string? q, int page) => Page.Props(new { q, page })),
                "string" => a.MapPageLoader("/search", (string q) => Page.Props(new { q })),
                "FromQuery" => a.MapPageLoader("/search", ([FromQuery(Name = "p")] int page) => Page.Props(new { page })),
                "StringValues" => a.MapPageLoader("/search", (StringValues tags) => Page.Props(new { tags = tags.Count })),
                _ => a.MapPageLoader("/search", ([AsParameters] RequiredQuery query) => Page.Props(new { query.Q, query.Page })),
            });

        Assert.Equal(1, _exitCode);
        Assert.False(File.Exists(Out("search/index.html")));
        var warning = Assert.Single(QueryStringWarnings(app));
        Assert.StartsWith(
            $"app/search/page.tsx requires {keys} from the query string, but netjes export requests /search without one, so the app answers 400 and the export fails.",
            warning.Message);
        Assert.Contains("give each such parameter a default value or a nullable type", warning.Message);
        Assert.Contains(app.Logs, l => l.Message.Contains("Cannot export /search: the app answered 400"));
    }

    [Fact]
    public async Task Export_DynamicPageWhoseLoaderRequiresAQueryStringValue_IsWarnedThatTheExportFails()
    {
        await using var app = await ExportAsync(
            ["app/page.tsx", "app/blog/[slug]/page.tsx"],
            map: a => a.MapPageLoader("/blog/[slug]", (string slug, int page) => Page.Props(new PostProps(slug)))
                .StaticParams(_ => [new { slug = "hello" }]));

        Assert.Equal(1, _exitCode);
        Assert.StartsWith(
            "app/blog/[slug]/page.tsx requires 'page' from the query string, but netjes export requests each URL of /blog/[slug] without one, so the app answers 400",
            Assert.Single(QueryStringWarnings(app)).Message);
    }

    [Fact]
    public async Task Export_LayoutLoaderThatRequiresAQueryStringValue_IsWarnedThatThePagesBelowItFail()
    {
        await using var app = await ExportAsync(
            ["app/page.tsx", "app/docs/layout.tsx", "app/docs/page.tsx", "app/docs/intro/page.tsx"],
            map: a => a.MapLayoutLoader("/docs", (string? version, int page) => Page.Props(new { version, page })));

        Assert.Equal(1, _exitCode);
        Assert.False(File.Exists(Out("docs/index.html")));
        var warning = Assert.Single(QueryStringWarnings(app));
        Assert.StartsWith(
            "app/docs/layout.tsx requires 'page' from the query string, but netjes export requests every page below it (/docs, /docs/intro) without one, so the layout loader cannot run and the export fails.",
            warning.Message);
        Assert.Contains("give each such parameter a default value or a nullable type", warning.Message);
    }

    [Fact]
    public async Task Export_LoaderThatReadsNoQueryString_IsNotWarned()
    {
        // Route values (in another case too, of a page or of a layout's own URL, or through [FromRoute], which minimal
        // APIs read before [FromQuery]), a header, services and the request: nothing that a static file leaves out.
        await using var app = await ExportAsync(
            [
                "app/page.tsx", "app/docs/layout.tsx", "app/docs/page.tsx", "app/blog/[slug]/page.tsx", "app/tags/[tag]/page.tsx",
                "app/team/[team]/layout.tsx", "app/team/[team]/page.tsx",
            ],
            map: a =>
            {
                a.MapPageLoader("/", (HttpContext http, CancellationToken cancellationToken) => Page.Props());
                a.MapLayoutLoader("/docs", (Slugs slugs, [FromHeader(Name = "Accept-Language")] string? language) => Page.Props(new { language }));
                a.MapPageLoader("/blog/[slug]", (string Slug, Slugs slugs) => Page.Props(new PostProps(Slug)))
                    .StaticParams<Slugs>(slugs => slugs.All.Select(s => new { slug = s }));
                a.MapPageLoader("/tags/[tag]", ([FromRoute(Name = "tag"), FromQuery(Name = "t")] string name) => Page.Props(new PostProps(name)))
                    .StaticParams(_ => [new { tag = "dotnet" }]);
                a.MapLayoutLoader("/team/[team]", (string Team) => Page.Props(new PostProps(Team)));
                a.MapPageLoader("/team/[team]", (string team) => Page.Props(new PostProps(team)))
                    .StaticParams(_ => [new { team = "red" }]);
            },
            services: s => s.AddSingleton(new Slugs(["one", "two"])));

        Assert.Equal(0, _exitCode);
        Assert.True(File.Exists(Out("blog/two/index.html")));
        Assert.True(File.Exists(Out("team/red/index.html")));
        Assert.Empty(QueryStringWarnings(app));
    }

    [Fact]
    public async Task Export_QueryStringKeyReadInTwoCases_IsNamedOnce()
    {
        // ASP.NET Core matches query string keys in any case, so both parameters read ?q=.
        await using var app = await ExportAsync(
            ["app/page.tsx", "app/search/page.tsx"],
            map: a => a.MapPageLoader("/search", (string? q, [FromQuery(Name = "Q")] string? other) => Page.Props(new { q, other })));

        Assert.Equal(0, _exitCode);
        Assert.StartsWith("app/search/page.tsx reads 'q' from the query string", Assert.Single(QueryStringWarnings(app)).Message);
    }

    [Fact]
    public async Task Export_DynamicPageWhoseStaticParamsListIsEmpty_IsNotWarned()
    {
        // It writes no file, so neither its loader nor the layout loader above it is warned about.
        await using var app = await ExportAsync(
            ["app/page.tsx", "app/blog/layout.tsx", "app/blog/[slug]/page.tsx"],
            map: a =>
            {
                a.MapLayoutLoader("/blog", (string? sort) => Page.Props(new { sort }));
                a.MapPageLoader("/blog/[slug]", (string slug, string? highlight) => Page.Props(new PostProps(slug)))
                    .StaticParams(_ => Array.Empty<object>());
            });

        Assert.Equal(0, _exitCode);
        Assert.True(File.Exists(Out("index.html")));
        Assert.False(Directory.Exists(Out("blog")));
        Assert.Empty(QueryStringWarnings(app));
    }

    [Fact]
    public async Task Export_ExcludedPageThatReadsTheQueryString_IsNotWarned()
    {
        // On a layout loader, .ExcludeFromExport() leaves out every page below it.
        await using var app = await ExportAsync(
            ["app/page.tsx", "app/search/page.tsx", "app/admin/layout.tsx", "app/admin/page.tsx", "app/admin/users/page.tsx"],
            map: a =>
            {
                a.MapPageLoader("/search", (string? q) => Page.Props(new { q })).ExcludeFromExport();
                a.MapLayoutLoader("/admin", (string? tab) => Page.Props(new { tab })).ExcludeFromExport();
                a.MapPageLoader("/admin/users", (string? sort) => Page.Props(new { sort }));
            });

        Assert.Equal(0, _exitCode);
        Assert.True(File.Exists(Out("index.html")));
        Assert.False(File.Exists(Out("search/index.html")));
        Assert.False(File.Exists(Out("admin/users/index.html")));
        Assert.Empty(QueryStringWarnings(app));
    }

    [Fact]
    public async Task Export_ThatCannotBeExported_SaysWhyWithoutQueryStringWarnings()
    {
        await using var app = await ExportAsync(
            ["app/search/page.tsx", "app/blog/[slug]/page.tsx"],
            map: a => a.MapPageLoader("/search", (string? q) => Page.Props(new { q })));

        Assert.Equal(1, _exitCode);
        Assert.Contains(app.Logs, l => l.Message.Contains("/blog/[slug] is dynamic"));
        Assert.Empty(QueryStringWarnings(app));
    }

    [Fact]
    public async Task Export_EndpointMarkedIncludeInExport_IsWrittenByteForByte()
    {
        await using var app = await ExportAsync(map: a =>
        {
            a.MapGet("llms.txt", () => Results.Bytes([0, 255, 10])).IncludeInExport();
            // No HTTP method counts as GET, and a group's prefix is part of the URL.
            a.Map("/any.txt", () => "any").IncludeInExport();
            a.MapGroup("/feeds").MapGet("news.xml", () => Results.Text("<rss/>", "application/rss+xml")).IncludeInExport();
            a.MapGet("/unmarked.txt", () => "not exported");
        });

        Assert.Equal(0, _exitCode);
        Assert.Equal([0, 255, 10], await File.ReadAllBytesAsync(Out("llms.txt"), TestContext.Current.CancellationToken));
        Assert.Equal("any", await File.ReadAllTextAsync(Out("any.txt"), TestContext.Current.CancellationToken));
        Assert.Equal("<rss/>", await File.ReadAllTextAsync(Out("feeds/news.xml"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Out("unmarked.txt")));
        Assert.True(File.Exists(Out("about/index.html")));
        Assert.Contains(app.Logs, l => l.Message.StartsWith("Exported 2 pages and 3 files to "));
    }

    [Fact]
    public async Task Export_EndpointMarkedIncludeInExportAndExcludeFromExport_IsNotWritten()
    {
        await using var app = await ExportAsync(map: a => a.MapGet("/llms.txt", () => "text").IncludeInExport().ExcludeFromExport());

        Assert.Equal(0, _exitCode);
        Assert.False(File.Exists(Out("llms.txt")));
    }

    [Fact]
    public async Task Export_PageLoaderMarkedIncludeInExport_IsWrittenAsAPage()
    {
        await using var app = await ExportAsync(map: a => a.MapPageLoader("/about", () => Page.Props()).IncludeInExport());

        Assert.Equal(0, _exitCode);
        Assert.True(File.Exists(Out("about/index.html")));
        Assert.Contains(app.Logs, l => l.Message.StartsWith("Exported 2 pages and 0 files to "));
    }

    [Fact]
    public async Task Export_MapPagesMarkedIncludeInExport_WritesThePagesAsPages()
    {
        await using var app = await ExportAsync(mapPages: pages => pages.IncludeInExport());

        Assert.Equal(0, _exitCode);
        Assert.True(File.Exists(Out("index.html")));
        Assert.True(File.Exists(Out("about/index.html")));
        Assert.Contains(app.Logs, l => l.Message.StartsWith("Exported 2 pages and 0 files to "));
    }

    [Theory]
    [InlineData("parameters", "/files/{name}.txt", "parameters")]
    [InlineData("catch-all", "/files/{**path}", "parameters")]
    [InlineData("extension", "/feed", "file extension")]
    [InlineData("root", "/", "file extension")]
    [InlineData("authorization", "/feed.xml", "requires authorization")]
    [InlineData("fallback", "/feed.xml", "fallback policy")]
    [InlineData("post", "/feed.xml", "GET")]
    [InlineData("segment", "/a/../feed.xml", "not a safe file or directory name")]
    [InlineData("segment", "/c:feed.xml", "not a safe file or directory name")]
    [InlineData("segment", "/a\\b.txt", "not a safe file or directory name")]
    [InlineData("device", "/con.txt", "Windows device")]
    [InlineData("device", "/Nul/feed.xml", "Windows device")]
    [InlineData("page", "/about/index.html", "same output file")]
    [InlineData("page", "/About/Index.html", "same output file")]
    [InlineData("not-found page", "/404.html", "same output file")]
    [InlineData("marker", "/.netjes-export", "same output file")]
    [InlineData("page below", "/notes.txt", "needs a directory")]
    [InlineData("file below", "/about.txt/feed.xml", "needs a directory")]
    public async Task Export_IncludeInExportEndpointThatCannotBeAFile_FailsBeforeWritingAnything(string kind, string pattern, string reason)
    {
        await using var app = await ExportAsync(
            // Without app/page.tsx at /, which the endpoint at / would tie with.
            kind switch { "page below" => ["app/page.tsx", "app/notes.txt/page.tsx"], "root" => ["app/about/page.tsx"], _ => null },
            map: a =>
            {
                var endpoint = kind == "post" ? a.MapPost(pattern, () => "text") : a.MapGet(pattern, () => "text");
                endpoint.IncludeInExport();
                if (kind == "authorization") endpoint.RequireAuthorization();
                if (kind == "file below") a.MapGet("/about.txt", () => "text").IncludeInExport();
            },
            services: s =>
            {
                if (kind == "fallback")
                {
                    s.AddAuthorization(o => o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
                }
                else
                {
                    s.AddAuthorization();
                }
            });

        Assert.Equal(1, _exitCode);
        Assert.Contains(app.Logs, l => l.Level == LogLevel.Error && l.Message.Contains(pattern.TrimStart('/')) && l.Message.Contains(reason));
        Assert.False(Directory.Exists(_out));
    }

    [Theory]
    [InlineData("dist", "public", "robots.txt", "/robots.txt")]
    [InlineData("dist", "public", "Robots.TXT", "/robots.txt")]
    [InlineData("build", "static", ".well-known/security.txt", "/.well-known/security.txt")]
    public async Task Export_EndpointAndAClientBuildFileForOnePath_FailBeforeWritingAnything(string dist, string publicDirectory, string file, string pattern)
    {
        _dist = dist;
        await using var app = await ExportAsync(
            map: a => a.MapGet(pattern, () => "theirs").IncludeInExport(),
            prepareRoot: root =>
            {
                var copied = Path.Combine(root, dist, "client", file.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(copied)!);
                File.WriteAllText(copied, "mine");
            },
            configure: o => o.PublicDirectory = publicDirectory);

        Assert.Equal(1, _exitCode);
        Assert.Contains(app.Logs, l => l.Level == LogLevel.Error
            && l.Message.Contains($"{dist}/client/{file} (copied from {publicDirectory}/ by the build, or written by a Vite plugin)")
            && l.Message.Contains($"GET {pattern} both make {pattern}")
            && l.Message.Contains(".ExcludeFromExport()"));
        Assert.False(Directory.Exists(_out));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(404)]
    [InlineData(302)]
    public async Task Export_IncludeInExportEndpointThatAnswersAnError_FailsTheExport(int status)
    {
        await using var app = await ExportAsync(map: a =>
        {
            a.MapGet("/broken.txt", () => status == 302 ? Results.Redirect("/elsewhere.txt") : Results.StatusCode(status)).IncludeInExport();
            a.MapGet("/fine.txt", () => "fine").IncludeInExport();
        });

        Assert.Equal(1, _exitCode);
        Assert.Contains(app.Logs, l => l.Level == LogLevel.Error && l.Message.Contains("/broken.txt") && l.Message.Contains($"{status} instead of 200"));
        Assert.False(File.Exists(Out("broken.txt")));
        Assert.DoesNotContain(app.Logs, l => l.Message.StartsWith("Exported "));
    }

    [Fact]
    public async Task Export_IncludeInExportEndpointThatReadsTheQueryString_IsWrittenAndWarned()
    {
        await using var app = await ExportAsync(map: a => a.MapGet("/feed.xml", (string? tag) => Results.Text($"<rss tag=\"{tag}\"/>", "application/rss+xml")).IncludeInExport());

        Assert.Equal(0, _exitCode);
        Assert.Equal("<rss tag=\"\"/>", await File.ReadAllTextAsync(Out("feed.xml"), TestContext.Current.CancellationToken));
        var warning = Assert.Single(QueryStringWarnings(app));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(
            "GET /feed.xml reads 'tag' from the query string, but netjes export writes /feed.xml once, without one, and a static host serves that file for every query string. " +
            "To keep it on a server, exclude it with .ExcludeFromExport().",
            warning.Message);
    }

    [Fact]
    public async Task Export_IncludeInExportEndpointThatRequiresAQueryStringValue_IsWarnedThatTheExportFails()
    {
        await using var app = await ExportAsync(map: a => a.MapGet("/feed.xml", (string tag, int? page) => Results.Text(tag)).IncludeInExport());

        Assert.Equal(1, _exitCode);
        Assert.False(File.Exists(Out("feed.xml")));
        var warning = Assert.Single(QueryStringWarnings(app));
        Assert.StartsWith("GET /feed.xml requires 'tag' from the query string, but netjes export requests /feed.xml without one, so the app answers 400 and the export fails.", warning.Message);
        Assert.Contains("default value or a nullable type", warning.Message);
        Assert.Contains(app.Logs, l => l.Level == LogLevel.Error && l.Message.Contains("/feed.xml") && l.Message.Contains("400 instead of 200"));
    }

    private static readonly System.Xml.Linq.XNamespace Sitemaps = "http://www.sitemaps.org/schemas/sitemap/0.9";

    private async Task<List<string>> SitemapLocsAsync() =>
        [.. System.Xml.Linq.XDocument.Parse(await File.ReadAllTextAsync(Out("sitemap.xml"), TestContext.Current.CancellationToken))
            .Root!.Elements(Sitemaps + "url").Select(u => (string)u.Element(Sitemaps + "loc")!)];

    [Fact]
    public async Task Export_AppMappingMapSitemap_WritesASitemapOfTheExportedPages()
    {
        await using var app = await ExportAsync(
            ["app/page.tsx", "app/about/page.tsx", "app/blog/[slug]/page.tsx", "app/guestbook/page.tsx", "app/secret/page.tsx"],
            map: a =>
            {
                a.MapPageLoader("/blog/[slug]", (string slug) => Page.Props(new PostProps(slug)))
                    .StaticParams(_ => [new { slug = "hello" }, new { slug = "a b" }]);
                a.MapPageLoader("/guestbook", () => Page.Props()).ExcludeFromExport();
                a.MapPageLoader("/secret", () => Page.Props()).ExcludeFromSitemap();
                a.MapSitemap(new Uri("https://example.com/"));
            });

        Assert.Equal(0, _exitCode);
        Assert.Equal(
            ["https://example.com/", "https://example.com/about", "https://example.com/blog/hello", "https://example.com/blog/a%20b"],
            await SitemapLocsAsync());
        Assert.True(File.Exists(Out("secret/index.html")));
        Assert.False(File.Exists(Out("guestbook/index.html")));
        Assert.Empty(QueryStringWarnings(app));
        Assert.DoesNotContain(app.Logs, l => l.Level >= LogLevel.Warning && l.Message.Contains("loopback"));
        Assert.Contains(app.Logs, l => l.Message.StartsWith("Exported 5 pages and 1 files to "));
    }

    [Fact]
    public async Task Export_AppMappingMapRobotsTxt_WritesRobotsTxtAsTheAppServesIt()
    {
        await using var app = await ExportAsync(map: a =>
        {
            a.MapSitemap(new Uri("https://example.com/"));
            a.MapRobotsTxt(new Uri("https://example.com/"));
        });

        Assert.Equal(0, _exitCode);
        Assert.Equal(
            "User-agent: *\nAllow: /\n\nSitemap: https://example.com/sitemap.xml\n",
            await File.ReadAllTextAsync(Out("robots.txt"), TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Out("sitemap.xml")));
    }

    [Theory]
    [InlineData("http://localhost:5080", "http://localhost:5080")]
    [InlineData("http://127.0.0.1:8080/", "http://127.0.0.1:8080")]
    [InlineData("http://[::1]/", "http://[::1]")]
    [InlineData("https://app.localhost/", "https://app.localhost")]
    [InlineData("http://LOCALHOST./", "http://localhost.")]
    public async Task Export_SitemapWithALoopbackSiteUrl_IsWrittenAndWarned(string siteUrl, string root)
    {
        await using var app = await ExportAsync(map: a =>
        {
            a.MapSitemap(new Uri(siteUrl));
            a.MapRobotsTxt(new Uri(siteUrl));
        });

        Assert.Equal(0, _exitCode);
        Assert.Contains($"{root}/about", await SitemapLocsAsync());
        var warnings = app.Logs.Where(l => l.Level == LogLevel.Warning && l.Message.Contains("loopback")).ToList();
        Assert.Equal(["/sitemap.xml", "/robots.txt"], warnings.Select(w => w.Message.Split(' ')[0]).Order().Reverse());
        Assert.All(warnings, w => Assert.Equal("Netjes.Export.StaticExportService", w.Category));
        Assert.Equal(
            $"/sitemap.xml is written with the site URL {root}, a loopback address, so its links point at the machine of whoever opens them. " +
            "Pass the static host's URL to MapSitemap and MapRobotsTxt in the Production environment that netjes export runs in, for example through configuration.",
            warnings.Single(w => w.Message.StartsWith("/sitemap.xml ")).Message);
        Assert.Empty(QueryStringWarnings(app));
    }

    [Fact]
    public async Task Export_SitemapExcludedFromExport_IsNotWritten()
    {
        await using var app = await ExportAsync(map: a =>
        {
            a.MapSitemap(new Uri("https://example.com/")).ExcludeFromExport();
            a.MapRobotsTxt(new Uri("https://example.com/")).ExcludeFromExport();
        });

        Assert.Equal(0, _exitCode);
        Assert.False(File.Exists(Out("sitemap.xml")));
        Assert.False(File.Exists(Out("robots.txt")));
        Assert.True(File.Exists(Out("about/index.html")));
    }

    [Fact]
    public async Task Export_MapSitemapInARouteGroupWithParameters_FailsBeforeWritingAnything()
    {
        await using var app = await ExportAsync(map: a => a.MapGroup("/{lang}").MapSitemap(new Uri("https://example.com/")));

        Assert.Equal(1, _exitCode);
        Assert.Contains(app.Logs, l => l.Level == LogLevel.Error && l.Message.Contains("GET, HEAD /{lang}/sitemap.xml") && l.Message.Contains("parameters"));
        Assert.False(Directory.Exists(_out));
    }

    [Fact]
    public async Task Export_MapRobotsTxtAndARobotsTxtInTheClientBuild_FailBeforeWritingAnything()
    {
        await using var app = await ExportAsync(
            map: a => a.MapRobotsTxt(new Uri("https://example.com/")),
            prepareRoot: root => File.WriteAllText(Path.Combine(root, "dist", "client", "robots.txt"), "User-agent: *\nDisallow: /\n"));

        Assert.Equal(1, _exitCode);
        Assert.Contains(app.Logs, l => l.Level == LogLevel.Error && l.Message.Contains(
            "dist/client/robots.txt (copied from public/ by the build, or written by a Vite plugin) and the endpoint GET, HEAD /robots.txt both make /robots.txt, and the server serves the file"));
        Assert.False(Directory.Exists(_out));
    }

    [Fact]
    public async Task Export_MarksItsOutput_AndReplacesAPreviousExport()
    {
        await using var app = await ExportAsync(prepareRoot: root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "out"));
            File.WriteAllText(Path.Combine(root, "out", ".netjes-export"), "");
            File.WriteAllText(Path.Combine(root, "out", "stale.html"), "old");
        });

        Assert.Equal(0, _exitCode);
        Assert.True(File.Exists(Out(".netjes-export")));
        Assert.False(File.Exists(Out("stale.html")));
    }

    [Fact]
    public async Task Export_ExistingDirectoryThatIsNoExport_IsLeftAlone()
    {
        await using var app = await ExportAsync(prepareRoot: root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "out"));
            File.WriteAllText(Path.Combine(root, "out", "keep.txt"), "mine");
        });

        Assert.Equal(1, _exitCode);
        Assert.Equal("mine", await File.ReadAllTextAsync(Out("keep.txt"), TestContext.Current.CancellationToken));
        Assert.Contains(app.Logs, l => l.Message.Contains("not a previous export"));
    }

    [Fact]
    public async Task Export_EmptyExistingDirectory_IsUsed()
    {
        await using var app = await ExportAsync(prepareRoot: root => Directory.CreateDirectory(Path.Combine(root, "out")));

        Assert.Equal(0, _exitCode);
        Assert.True(File.Exists(Out("index.html")));
    }

    [Theory]
    [InlineData("app")]
    [InlineData("app/sub")]
    [InlineData("public")]
    [InlineData("dist")]
    [InlineData("dist/client")]
    [InlineData("dist/server")]
    [InlineData("generated")]
    [InlineData(".")]
    [InlineData("app/..sub")]
    public async Task Export_OutputOverlappingTheAppsFiles_FailsWithoutTouchingThem(string output)
    {
        _output = output;
        await using var app = await ExportAsync(prepareRoot: root =>
        {
            foreach (var dir in new[] { "app", "public", "generated" })
            {
                Directory.CreateDirectory(Path.Combine(root, dir));
                File.WriteAllText(Path.Combine(root, dir, "keep.txt"), "mine");
            }
        });

        Assert.Equal(1, _exitCode);
        Assert.Contains(app.Logs, l => l.Message.Contains("output directory"));
        Assert.True(File.Exists(Path.Combine(_root, "app", "keep.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "public", "keep.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "dist", "client", "favicon.svg")));
        Assert.True(File.Exists(Path.Combine(_root, "generated", "keep.txt")));
    }

    [Fact]
    public async Task Export_OutputWhoseNameStartsWithTwoDots_IsInsideTheApp()
    {
        _output = "..out";
        await using var app = await ExportAsync();

        Assert.Equal(0, _exitCode);
        Assert.True(File.Exists(Out("index.html")));
    }

    [Fact]
    public async Task Export_OutputOutsideTheApp_Fails()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"netjes-outside-{Guid.NewGuid():N}");
        _output = outside;
        try
        {
            await using var app = await ExportAsync();

            Assert.Equal(1, _exitCode);
            Assert.Contains(app.Logs, l => l.Message.Contains("inside the app"));
            Assert.False(Directory.Exists(outside));
        }
        finally
        {
            if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true);
        }
    }
}
