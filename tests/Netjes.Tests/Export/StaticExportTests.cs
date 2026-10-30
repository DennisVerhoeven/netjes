using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

    public sealed record PostProps(string Slug);

    /// <summary>Runs the export command against a production build (a manifest listing <paramref name="files"/>).</summary>
    private Task<TestApp> ExportAsync(
        IEnumerable<string>? files = null,
        Action<WebApplication>? map = null,
        Action<IServiceCollection>? services = null,
        Action<string>? prepareRoot = null,
        Action<FakeSsrRenderer>? renderer = null)
    {
        var pages = (files ?? ["app/page.tsx", "app/about/page.tsx"]).ToList();
        return TestApp.StartAsync(
            [],
            map: map,
            renderer: renderer,
            environment: "Production",
            prepareRoot: root =>
            {
                _root = root;
                WriteBuild(root, pages);
                prepareRoot?.Invoke(root);
            },
            configure: o => o.Command = new NetjesCommandOptions { Name = "export", Output = _output },
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

    private static void WriteBuild(string root, IReadOnlyList<string> pages)
    {
        var client = Path.Combine(root, "dist", "client");
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
