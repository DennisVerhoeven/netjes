using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Rewrite;
using Microsoft.Extensions.DependencyInjection;

namespace Netjes.Tests.Endpoints;

/// <summary>
/// A page answers one spelling of its URL: another case of its literal segments, and with <see cref="TrailingSlash.Never"/>
/// a trailing slash, answers 308 with the file's spelling. These run on Kestrel, which passes on the request target as
/// the client sent it; TestServer passes on none. Requests go out as written, on a connection of their own, since
/// HttpClient would tidy the target first (<c>/b%61se</c> to <c>/base</c>, a backslash to a slash).
/// </summary>
public class OtherSpellingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Pages =
    [
        "app/page.tsx",
        "app/about/page.tsx",
        "app/blog/[slug]/page.tsx",
        "app/docs/[[...slug]]/page.tsx",
        "app/guides/[...path]/page.tsx",
        "app/contact/page.tsx",
    ];

    private static readonly Uri Site = new("https://example.com");

    /// <summary>Every page above, <c>/contact</c> through <c>MapPageLoader</c>, and the sitemap and robots.txt, on Kestrel.</summary>
    private static Task<TestApp> StartAsync(
        Action<NetjesOptions>? configure = null,
        string? pathBase = null,
        Action<WebApplication>? beforeNetjes = null,
        Action<WebApplication>? map = null,
        bool kestrel = true) =>
        TestApp.StartAsync(
            Pages,
            map: a =>
            {
                a.MapPageLoader("/contact", () => Page.Props(new { Email = "hello@example.com" }));
                a.MapSitemap(Site);
                a.MapRobotsTxt(Site);
                map?.Invoke(a);
            },
            configure: configure,
            pathBase: pathBase,
            beforeNetjes: beforeNetjes,
            kestrel: kestrel);

    private static void Never(NetjesOptions options) => options.TrailingSlash = TrailingSlash.Never;

    [Fact]
    public async Task AnotherCaseOfALiteralSegment_IsRedirectedToTheFilesSpelling()
    {
        await using var app = await StartAsync();

        // Route values keep their case, and under TrailingSlash.Ignore, the default, so does a trailing slash.
        Assert.Equal(
            [
                "GET /ABOUT: 308 /about",
                "GET /About: 308 /about",
                "GET /Blog/Hello-World: 308 /blog/Hello-World",
                "GET /DOCS/Guides/A: 308 /docs/Guides/A",
                "GET /Guides/Routing: 308 /guides/Routing",
                "GET /CONTACT: 308 /contact",
                "GET /ABOUT/: 308 /about/",
                "HEAD /ABOUT: 308 /about",
                "GET /about: 200",
                "GET /about/: 200",
                "GET /blog/Hello-World: 200",
            ],
            await AnswersAsync(app,
                "/ABOUT", "/About", "/Blog/Hello-World", "/DOCS/Guides/A", "/Guides/Routing", "/CONTACT", "/ABOUT/", "HEAD /ABOUT",
                "/about", "/about/", "/blog/Hello-World"));
    }

    [Fact]
    public async Task WithTrailingSlashNever_ATrailingSlashIsRedirectedAway()
    {
        await using var app = await StartAsync(Never);
        await using var underABase = await StartAsync(Never, pathBase: "/base");

        // A catch-all's value loses its last slash; the root keeps its own, also under a base path.
        Assert.Equal(
            [
                "GET /about/: 308 /about",
                "GET /ABOUT/: 308 /about",
                "GET /docs/a/b/: 308 /docs/a/b",
                "GET /docs/: 308 /docs",
                "GET /guides/a/: 308 /guides/a",
                "GET /contact/: 308 /contact",
                "HEAD /about/: 308 /about",
                "GET /: 200",
                "GET /about: 200",
                "GET /docs: 200",
            ],
            await AnswersAsync(app, "/about/", "/ABOUT/", "/docs/a/b/", "/docs/", "/guides/a/", "/contact/", "HEAD /about/", "/", "/about", "/docs"));
        Assert.Equal(
            ["GET /base/: 200", "GET /base: 200", "GET /base/about/: 308 /base/about"],
            await AnswersAsync(underABase, "/base/", "/base", "/base/about/"));
    }

    [Fact]
    public async Task TheQueryTheBasePathAndRouteValueEncodings_AreKept()
    {
        await using var app = await TestApp.StartAsync(
            [.. Pages, "app/sale%25/page.tsx"], configure: Never, pathBase: "/base", kestrel: true);
        // What X-Forwarded-Prefix does: the base path the client sees, which the request target leaves out.
        await using var forwarded = await StartAsync(Never, beforeNetjes: a => a.Use((HttpContext http, RequestDelegate next) =>
        {
            http.Request.PathBase = "/proxy";
            return next(http);
        }));

        Assert.Equal(
            [
                "GET /base/About/?q=1&q=2: 308 /base/about?q=1&q=2",
                "GET /base/Blog/a%2Fb: 308 /base/blog/a%2Fb",
                "GET /base/Blog/caf%C3%A9: 308 /base/blog/caf%C3%A9",
                "GET /base/Blog/a%20b: 308 /base/blog/a%20b",
                "GET /base/Blog/%2541: 308 /base/blog/%2541",
                "GET /BASE/About: 308 /BASE/about",
                // The folder sale%25: the location decodes to its name, not to sale%.
                "GET /base/SALE%2525: 308 /base/sale%2525",
                "GET /base/sale%2525: 200",
            ],
            await AnswersAsync(app,
                "/base/About/?q=1&q=2", "/base/Blog/a%2Fb", "/base/Blog/caf%C3%A9", "/base/Blog/a%20b", "/base/Blog/%2541", "/BASE/About",
                "/base/SALE%2525", "/base/sale%2525"));
        // The redirect leads to the value the client sent: %2541 is %41, not A.
        Assert.Equal("%41", (string?)(await app.GetDataJsonAsync("/base/blog/%2541"))["params"]!["slug"]);
        Assert.Equal(["GET /About?x=1: 308 /proxy/about?x=1"], await AnswersAsync(forwarded, "/About?x=1"));
    }

    [Fact]
    public async Task AClientNavigation_GetsARedirectPayload()
    {
        await using var app = await StartAsync(Never);

        foreach (var (url, location) in new[] { ("/ABOUT", "/about"), ("/about/", "/about"), ("/Blog/x?tag=a", "/blog/x?tag=a") })
        {
            var response = await app.GetDataAsync(url);
            var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("redirect", (string?)payload["kind"]);
            Assert.Equal(location, (string?)payload["location"]);
            Assert.Contains(NetjesHeaders.Data, response.Headers.Vary);
        }
    }

    [Fact]
    public async Task RobotsTxtAndSitemapXml_InAnotherSpelling_AreRedirected()
    {
        await using var app = await StartAsync();
        await using var never = await StartAsync(Never);

        // A plain 308 for a client navigation too: neither is a page.
        Assert.Equal(
            [
                "GET /ROBOTS.TXT: 308 /robots.txt",
                "HEAD /Robots.txt: 308 /robots.txt",
                "GET /Sitemap.xml/: 308 /sitemap.xml/",
                "GET /robots.txt: 200",
                "GET /sitemap.xml: 200",
            ],
            await AnswersAsync(app, "/ROBOTS.TXT", "HEAD /Robots.txt", "/Sitemap.xml/", "/robots.txt", "/sitemap.xml"));
        Assert.Equal(
            ["GET /Sitemap.xml/: 308 /sitemap.xml", "GET /robots.txt/: 308 /robots.txt"],
            await AnswersAsync(never, "/Sitemap.xml/", "/robots.txt/"));
        var data = await SendAsync(app, "GET", "/ROBOTS.TXT", $"{NetjesHeaders.Data}: 1");
        Assert.Equal((308, "/robots.txt"), (data.Status, data.Location));
    }

    [Fact]
    public async Task ARedirectNeverLeavesTheSite()
    {
        var routed = new ConcurrentQueue<string?>();
        await using var app = await TestApp.StartAsync(
            ["app/[[...slug]]/page.tsx"],
            configure: Never,
            beforeNetjes: a => a.Use((HttpContext http, RequestDelegate next) =>
            {
                // Which endpoint routing chose, whatever answered the request: the page or a redirect.
                http.Response.OnStarting(() =>
                {
                    routed.Enqueue(http.GetEndpoint()?.DisplayName);
                    return Task.CompletedTask;
                });
                // A base path from a header the app trusts from anyone, as X-Forwarded-Prefix is without known proxies.
                if (http.Request.Headers["X-Prefix"] is [{ Length: > 0 } prefix])
                {
                    http.Request.PathBase = prefix;
                }

                return next(http);
            }),
            kestrel: true);

        string[] targets =
        [
            "//evil.example/", "/%5Cevil.example/", "/%2F%2Fevil.example/", "/%5C%5Cevil.example/", "/\\evil.example/",
            "///evil.example/", "/\\/evil.example/", "/\t/evil.example/", "/%09/evil.example/", "//evil.example//",
        ];
        foreach (var target in targets)
        {
            var answer = await SendAsync(app, "GET", target);

            Assert.True(routed.TryDequeue(out var endpoint));
            Assert.Equal("Page /[[...slug]]", endpoint);
            AssertStaysOnTheSite(target, answer);
        }

        foreach (var prefix in new[] { "//evil.example", "/\\evil.example", "/%5Cevil.example" })
        {
            var answer = await SendAsync(app, "GET", "/About/", $"X-Prefix: {prefix}");

            Assert.True(routed.TryDequeue(out var endpoint));
            Assert.Equal("Page /[[...slug]]", endpoint);
            AssertStaysOnTheSite(prefix, answer);
        }

        // The setting is on: a URL that can go somewhere on the site does. A browser reads a backslash as a slash and a
        // '#' as the end of the path, so a location with either would be another page: those stay where they are.
        Assert.Equal(
            ["GET /a/b/: 308 /a/b", "GET /a\\b/: 200", "GET /a#b/: 200"],
            await AnswersAsync(app, "/a/b/", "/a\\b/", "/a#b/"));
    }

    private static void AssertStaysOnTheSite(string sent, Answer answer)
    {
        if (answer.Location is not { } location)
        {
            Assert.Equal(200, answer.Status);
            return;
        }

        // As sent, and as a client that decodes it would read it.
        foreach (var spelling in new[] { location, Uri.UnescapeDataString(location) })
        {
            Assert.True(Page.IsLocalUrl(spelling), $"{sent} went to {location}");
            Assert.False(spelling.StartsWith("//", StringComparison.Ordinal) || spelling.StartsWith("/\\", StringComparison.Ordinal), $"{sent} went to {location}");
        }
    }

    [Fact]
    public async Task RewrittenAndReExecutedRequests_AreNotRedirected()
    {
        // The rewrite from the redirects guide, which routing sees after it: /docs/v2/intro/ was not sent.
        await using var rewritten = await TestApp.StartAsync(
            ["app/docs/v2/[...path]/page.tsx"],
            configure: Never,
            beforeNetjes: a => a.UseRewriter(new RewriteOptions().AddRewrite("^docs/latest/(.*)", "docs/v2/$1", skipRemainingRules: true)),
            kestrel: true);
        // Each runs the pipeline again for a page path that would be redirected if it had been sent.
        await using var statusPages = await TestApp.StartAsync(
            ["app/status/[code]/page.tsx"],
            configure: Never,
            beforeNetjes: a => a.UseStatusCodePagesWithReExecute("/Status/{0}/"),
            map: a => a.MapGet("/api/gone", () => Results.StatusCode(StatusCodes.Status410Gone)),
            kestrel: true);
        await using var exceptions = await TestApp.StartAsync(
            ["app/oops/page.tsx"],
            configure: Never,
            beforeNetjes: a => a.UseExceptionHandler("/Oops/"),
            map: a => a.MapGet("/api/boom", string () => throw new InvalidOperationException("boom")),
            kestrel: true);

        foreach (var (app, url, page) in new[] { (statusPages, "/api/gone", "app/status/[code]/page.tsx"), (exceptions, "/api/boom", "app/oops/page.tsx") })
        {
            var answer = await SendAsync(app, "GET", url);

            Assert.Null(answer.Location);
            Assert.NotEqual(308, answer.Status);
            Assert.Equal(page, (string?)app.Renderer.LastPayload["page"]);
        }

        Assert.Equal(["GET /docs/latest/intro/: 200"], await AnswersAsync(rewritten, "/docs/latest/intro/"));
        // Sent as they are, the same paths are redirected.
        Assert.Equal(["GET /docs/v2/intro/: 308 /docs/v2/intro"], await AnswersAsync(rewritten, "/docs/v2/intro/"));
        Assert.Equal(["GET /Status/410/: 308 /status/410"], await AnswersAsync(statusPages, "/Status/410/"));
        Assert.Equal(["GET /Oops/: 308 /oops"], await AnswersAsync(exceptions, "/Oops/"));
    }

    public sealed record NoteInput
    {
        [Required(ErrorMessage = "Write something.")]
        public string? Text { get; init; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APostAndTheRenderOfAFailedFormPost_AreNotRedirected(bool javaScript)
    {
        var saved = new List<string?>();
        await using var app = await TestApp.StartAsync(
            ["app/notes/page.tsx"],
            configure: Never,
            map: a =>
            {
                a.MapPageLoader("/notes", () => Page.Props(new { Count = saved.Count }));
                a.MapPageAction("/notes", ([FromForm] NoteInput input) =>
                {
                    saved.Add(input.Text);
                    return Page.Redirect("/notes");
                });
            },
            kestrel: true);
        var token = await TokenAsync(app, "/notes");

        // Another case and a trailing slash, as a form whose action is written that way posts it.
        var failed = await app.Client.SendAsync(NotePost(javaScript, "/Notes/", token, ""), Ct);
        var done = await app.Client.SendAsync(NotePost(javaScript, "/Notes/", token, "Milk"), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);
        Assert.Null(failed.Headers.Location);
        var page = javaScript ? JsonNode.Parse(await failed.Content.ReadAsStringAsync(Ct))! : app.Renderer.LastPayload;
        Assert.Equal("page", (string?)page["kind"]);
        Assert.Equal("/Notes/", (string?)page["url"]);
        Assert.Equal(javaScript ? HttpStatusCode.OK : HttpStatusCode.SeeOther, done.StatusCode);
        Assert.Equal(["Milk"], saved);
    }

    private sealed record Token(string Cookie, string Field, string Value);

    private static async Task<Token> TokenAsync(TestApp app, string page)
    {
        var response = await app.GetDataAsync(page);
        var antiforgery = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["antiforgery"]!;
        return new Token(
            string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])),
            (string)antiforgery["formFieldName"]!,
            (string)antiforgery["requestToken"]!);
    }

    private static HttpRequestMessage NotePost(bool javaScript, string url, Token token, string text)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent([new(token.Field, token.Value), new("text", text)]),
        };
        request.Headers.Add("Cookie", token.Cookie);
        if (javaScript)
        {
            request.Headers.Add(NetjesHeaders.Data, "1");
        }

        return request;
    }

    [Fact]
    public async Task EndpointsThatAreNoPage_AndTheNotFoundPage_AnswerAsBefore()
    {
        await using var app = await StartAsync(Never, map: a =>
        {
            a.MapGet("/api/status", () => "ok");
            a.MapGet("/featured", () => Page.Render("/about"));
        });

        Assert.Equal(
            [
                "GET /API/Status/: 200",
                "GET /Featured/: 200",
                "GET /Nothing/Here/: 404",
                "GET /_NETJES/antiforgery/: 200",
            ],
            await AnswersAsync(app, "/API/Status/", "/Featured/", "/Nothing/Here/", "/_NETJES/antiforgery/"));
    }

    [Fact]
    public async Task ARouteWithASegmentTheUrlLeavesOut_IsNotRedirected()
    {
        // No page's route has an optional segment; an endpoint marked like robots.txt can, and its URL then has fewer
        // segments than the route. Only a URL that lines up with the route segment for segment is spelled again: /FEED/
        // does, with an empty value.
        await using var app = await StartAsync(Never, map: a =>
            a.MapGet("/feed/{page?}", (string? page) => page ?? "first").WithMetadata(new Netjes.Routing.OneSpellingMetadata()));

        Assert.Equal(
            ["GET /FEED: 200", "GET /FEED/: 308 /feed", "GET /FEED/2/: 308 /feed/2", "GET /feed: 200"],
            await AnswersAsync(app, "/FEED", "/FEED/", "/FEED/2/", "/feed"));
    }

    [Fact]
    public async Task TheOptionsOff_AnswerEverySpelling()
    {
        await using var app = await StartAsync(o => o.RedirectLiteralCase = false);

        Assert.Equal(
            ["GET /ABOUT: 200", "GET /About/: 200", "GET /Blog/x/: 200", "GET /DOCS/a/: 200", "GET /ROBOTS.TXT: 200", "GET /Sitemap.xml/: 200"],
            await AnswersAsync(app, "/ABOUT", "/About/", "/Blog/x/", "/DOCS/a/", "/ROBOTS.TXT", "/Sitemap.xml/"));
    }

    [Fact]
    public async Task OnTestServer_NothingIsRedirected()
    {
        // TestServer passes on no request target (IHttpRequestFeature.RawTarget is empty), so no URL counts as sent.
        await using var app = await StartAsync(Never, kestrel: false);

        foreach (var url in new[] { "/ABOUT", "/about/", "/ROBOTS.TXT" })
        {
            var response = await app.GetDocumentAsync(url);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal("page", (string?)(await app.GetDataJsonAsync("/About/"))["kind"]);
    }

    [Fact]
    public async Task OnTestServer_ARequestTargetSetAsKestrelSetsIt_IsRedirected()
    {
        // What the testing guide shows for WebApplicationFactory tests: the target from the request, ahead of the app.
        await using var app = await StartAsync(Never, kestrel: false, beforeNetjes: a => a.Use((HttpContext http, RequestDelegate next) =>
        {
            http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()!.RawTarget =
                Microsoft.AspNetCore.Http.Extensions.UriHelper.GetEncodedPathAndQuery(http.Request);
            return next(http);
        }));

        var response = await app.GetDocumentAsync("/About/?tab=1");

        Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);
        Assert.Equal("/about?tab=1", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task UnderCachePage_AnotherSpellingNeverReachesTheCache()
    {
        var renders = 0;
        await using var app = await TestApp.StartAsync(
            ["app/about/page.tsx"],
            map: a => a.MapPageLoader("/about", () => Page.Props(new { Render = Interlocked.Increment(ref renders) }))
                .CachePage(TimeSpan.FromMinutes(5)),
            services: s => s.AddOutputCache(),
            configureApp: a => a.UseOutputCache(),
            environment: "Testing",
            mode: NetjesMode.Development,
            kestrel: true);

        var before = await SendAsync(app, "GET", "/ABOUT");
        var first = await SendAsync(app, "GET", "/about");
        var stored = await SendAsync(app, "GET", "/about");
        var after = await SendAsync(app, "GET", "/ABOUT");
        var payload = await app.GetDataJsonAsync("/About");

        Assert.Equal((308, "/about"), (before.Status, before.Location));
        Assert.Equal((200, 200), (first.Status, stored.Status));
        Assert.True(stored.Headers.ContainsKey("Age"), "the second /about comes from the output cache");
        Assert.Equal((308, "/about", false), (after.Status, after.Location, after.Headers.ContainsKey("Age")));
        Assert.Equal("redirect", (string?)payload["kind"]);
        Assert.Equal(1, Volatile.Read(ref renders));
    }

    [Fact]
    public async Task ABasePathThatPrefixesThePagePath_IsNotAddedTwice()
    {
        await using var app = await TestApp.StartAsync(["app/docs/[slug]/page.tsx"], pathBase: "/docs", kestrel: true);

        // The base path goes out as the client sent it: %6F is o, so /d%6Fcs is the base path, and nothing is added to it.
        Assert.Equal(
            ["GET /docs/Docs/x: 308 /docs/docs/x", "GET /d%6Fcs/Docs/x: 308 /d%6Fcs/docs/x", "GET /docs/docs/x: 200"],
            await AnswersAsync(app, "/docs/Docs/x", "/d%6Fcs/Docs/x", "/docs/docs/x"));
        Assert.Equal("/docs/docs/x", (string?)(await app.GetDataJsonAsync("/docs/Docs/x"))["location"]);
    }

    /// <summary>What each request gets, as <c>GET /ABOUT: 308 /about</c>; a request is a target, or a method and a target.</summary>
    private static async Task<string[]> AnswersAsync(TestApp app, params string[] requests)
    {
        var answers = new List<string>();
        foreach (var request in requests)
        {
            var (method, target) = request.Split(' ', 2) is [var m, var t] ? (m, t) : ("GET", request);
            var answer = await SendAsync(app, method, target);
            answers.Add($"{method} {target}: {answer.Status}{(answer.Location is { } location ? $" {location}" : "")}");
        }

        return [.. answers];
    }

    private sealed record Answer(int Status, IReadOnlyDictionary<string, string> Headers)
    {
        public string? Location => Headers.GetValueOrDefault("Location");
    }

    /// <summary>Sends a request with the target exactly as written and reads the status and headers of its answer.</summary>
    private static async Task<Answer> SendAsync(TestApp app, string method, string target, params string[] headers)
    {
        var server = app.Client.BaseAddress!;
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(server.Host, server.Port, Ct);
        await using var stream = tcp.GetStream();
        var head = new StringBuilder($"{method} {target} HTTP/1.1\r\nHost: {server.Authority}\r\nConnection: close\r\n");
        foreach (var header in headers)
        {
            head.Append(header).Append("\r\n");
        }

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.Append("\r\n").ToString()), Ct);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        var text = await reader.ReadToEndAsync(Ct);
        var lines = text[..text.IndexOf("\r\n\r\n", StringComparison.Ordinal)].Split("\r\n");
        // A header sent on several lines, such as Vary, is one value.
        return new Answer(
            int.Parse(lines[0].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture),
            lines.Skip(1).Select(line => line.Split(':', 2)).GroupBy(h => h[0], StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(h => h[1].Trim())), StringComparer.OrdinalIgnoreCase));
    }
}
