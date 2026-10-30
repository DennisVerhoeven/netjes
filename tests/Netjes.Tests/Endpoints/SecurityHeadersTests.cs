using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Netjes.Tests.Endpoints;

/// <summary>nosniff, a referrer policy and a same-origin framing policy on every document, unless the app set its own.</summary>
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
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnAppWithItsOwnFrameAncestors_GetsNoXFrameOptionsFromNetjes(bool inItsOwnHeader)
    {
        // How an app lets a partner frame its pages: X-Frame-Options cannot name another site.
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
        Assert.Null(Header(response, "X-Frame-Options"));
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
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
    public async Task DataResponses_GetNone()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"], configure: o => o.ContentSecurityPolicy = "script-src 'self'");
        var first = await app.GetDataAsync("/");
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add(NetjesHeaders.Data, "1");
        request.Headers.Add("Cookie", string.Join("; ", first.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])));

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(Header(response, "X-Content-Type-Options"));
        Assert.Null(Header(response, "Referrer-Policy"));
        Assert.Null(Header(response, "X-Frame-Options"));
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
    public async Task OutputCachedDocuments_KeepThem()
    {
        var loads = 0;
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props(new { Load = Interlocked.Increment(ref loads) })).CachePage(TimeSpan.FromMinutes(5)),
            services: s => s.AddOutputCache(),
            configureApp: a => a.UseOutputCache());

        await app.GetDocumentAsync("/");
        var cached = await app.GetDocumentAsync("/");

        Assert.Equal(1, loads);
        Assert.Equal("nosniff", Header(cached, "X-Content-Type-Options"));
        Assert.Equal("strict-origin-when-cross-origin", Header(cached, "Referrer-Policy"));
        Assert.Equal("SAMEORIGIN", Header(cached, "X-Frame-Options"));
    }
}
