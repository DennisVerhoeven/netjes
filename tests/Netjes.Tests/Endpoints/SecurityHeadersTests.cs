using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Netjes.Tests.Endpoints;

/// <summary>
/// nosniff on every response through UseNetjes, and a referrer policy and a same-origin framing policy on every document,
/// unless the app set its own.
/// </summary>
public class SecurityHeadersTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A returning visitor: the antiforgery cookie is already there, so antiforgery adds no X-Frame-Options of its own.</summary>
    private static async Task<HttpResponseMessage> GetAsReturningVisitorAsync(TestApp app, string url)
    {
        var first = await app.GetDocumentAsync(url);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", string.Join("; ", first.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])));
        var response = await app.Client.SendAsync(request, Ct);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        return response;
    }

    /// <summary>Every value of the header, comma-joined (a duplicate shows up as two), or null without one.</summary>
    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;

    [Fact]
    public async Task Documents_GetNosniffAReferrerPolicyAndSameOriginFraming_WithoutAnAntiforgeryCookieBeingMinted()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"]);

        var response = await GetAsReturningVisitorAsync(app, "/");

        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("strict-origin-when-cross-origin", Header(response, "Referrer-Policy"));
        Assert.Equal("SAMEORIGIN", Header(response, "X-Frame-Options"));
    }

    [Fact]
    public async Task TheNotFoundDocument_GetsThem()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"]);

        var response = await app.GetDocumentAsync("/missing");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("strict-origin-when-cross-origin", Header(response, "Referrer-Policy"));
        Assert.Equal("SAMEORIGIN", Header(response, "X-Frame-Options"));
    }

    [Fact]
    public async Task AFirstVisit_WhereAntiforgeryStoresACookie_GetsOneFramingHeader()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"]);

        var response = await app.GetDocumentAsync("/");

        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal));
        Assert.Equal("SAMEORIGIN", Header(response, "X-Frame-Options"));
    }

    [Fact]
    public async Task ThePlainErrorDocument_WhenTheErrorPageFailsToo_GetsThem()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"]);
        app.Renderer.OnRender = _ => throw new SsrRenderException("window is not defined", null);

        var response = await app.GetDocumentAsync("/");

        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("strict-origin-when-cross-origin", Header(response, "Referrer-Policy"));
        Assert.Equal("SAMEORIGIN", Header(response, "X-Frame-Options"));
    }

    [Fact]
    public async Task HeadersTheAppSet_AreKept()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configureApp: a => a.Use((http, next) =>
            {
                http.Response.Headers.XFrameOptions = "DENY";
                http.Response.Headers["Referrer-Policy"] = "no-referrer";
                http.Response.Headers.XContentTypeOptions = "nosniff";
                return next(http);
            }));

        var response = await app.GetDocumentAsync("/");

        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
    }

    [Fact]
    public async Task ThePolicyNetjesManages_AlsoGetsFrameAncestorsSelf()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"], configure: o => o.ContentSecurityPolicy = "script-src 'nonce-{nonce}'; object-src 'none'");

        var response = await app.GetDocumentAsync("/");

        Assert.Matches("^script-src 'nonce-[^']+'; object-src 'none'; frame-ancestors 'self'$", Header(response, "Content-Security-Policy"));
    }

    [Theory]
    [InlineData("script-src 'self'; frame-ancestors 'none'")]
    [InlineData("FRAME-ANCESTORS https://partner.example; script-src 'self';")]
    public async Task APolicyWithItsOwnFrameAncestors_IsLeftAsIs(string policy)
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"], configure: o => o.ContentSecurityPolicy = policy);

        var response = await app.GetDocumentAsync("/");

        Assert.Equal(policy, Header(response, "Content-Security-Policy"));
    }

    [Fact]
    public async Task AnAppWithItsOwnXFrameOptions_GetsNoFrameAncestorsInThePolicyNetjesManages()
    {
        // Browsers obey frame-ancestors over X-Frame-Options: a 'self' there would undo the app's DENY.
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configure: o => o.ContentSecurityPolicy = "script-src 'self'",
            configureApp: a => a.Use((http, next) =>
            {
                http.Response.Headers.XFrameOptions = "DENY";
                return next(http);
            }));

        var response = await app.GetDocumentAsync("/");

        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        Assert.Equal("script-src 'self'", Header(response, "Content-Security-Policy"));
    }

    [Theory]
    [InlineData("frame-ancestors 'none'", "DENY")]
    [InlineData("frame-ancestors 'self'", "SAMEORIGIN")]
    [InlineData("script-src 'self'; frame-ancestors 'self' https://partner.example", "SAMEORIGIN")]
    [InlineData("frame-ancestors https://partner.example", "DENY")]
    [InlineData("FRAME-ANCESTORS 'NONE'", "DENY")]
    [InlineData("frame-ancestors 'SELF'", "SAMEORIGIN")]
    [InlineData("script-src 'self'; frame-ancestors;", "DENY")]
    [InlineData("frame-ancestors *", "SAMEORIGIN")]
    [InlineData("frame-ancestors https:", "SAMEORIGIN")]
    // The app's own host as a host source: Netjes does not compare sources with the request's host.
    [InlineData("frame-ancestors https://localhost", "DENY")]
    [InlineData("script-src 'self';\tframe-ancestors\t'none'", "DENY")]
    // Without its quotes, self is a host named self.
    [InlineData("frame-ancestors self", "DENY")]
    // 'none' counts only on its own.
    [InlineData("frame-ancestors 'none' 'self'", "SAMEORIGIN")]
    // Browsers obey the first of two.
    [InlineData("frame-ancestors 'self'; frame-ancestors 'none'", "SAMEORIGIN")]
    public async Task FrameAncestors_SendTheXFrameOptionsTheyImply(string policy, string xFrameOptions)
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"], configure: o => o.ContentSecurityPolicy = policy);

        var response = await GetAsReturningVisitorAsync(app, "/");

        Assert.Equal(xFrameOptions, Header(response, "X-Frame-Options"));
        Assert.Equal(policy, Header(response, "Content-Security-Policy"));
    }

    [Theory]
    [InlineData("a first visit", 200)]
    [InlineData("a returning visitor", 200)]
    [InlineData("the not-found document", 404)]
    [InlineData("the plain error document", 500)]
    [InlineData("an output-cached document", 200)]
    public async Task FrameAncestorsNone_SendsDenyOnEveryKindOfDocument(string document, int status)
    {
        // Antiforgery adds X-Frame-Options: SAMEORIGIN when it stores a new cookie and finds none: on a first visit,
        // but never on a page the output cache may store, which carries no token. The policy decides on all of them.
        const string policy = "script-src 'self'; frame-ancestors 'none'";
        var loads = 0;
        await using var app = document == "an output-cached document"
            ? await TestApp.StartAsync(
                [],
                configure: o => o.ContentSecurityPolicy = policy,
                map: a => a.MapPageLoader("/", () => Page.Props(new { Load = Interlocked.Increment(ref loads) })).CachePage(TimeSpan.FromMinutes(5)),
                services: s => s.AddOutputCache(),
                configureApp: a => a.UseOutputCache(),
                environment: "Production",
                prepareRoot: ModeTests.WriteProductionBuild)
            : await TestApp.StartAsync(["app/page.tsx"], configure: o => o.ContentSecurityPolicy = policy);
        if (document == "the plain error document")
        {
            app.Renderer.OnRender = _ => throw new SsrRenderException("window is not defined", null);
        }

        var response = document switch
        {
            "a returning visitor" => await GetAsReturningVisitorAsync(app, "/"),
            "the not-found document" => await app.GetDocumentAsync("/missing"),
            "an output-cached document" => await GetFromTheOutputCacheAsync(app, "/"),
            _ => await app.GetDocumentAsync("/"),
        };

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(document == "an output-cached document" ? 1 : 0, loads);
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
    }

    /// <summary>The second of two requests, which the output cache answers.</summary>
    private static async Task<HttpResponseMessage> GetFromTheOutputCacheAsync(TestApp app, string url)
    {
        await app.GetDocumentAsync(url);
        return await app.GetDocumentAsync(url);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnAppsOwnFrameAncestors_GetTheXFrameOptionsTheyImply(bool inItsOwnHeader)
    {
        // How an app lets a partner frame its pages. X-Frame-Options cannot name another site, so a browser that knows
        // only that header keeps them to the app's own.
        const string policy = "frame-ancestors 'self' https://partner.example";
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configure: o => o.ContentSecurityPolicy = inItsOwnHeader ? null : policy,
            configureApp: a => a.Use((http, next) =>
            {
                if (inItsOwnHeader)
                {
                    http.Response.Headers.ContentSecurityPolicy = policy;
                }

                return next(http);
            }));

        var response = await GetAsReturningVisitorAsync(app, "/");

        Assert.Equal(policy, Header(response, "Content-Security-Policy"));
        Assert.Equal("SAMEORIGIN", Header(response, "X-Frame-Options"));
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
    }

    [Theory]
    [InlineData(true, "frame-ancestors 'self'", "frame-ancestors 'none'")]
    [InlineData(true, "frame-ancestors 'self', frame-ancestors 'none'", null)]
    [InlineData(true, "script-src 'self', frame-ancestors 'none'", null)]
    [InlineData(false, "frame-ancestors 'self', frame-ancestors 'none'", null)]
    [InlineData(false, "script-src 'self', frame-ancestors 'none'", null)]
    public async Task SeveralPolicies_TheStrictestFramingWins(bool inItsOwnHeader, string policy, string? another)
    {
        // Browsers enforce every policy, whether each comes in a header of its own or several are joined by commas.
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configure: o => o.ContentSecurityPolicy = inItsOwnHeader ? null : policy,
            configureApp: a => a.Use((http, next) =>
            {
                if (inItsOwnHeader)
                {
                    http.Response.Headers.ContentSecurityPolicy = another is null ? policy : new StringValues([policy, another]);
                }

                return next(http);
            }));

        var response = await GetAsReturningVisitorAsync(app, "/");

        Assert.Equal(another is null ? policy : $"{policy}, {another}", Header(response, "Content-Security-Policy"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
    }

    [Fact]
    public async Task AReportOnlyPolicy_DoesNotDecideFraming()
    {
        // A report-only policy blocks nothing, so it says nothing about who may frame the pages.
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configureApp: a => a.Use((http, next) =>
            {
                http.Response.Headers["Content-Security-Policy-Report-Only"] = "frame-ancestors 'none'";
                return next(http);
            }));

        var response = await GetAsReturningVisitorAsync(app, "/");

        Assert.Equal("SAMEORIGIN", Header(response, "X-Frame-Options"));
        Assert.Null(Header(response, "Content-Security-Policy"));
    }

    [Theory]
    [InlineData(false, "SAMEORIGIN")]
    [InlineData(true, "DENY")]
    public async Task ALoaderThatStoresAntiforgeryTokens_KeepsAntiforgerysHeader_UnlessSuppressed(bool suppressed, string xFrameOptions)
    {
        // Antiforgery adds its X-Frame-Options before Netjes renders, so Netjes takes it for the app's own.
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configure: o => o.ContentSecurityPolicy = "frame-ancestors 'none'",
            services: s => s.AddAntiforgery(o => o.SuppressXFrameOptionsHeader = suppressed),
            map: a => a.MapPageLoader("/", (HttpContext http, IAntiforgery antiforgery) =>
            {
                antiforgery.GetAndStoreTokens(http);
                return Page.Props(new { Stored = true });
            }));

        var response = await app.GetDocumentAsync("/");

        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal));
        Assert.Equal(xFrameOptions, Header(response, "X-Frame-Options"));
    }

    [Fact]
    public async Task FrameAncestorsInTheOption_DoNotCount_WhenTheAppSendsAPolicyOfItsOwn()
    {
        // Netjes writes the option's policy only when the app sent none, so here its frame-ancestors never go out.
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configure: o => o.ContentSecurityPolicy = "script-src 'self'; frame-ancestors 'none'",
            configureApp: a => a.Use((http, next) =>
            {
                http.Response.Headers.ContentSecurityPolicy = "script-src 'self'";
                return next(http);
            }));

        var response = await GetAsReturningVisitorAsync(app, "/");

        Assert.Equal("script-src 'self'", Header(response, "Content-Security-Policy"));
        Assert.Equal("SAMEORIGIN", Header(response, "X-Frame-Options"));
    }

    private static async Task<TestApp> StartWithDraftModeAsync(Action<NetjesOptions> configure, Action<WebApplication>? configureApp = null)
    {
        var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configure: configure,
            configureApp: configureApp,
            map: a => a.MapGet("/api/draft", (IDraftMode draft) =>
            {
                draft.Enable();
                return Results.Ok();
            }));
        return app;
    }

    /// <summary>A first visit in draft mode, as in a CMS's cross-site frame, where the antiforgery cookie is never sent.</summary>
    private static async Task<HttpResponseMessage> GetDraftAsync(TestApp app, string url)
    {
        var enabled = await app.Client.GetAsync("/api/draft", Ct);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", enabled.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        var response = await app.Client.SendAsync(request, Ct);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal));
        return response;
    }

    [Fact]
    public async Task DraftDocuments_MayBeFramedByTheConfiguredSites()
    {
        // Browsers obey frame-ancestors over X-Frame-Options, so the CMS may frame the draft; SAMEORIGIN stays for the
        // case where the policy is rewritten without it (below).
        await using var app = await StartWithDraftModeAsync(o => o.SecurityHeaders.DraftFrameAncestors = ["https://cms.example"]);

        var draft = await GetDraftAsync(app, "/");
        var published = await GetAsReturningVisitorAsync(app, "/");

        Assert.Equal("frame-ancestors 'self' https://cms.example", Header(draft, "Content-Security-Policy"));
        Assert.Equal("SAMEORIGIN", Header(draft, "X-Frame-Options"));
        Assert.Equal("nosniff", Header(draft, "X-Content-Type-Options"));
        Assert.Null(Header(published, "Content-Security-Policy"));
        Assert.Equal("SAMEORIGIN", Header(published, "X-Frame-Options"));
    }

    [Fact]
    public async Task DraftDocuments_WithThePolicyNetjesManages_GetTheSitesInIt()
    {
        await using var app = await StartWithDraftModeAsync(o =>
        {
            o.ContentSecurityPolicy = "script-src 'nonce-{nonce}'";
            o.SecurityHeaders.DraftFrameAncestors = ["https://cms.example", "https://*.cms.example"];
        });

        var draft = await GetDraftAsync(app, "/");

        Assert.Matches("^script-src 'nonce-[^']+'; frame-ancestors 'self' https://cms.example https://\\*.cms.example$", Header(draft, "Content-Security-Policy"));
        Assert.Equal("SAMEORIGIN", Header(draft, "X-Frame-Options"));
    }

    [Fact]
    public async Task DraftDocuments_StaySameOrigin_WhenAHeaderLibraryRewritesThePolicyWithoutFrameAncestors()
    {
        // A middleware ahead of Netjes that writes its own policy as the response starts, after Netjes has had its say.
        await using var app = await StartWithDraftModeAsync(
            o => o.SecurityHeaders.DraftFrameAncestors = ["https://cms.example"],
            a => a.Use((http, next) =>
            {
                http.Response.OnStarting(() =>
                {
                    http.Response.Headers.ContentSecurityPolicy = "script-src 'self'";
                    return Task.CompletedTask;
                });
                return next(http);
            }));

        var draft = await GetDraftAsync(app, "/");

        Assert.Equal("script-src 'self'", Header(draft, "Content-Security-Policy"));
        Assert.Equal("SAMEORIGIN", Header(draft, "X-Frame-Options"));
    }

    [Fact]
    public async Task ThePlainErrorDocument_InDraftModeWithConfiguredSites_StaysSameOrigin()
    {
        // The draft render that failed had promised the sites a frame; the plain page that replaces it makes no such promise.
        await using var app = await StartWithDraftModeAsync(o => o.SecurityHeaders.DraftFrameAncestors = ["https://cms.example"]);
        app.Renderer.OnRender = _ => throw new SsrRenderException("window is not defined", null);
        var enabled = await app.Client.GetAsync("/api/draft", Ct);
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Cookie", enabled.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("SAMEORIGIN", Header(response, "X-Frame-Options"));
        Assert.Null(Header(response, "Content-Security-Policy"));
    }

    [Fact]
    public async Task DraftDocuments_WithoutConfiguredSites_StaySameOrigin()
    {
        await using var app = await StartWithDraftModeAsync(_ => { });

        var draft = await GetDraftAsync(app, "/");

        Assert.Equal("SAMEORIGIN", Header(draft, "X-Frame-Options"));
        Assert.Null(Header(draft, "Content-Security-Policy"));
    }

    [Fact]
    public async Task DataResponses_GetNosniffOnly()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"], configure: o => o.ContentSecurityPolicy = "script-src 'self'");
        var first = await app.GetDataAsync("/");
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add(NetjesHeaders.Data, "1");
        request.Headers.Add("Cookie", string.Join("; ", first.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])));

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Null(Header(response, "Referrer-Policy"));
        Assert.Null(Header(response, "X-Frame-Options"));
    }

    [Theory]
    [InlineData("/robots.txt")]
    [InlineData("/sitemap.xml")]
    [InlineData("/_netjes/antiforgery")]
    public async Task NetjesEndpoints_GetNosniff(string path)
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"], map: a =>
        {
            a.MapRobotsTxt(new Uri("https://example.com"));
            a.MapSitemap(new Uri("https://example.com"));
        });

        var response = await app.GetDocumentAsync(path);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaticFiles_GetNosniff(bool production)
    {
        // public/ in Development, the client build elsewhere, whose hashed files keep their own caching.
        await using var app = production
            ? await TestApp.StartAsync([], environment: "Production", prepareRoot: ModeTests.WriteProductionBuild)
            : await TestApp.StartAsync(["app/page.tsx"], prepareRoot: WritePublicFile);

        var response = await app.GetDocumentAsync(production ? "/assets/index-CCC.js" : "/favicon.svg");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        if (production)
        {
            Assert.Equal("public, max-age=31536000, immutable", response.Headers.CacheControl?.ToString());
        }
    }

    private static void WritePublicFile(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "public"));
        File.WriteAllText(Path.Combine(root, "public", "favicon.svg"), "<svg/>");
    }

    [Fact]
    public async Task TheAppsOwnEndpoints_GetNosniff()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"], map: a => a.MapGet("/api/text", () => "x"));

        var response = await app.GetDocumentAsync("/api/text");

        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
    }

    [Fact]
    public async Task AnAnswerFromAnExceptionHandlerAheadOfNetjes_GetsNosniff()
    {
        // The handler clears the response, headers and all, before it writes its own: nosniff is added as it starts.
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            beforeNetjes: a => a.UseExceptionHandler(new ExceptionHandlerOptions
            {
                ExceptionHandler = http => http.Response.WriteAsync("failed"),
            }),
            map: a => a.MapGet("/api/fails", string () => throw new InvalidOperationException("broken")));

        var response = await app.GetDocumentAsync("/api/fails");

        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("failed", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
    }

    [Theory]
    [InlineData("an endpoint")]
    [InlineData("an endpoint's response-start callback")]
    [InlineData("a middleware, on a page")]
    public async Task TheAppsOwnXContentTypeOptions_IsKept(string setBy)
    {
        // Browsers read nosniff in any casing; this one shows that the app's value went out, and only it.
        const string own = "NOSNIFF";
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configureApp: a => a.Use((http, next) =>
            {
                if (setBy == "a middleware, on a page")
                {
                    http.Response.Headers.XContentTypeOptions = own;
                }

                return next(http);
            }),
            map: a => a.MapGet("/api/text", (HttpContext http) =>
            {
                if (setBy == "an endpoint")
                {
                    http.Response.Headers.XContentTypeOptions = own;
                }
                else
                {
                    http.Response.OnStarting(() =>
                    {
                        http.Response.Headers.XContentTypeOptions = own;
                        return Task.CompletedTask;
                    });
                }

                return "x";
            }));

        var response = await app.GetDocumentAsync(setBy == "a middleware, on a page" ? "/" : "/api/text");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(own, Header(response, "X-Content-Type-Options"));
    }

    [Fact]
    public async Task OutputCachedResponses_CarryNosniffOnce()
    {
        // The endpoint's answer is stored without it (the output cache takes the headers before the response starts),
        // the document's with it: either way, each answer from the cache has one.
        var calls = 0;
        var loads = 0;
        await using var app = await TestApp.StartAsync(
            [],
            map: a =>
            {
                a.MapGet("/api/count", () => $"{Interlocked.Increment(ref calls)}").CacheOutput();
                a.MapPageLoader("/", () => Page.Props(new { Load = Interlocked.Increment(ref loads) })).CachePage(TimeSpan.FromMinutes(5));
            },
            services: s => s.AddOutputCache(),
            configureApp: a => a.UseOutputCache(),
            environment: "Production",
            prepareRoot: ModeTests.WriteProductionBuild);

        var first = await app.GetDocumentAsync("/api/count");
        var cached = await app.GetDocumentAsync("/api/count");
        var firstDocument = await app.GetDocumentAsync("/");
        var cachedDocument = await app.GetDocumentAsync("/");

        Assert.Equal(1, calls);
        Assert.Equal(1, loads);
        Assert.Equal("1", await cached.Content.ReadAsStringAsync(Ct));
        Assert.Equal("nosniff", Header(first, "X-Content-Type-Options"));
        Assert.Equal("nosniff", Header(cached, "X-Content-Type-Options"));
        Assert.Equal("nosniff", Header(firstDocument, "X-Content-Type-Options"));
        Assert.Equal("nosniff", Header(cachedDocument, "X-Content-Type-Options"));
    }

    [Fact]
    public async Task TurnedOff_NetjesSetsNone()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"], configure: o =>
        {
            o.SecurityHeaders.Enabled = false;
            o.ContentSecurityPolicy = "script-src 'self'";
        });

        var response = await GetAsReturningVisitorAsync(app, "/");

        Assert.Null(Header(response, "X-Content-Type-Options"));
        Assert.Null(Header(response, "Referrer-Policy"));
        Assert.Null(Header(response, "X-Frame-Options"));
        Assert.Equal("script-src 'self'", Header(response, "Content-Security-Policy"));
    }

    [Fact]
    public async Task TurnedOff_NoResponseGetsNosniff_ButImagesKeepTheirs()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configure: o => o.SecurityHeaders.Enabled = false,
            map: a => a.MapGet("/api/text", () => "x"),
            prepareRoot: root =>
            {
                WritePublicFile(root);
                File.WriteAllBytes(Path.Combine(root, "public", "hero.png"), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4]);
            });

        var document = await GetAsReturningVisitorAsync(app, "/");
        var data = await app.GetDataAsync("/");
        var file = await app.GetDocumentAsync("/favicon.svg");
        var text = await app.GetDocumentAsync("/api/text");
        var image = await app.GetDocumentAsync("/_netjes/image?url=%2Fhero.png&w=640");

        Assert.Null(Header(document, "X-Content-Type-Options"));
        Assert.Null(Header(data, "X-Content-Type-Options"));
        Assert.Null(Header(file, "X-Content-Type-Options"));
        Assert.Null(Header(text, "X-Content-Type-Options"));
        Assert.Equal(System.Net.HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("nosniff", Header(image, "X-Content-Type-Options"));
        Assert.Contains("sandbox", Header(image, "Content-Security-Policy"));
    }

    [Fact]
    public async Task OutputCachedDocuments_KeepThem()
    {
        var loads = 0;
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props(new { Load = Interlocked.Increment(ref loads) })).CachePage(TimeSpan.FromMinutes(5)),
            services: s => s.AddOutputCache(),
            configureApp: a => a.UseOutputCache(),
            environment: "Testing",
            mode: NetjesMode.Development);

        await app.GetDocumentAsync("/");
        var cached = await app.GetDocumentAsync("/");

        Assert.Equal(1, loads);
        Assert.Equal("nosniff", Header(cached, "X-Content-Type-Options"));
        Assert.Equal("strict-origin-when-cross-origin", Header(cached, "Referrer-Policy"));
        Assert.Equal("SAMEORIGIN", Header(cached, "X-Frame-Options"));
    }
}
