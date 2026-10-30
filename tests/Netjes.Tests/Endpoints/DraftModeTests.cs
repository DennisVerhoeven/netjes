using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Netjes.Tests.Endpoints;

/// <summary>Draft mode: preview unpublished content, like Next.js's draftMode(), behind a signed cookie.</summary>
public class DraftModeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void MapDraftEndpoints(WebApplication app)
    {
        app.MapGet("/api/draft/on", (IDraftMode draft) =>
        {
            draft.Enable();
            return Results.Ok();
        });
        app.MapGet("/api/draft/off", (IDraftMode draft) =>
        {
            draft.Disable();
            return Results.Ok();
        });
    }

    private static async Task<string> EnableAsync(TestApp app)
    {
        var response = await app.Client.GetAsync("/api/draft/on", Ct);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__netjes_draft=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        return cookie.Split(';')[0];
    }

    private static async Task<JsonNode> GetDataAsync(TestApp app, string url, string? cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(NetjesHeaders.Data, "1");
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        var response = await app.Client.SendAsync(request, Ct);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
    }

    private static Task<TestApp> StartAsync(
        Action<WebApplication>? map = null, Action<IServiceCollection>? services = null, Action<WebApplication>? configureApp = null) =>
        TestApp.StartAsync(
            ["app/post/page.tsx"],
            map: a =>
            {
                MapDraftEndpoints(a);
                if (map is not null)
                {
                    map(a);
                }
                else
                {
                    a.MapPageLoader("/post", (IDraftMode draft) => Page.Props(new { Draft = draft.IsEnabled }));
                }
            },
            services: services,
            configureApp: configureApp);

    [Fact]
    public async Task ByDefault_LoadersSeePublishedContent()
    {
        await using var app = await StartAsync();

        var payload = await GetDataAsync(app, "/post", cookie: null);

        Assert.False((bool?)payload["props"]!["draft"]);
        Assert.Null(payload["draftMode"]);
    }

    [Fact]
    public async Task Enabled_LoadersAndTheClientSeeIt_AndBrowsersDoNotStoreThePage()
    {
        await using var app = await StartAsync();
        var cookie = await EnableAsync(app);

        var request = new HttpRequestMessage(HttpMethod.Get, "/post");
        request.Headers.Add("Cookie", cookie);
        var document = await app.Client.SendAsync(request, Ct);
        var payload = await GetDataAsync(app, "/post", cookie);

        Assert.True((bool?)payload["props"]!["draft"]);
        Assert.True((bool?)payload["draftMode"]);
        Assert.True(document.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task ATamperedCookie_IsIgnored()
    {
        await using var app = await StartAsync();

        var payload = await GetDataAsync(app, "/post", "__netjes_draft=forged");

        Assert.False((bool?)payload["props"]!["draft"]);
    }

    [Fact]
    public async Task OutsideDevelopment_TheCookieReachesCrossSitePreviewFrames()
    {
        // A CMS shows the preview in an iframe on its own site: only SameSite=None (which requires Secure) is sent there.
        await using var app = await TestApp.StartAsync(
            [],
            environment: "Production",
            prepareRoot: root =>
            {
                Directory.CreateDirectory(Path.Combine(root, "dist", "client", ".vite"));
                File.WriteAllText(
                    Path.Combine(root, "dist", "client", ".vite", "manifest.json"),
                    """
                    {
                      "node_modules/netjes/dist/client-entry.js": { "file": "assets/entry.js", "src": "node_modules/netjes/dist/client-entry.js", "isEntry": true },
                      "app/post/page.tsx": { "file": "assets/post.js", "src": "app/post/page.tsx", "isDynamicEntry": true }
                    }
                    """);
            },
            map: MapDraftEndpoints);

        var response = await app.Client.GetAsync("/api/draft/on", Ct);

        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__netjes_draft=", StringComparison.Ordinal));
        Assert.Contains("samesite=none", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Disable_ClearsTheCookie()
    {
        await using var app = await StartAsync();
        await EnableAsync(app);

        var response = await app.Client.GetAsync("/api/draft/off", Ct);

        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__netjes_draft=", StringComparison.Ordinal));
        Assert.Contains("expires=Thu, 01 Jan 1970", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CachedPages_AreBypassedInDraftMode_AndDraftsAreNeverCached()
    {
        var loads = 0;
        await using var app = await StartAsync(
            map: a => a
                .MapPageLoader("/post", (IDraftMode draft) => Page.Props(new { Draft = draft.IsEnabled, Load = Interlocked.Increment(ref loads) }))
                .CachePage(TimeSpan.FromMinutes(5)),
            services: s => s.AddOutputCache(),
            configureApp: a => a.UseOutputCache());
        var cookie = await EnableAsync(app);

        var published = await GetDataAsync(app, "/post", cookie: null);
        var draft = await GetDataAsync(app, "/post", cookie);
        var publishedAgain = await GetDataAsync(app, "/post", cookie: null);

        Assert.False((bool?)published["props"]!["draft"]);
        Assert.True((bool?)draft["props"]!["draft"]);
        Assert.False((bool?)publishedAgain["props"]!["draft"]);
        Assert.Equal(1, (int?)publishedAgain["props"]!["load"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OutputCachingOtherThanCachePage_NeverMixesDraftAndPublishedPages(bool draftFirst)
    {
        var loads = 0;
        await using var app = await StartAsync(
            map: a => a
                .MapPageLoader("/post", (IDraftMode draft) => Page.Props(new { Draft = draft.IsEnabled, Load = Interlocked.Increment(ref loads) }))
                .CacheOutput(policy => policy.Expire(TimeSpan.FromMinutes(5)))
                // As for a returning visitor who already has the antiforgery cookie: no Set-Cookie, so cacheable.
                .WithMetadata(new Netjes.Routing.OmitAntiforgeryTokenMetadata()),
            services: s => s.AddOutputCache(),
            configureApp: a => a.UseOutputCache());
        var cookie = await EnableAsync(app);

        JsonNode published, draft;
        if (draftFirst)
        {
            draft = await GetDataAsync(app, "/post", cookie);
            published = await GetDataAsync(app, "/post", cookie: null);
        }
        else
        {
            published = await GetDataAsync(app, "/post", cookie: null);
            draft = await GetDataAsync(app, "/post", cookie);
        }

        var draftAgain = await GetDataAsync(app, "/post", cookie);

        Assert.False((bool?)published["props"]!["draft"]);
        Assert.True((bool?)draft["props"]!["draft"]);
        // Not even other editors get a stored draft.
        Assert.NotEqual((int?)draft["props"]!["load"], (int?)draftAgain["props"]!["load"]);
    }

    public static TheoryData<string, string, bool> CdnHeaderCases => CdnCacheHeaders.SourcesCasingsAndCaching();

    /// <summary>
    /// A CDN that reads its own header ahead of <c>Cache-Control</c> would keep the draft and serve unpublished
    /// content to everyone, so a draft goes out without them, wherever the page or a middleware set them.
    /// </summary>
    [Theory]
    [MemberData(nameof(CdnHeaderCases))]
    public async Task Enabled_ThePageGoesOutWithoutTheHeadersACdnReadsAheadOfCacheControl(string source, string casing, bool outputCache)
    {
        await using var app = await TestApp.StartAsync(
            ["app/post/page.tsx"],
            map: a =>
            {
                MapDraftEndpoints(a);
                var page = a.MapPageLoader("/post", (HttpContext http, IDraftMode draft) =>
                {
                    CdnCacheHeaders.FromLoader(http, source, casing);
                    return Page.Props(new { Draft = draft.IsEnabled });
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
        var cookie = await EnableAsync(app);

        var published = await app.Client.GetAsync("/post", Ct);
        Assert.Equal(CdnCacheHeaders.Names.Length, CdnCacheHeaders.In(published).Count);
        foreach (var data in new[] { false, true })
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/post");
            request.Headers.Add("Cookie", cookie);
            if (data)
            {
                request.Headers.Add(NetjesHeaders.Data, "1");
            }

            var draft = await app.Client.SendAsync(request, Ct);

            Assert.Equal(HttpStatusCode.OK, draft.StatusCode);
            Assert.Equal("no-store, private", draft.Headers.CacheControl?.ToString());
            Assert.Empty(CdnCacheHeaders.In(draft));
        }
    }

    public static TheoryData<string, string, string, bool> RedirectCases()
    {
        var data = new TheoryData<string, string, string, bool>();
        foreach (var loader in new[] { "page", "layout" })
        {
            foreach (var source in CdnCacheHeaders.Sources)
            {
                foreach (var casing in CdnCacheHeaders.Casings)
                {
                    data.Add(loader, source, casing, false);
                    data.Add(loader, source, casing, true);
                }
            }
        }

        return data;
    }

    /// <summary>
    /// Where a draft redirects (to an unpublished slug, say) is draft content too: a CDN or browser that kept the
    /// redirect would send everyone there. It goes out like a draft page, from a page loader or a layout loader. Outside
    /// draft mode, the redirect keeps what the loader set.
    /// </summary>
    [Theory]
    [MemberData(nameof(RedirectCases))]
    public async Task Enabled_ALoaderRedirects_TheRedirectGoesOutNoStoreWithoutTheHeadersACdnReadsFirst(
        string loader, string source, string casing, bool outputCache)
    {
        PageResult Redirect(HttpContext http)
        {
            CdnCacheHeaders.FromLoader(http, source, casing);
            return Page.Redirect("/unpublished-slug");
        }

        await using var app = await TestApp.StartAsync(
            ["app/post/layout.tsx", "app/post/page.tsx"],
            map: a =>
            {
                MapDraftEndpoints(a);
                if (loader == "layout")
                {
                    a.MapLayoutLoader("/post", Redirect);
                }

                var page = a.MapPageLoader("/post", (HttpContext http) => loader == "page" ? Redirect(http) : Page.Props(new { }));
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
        var cookie = await EnableAsync(app);

        foreach (var data in new[] { false, true })
        {
            foreach (var draft in new[] { false, true })
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "/post");
                if (draft)
                {
                    request.Headers.Add("Cookie", cookie);
                }

                if (data)
                {
                    request.Headers.Add(NetjesHeaders.Data, "1");
                }

                var response = await app.Client.SendAsync(request, Ct);

                if (data)
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
                    Assert.Equal("redirect", (string?)payload["kind"]);
                    Assert.Equal("/unpublished-slug", (string?)payload["location"]);
                }
                else
                {
                    Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
                    Assert.Equal("/unpublished-slug", response.Headers.Location?.OriginalString);
                }

                if (draft)
                {
                    Assert.Equal("no-store, private", response.Headers.CacheControl?.ToString());
                    Assert.Empty(CdnCacheHeaders.In(response));
                }
                else
                {
                    Assert.Equal("public, max-age=600", response.Headers.CacheControl?.ToString());
                    Assert.Equal(CdnCacheHeaders.Names.Length, CdnCacheHeaders.In(response).Count);
                }
            }
        }
    }

    [Fact]
    public async Task AnExpiredCookie_IsIgnored()
    {
        var time = new MovableTime(DateTimeOffset.UtcNow);
        await using var app = await StartAsync(services: s => s.AddSingleton<TimeProvider>(time));
        var cookie = await EnableAsync(app);

        time.Advance(TimeSpan.FromDays(2));
        var payload = await GetDataAsync(app, "/post", cookie);

        Assert.False((bool?)payload["props"]!["draft"]);
    }

    private sealed class MovableTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
