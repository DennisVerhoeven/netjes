using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Netjes.Tests.Endpoints;

public class PageCacheTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RevalidateTagAndPath_EvictCachedPagesFromAnyEndpoint()
    {
        var loads = new Dictionary<string, int>();
        await using var app = await TestApp.StartAsync(
            ["app/posts/[id]/page.tsx"],
            map: a =>
            {
                a.MapPageLoader("/posts/[id]", (string id) =>
                {
                    loads[id] = loads.GetValueOrDefault(id) + 1;
                    return Page.Props(new { Id = id });
                }).CachePage(TimeSpan.FromMinutes(5), "posts");
                // e.g. a CMS webhook
                a.MapPost("/hooks/post/{id}", async (string id, IPageCache cache) =>
                {
                    await cache.RevalidatePathAsync($"/posts/{id}");
                    return Results.NoContent();
                });
                a.MapPost("/hooks/all", async (IPageCache cache) =>
                {
                    await cache.RevalidateTagAsync("posts");
                    return Results.NoContent();
                });
            },
            services: s => s.AddOutputCache(),
            configureApp: a => a.UseOutputCache(),
            environment: "Testing",
            mode: NetjesMode.Development);
        await app.GetDataAsync("/posts/1");
        await app.GetDataAsync("/posts/2");

        await app.Client.PostAsync("/hooks/post/1", null, Ct);
        await app.GetDataAsync("/posts/1");
        await app.GetDataAsync("/posts/2");
        Assert.Equal((2, 1), (loads["1"], loads["2"]));

        await app.Client.PostAsync("/hooks/all", null, Ct);
        await app.GetDataAsync("/posts/1");
        await app.GetDataAsync("/posts/2");
        Assert.Equal((3, 2), (loads["1"], loads["2"]));
    }

    /// <summary>The output cache folds path case by default; loaders get route values as sent, and may compare them exactly.</summary>
    [Fact]
    public async Task PathsThatDifferOnlyInCase_GetTheirOwnEntries()
    {
        var codes = new Dictionary<string, string>(StringComparer.Ordinal) { ["Ab"] = "beta", ["aB"] = "alpha" };
        await using var app = await TestApp.StartAsync(
            ["app/s/[code]/page.tsx"],
            map: a => a.MapPageLoader("/s/[code]", (string code) => Page.Props(new { Value = codes.GetValueOrDefault(code) }))
                .CachePage(TimeSpan.FromMinutes(5)),
            services: s => s.AddOutputCache(),
            configureApp: a => a.UseOutputCache(),
            environment: "Testing",
            mode: NetjesMode.Development);

        var first = await app.GetDataJsonAsync("/s/Ab");
        var second = await app.GetDataJsonAsync("/s/aB");
        var firstAgain = await app.GetDataJsonAsync("/s/Ab");

        Assert.Equal("beta", (string?)first["props"]!["value"]);
        Assert.Equal("alpha", (string?)second["props"]!["value"]);
        Assert.Equal("beta", (string?)firstAgain["props"]!["value"]);
    }

    private static async Task<System.Text.Json.Nodes.JsonNode> DataWithCookieAsync(TestApp app, string url, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(NetjesHeaders.Data, "1");
        request.Headers.Add("Cookie", cookie);
        var response = await app.Client.SendAsync(request, Ct);
        return System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
    }

    /// <summary>
    /// A cached page whose shared props read a cookie. In the Testing environment, with Netjes in development mode
    /// (routes from <c>app/</c> on disk), unless <paramref name="development"/> asks for the Development environment.
    /// </summary>
    private static Task<TestApp> StartWithCookieSharedPropsAsync(bool development = false, string[]? queryKeys = null) =>
        TestApp.StartAsync(
            ["app/posts/[id]/page.tsx"],
            configure: o => o.SharedProps = http => ValueTask.FromResult<object?>(new { Theme = http.Request.Cookies["theme"] }),
            map: a =>
            {
                var page = a.MapPageLoader("/posts/[id]", (string id) => Page.Props(new { Id = id })).CachePage(TimeSpan.FromMinutes(5));
                if (queryKeys is not null)
                {
                    page.CacheQueryKeys(queryKeys);
                }
            },
            services: s => s.AddOutputCache(),
            configureApp: a => a.UseOutputCache(),
            environment: development ? "Development" : "Testing",
            mode: NetjesMode.Development,
            developmentOutputCache: development);

    /// <summary>
    /// CachePage keeps storing: a render that reads a cookie is stored and served to everyone (the warning says so),
    /// since refusing would turn caching off for apps whose later middleware reads a cookie on every request.
    /// </summary>
    [Fact]
    public async Task ARenderThatReadsACookie_IsStoredAndServedToOtherVisitors()
    {
        await using var app = await StartWithCookieSharedPropsAsync();

        var first = await DataWithCookieAsync(app, "/posts/1", "theme=attacker-chosen");
        var second = await DataWithCookieAsync(app, "/posts/1", "theme=dark");

        Assert.Equal("attacker-chosen", (string?)first["shared"]!["theme"]);
        Assert.Equal("attacker-chosen", (string?)second["shared"]!["theme"]);
        var warning = Assert.Single(app.Logs, l => l.Level == Microsoft.Extensions.Logging.LogLevel.Warning && l.Message.Contains("cookie", StringComparison.Ordinal));
        Assert.Contains("stored", warning.Message, StringComparison.Ordinal);
        Assert.Contains("every visitor", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>Cookie authentication after the output cache reads a cookie on every request: still cached, with the warning.</summary>
    [Fact]
    public async Task AuthenticationAfterTheOutputCache_StillCachesAndWarns()
    {
        var loads = 0;
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", (HttpContext http) => Page.Props(new { Load = Interlocked.Increment(ref loads), User = http.User.Identity?.Name }))
                .CachePage(TimeSpan.FromMinutes(5)),
            services: s =>
            {
                s.AddOutputCache();
                s.AddAuthentication().AddCookie();
            },
            configureApp: a =>
            {
                a.UseOutputCache();
                a.UseAuthentication();
            },
            environment: "Testing",
            mode: NetjesMode.Development);

        await app.GetDataAsync("/");
        await app.GetDataAsync("/");

        Assert.Equal(1, loads);
        Assert.Contains(app.Logs, l => l.Message.Contains("the request cookie '.AspNetCore.Cookies'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CookiesThatNoRenderReads_DoNotKeepAPageOutOfTheCache()
    {
        var loads = 0;
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props(new { Load = Interlocked.Increment(ref loads) })).CachePage(TimeSpan.FromMinutes(5)),
            services: s => s.AddOutputCache(),
            configureApp: a => a.UseOutputCache(),
            environment: "Testing",
            mode: NetjesMode.Development);

        await DataWithCookieAsync(app, "/", "theme=dark; __netjes_draft=forged");
        var second = await DataWithCookieAsync(app, "/", "theme=light");

        Assert.Equal(1, loads);
        Assert.Equal(1, (int?)second["props"]!["load"]);
        Assert.DoesNotContain(app.Logs, l => l.Message.Contains("rendering it read", StringComparison.Ordinal));
    }

    /// <summary>A middleware outside the cache reads after the response is stored or served: not part of the render.</summary>
    [Fact]
    public async Task ACookieReadAfterTheOutputCacheIsDone_DoesNotWarn()
    {
        var loads = 0;
        var reads = 0;
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props(new { Load = Interlocked.Increment(ref loads) })).CachePage(TimeSpan.FromMinutes(5)),
            services: s => s.AddOutputCache(),
            configureApp: a =>
            {
                a.Use(async (http, next) =>
                {
                    await next(http);
                    _ = http.Request.Cookies["consent"];
                    Interlocked.Increment(ref reads);
                });
                a.UseOutputCache();
            },
            environment: "Testing",
            mode: NetjesMode.Development);

        for (var i = 0; i < 3; i++)
        {
            await DataWithCookieAsync(app, "/", "consent=yes");
        }

        Assert.Equal((1, 3), (loads, reads));
        Assert.DoesNotContain(app.Logs, l => l.Message.Contains("rendering it read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARenderThatReadsACookie_WarnsOncePerPage()
    {
        await using var app = await StartWithCookieSharedPropsAsync();

        await DataWithCookieAsync(app, "/posts/1", "theme=dark");
        await DataWithCookieAsync(app, "/posts/2", "theme=dark");

        var warning = Assert.Single(app.Logs, l => l.Level == Microsoft.Extensions.Logging.LogLevel.Warning && l.Message.Contains("cookie", StringComparison.Ordinal));
        Assert.Contains("Page /posts/[id]", warning.Message, StringComparison.Ordinal);
        Assert.Contains("'theme'", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("CacheShell", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("coming", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The warning says a render was stored for every visitor. A render for a query key the page does not name is stored
    /// nowhere, so that one visitor's value reaches nobody else; a render for a key it names still warns.
    /// </summary>
    [Fact]
    public async Task WithCacheQueryKeys_ARenderForAnotherKeyThatReadsACookie_DoesNotWarn()
    {
        await using var app = await StartWithCookieSharedPropsAsync(queryKeys: ["page"]);

        await DataWithCookieAsync(app, "/posts/1?x=1", "theme=dark");

        Assert.DoesNotContain(app.Logs, l => l.Level == Microsoft.Extensions.Logging.LogLevel.Warning && l.Message.Contains("cookie", StringComparison.Ordinal));

        await DataWithCookieAsync(app, "/posts/1?page=1", "theme=dark");

        Assert.Single(app.Logs, l => l.Level == Microsoft.Extensions.Logging.LogLevel.Warning && l.Message.Contains("cookie", StringComparison.Ordinal));
    }

    /// <summary>
    /// Shared props run after the renderer has refused storage, but a loader runs before it, while storage is still
    /// allowed. So what keeps a loader's read quiet is that a render for a key the page does not name has no watch on its cookies.
    /// </summary>
    [Fact]
    public async Task WithCacheQueryKeys_ALoaderThatReadsACookieForAnotherKey_DoesNotWarn()
    {
        await using var app = await TestApp.StartAsync(
            ["app/posts/[id]/page.tsx"],
            map: a => a.MapPageLoader("/posts/[id]", (HttpContext http, string id) => Page.Props(new { Id = id, Theme = http.Request.Cookies["theme"] }))
                .CachePage(TimeSpan.FromMinutes(5))
                .CacheQueryKeys("page"),
            services: s => s.AddOutputCache(),
            configureApp: a => a.UseOutputCache(),
            environment: "Testing",
            mode: NetjesMode.Development);

        var other = await DataWithCookieAsync(app, "/posts/1?x=1", "theme=dark");

        Assert.Equal("dark", (string?)other["props"]!["theme"]);
        Assert.DoesNotContain(app.Logs, l => l.Level == Microsoft.Extensions.Logging.LogLevel.Warning && l.Message.Contains("cookie", StringComparison.Ordinal));

        await DataWithCookieAsync(app, "/posts/1?page=1", "theme=dark");

        Assert.Single(app.Logs, l => l.Level == Microsoft.Extensions.Logging.LogLevel.Warning && l.Message.Contains("cookie", StringComparison.Ordinal));
    }

    private sealed class NoConventions : IEndpointConventionBuilder
    {
        public void Add(Action<EndpointBuilder> convention)
        {
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void CacheQueryKeys_WithAnEmptyOrNullKey_Throws(string? key)
    {
        var thrown = Assert.Throws<ArgumentException>(() => new NoConventions().CacheQueryKeys("page", key!));

        Assert.Equal("keys", thrown.ParamName);
    }

    [Fact]
    public void CacheQueryKeys_WithANullArray_Throws()
    {
        var thrown = Assert.Throws<ArgumentNullException>(() => new NoConventions().CacheQueryKeys(null!));

        Assert.Equal("keys", thrown.ParamName);
    }

    /// <summary>
    /// Groups mix pages and APIs, so the convention does not refuse an endpoint outside the page rules: one that is no page
    /// and not under CachePage keeps ASP.NET Core's rules, where every query key varies the entry and <c>SetVaryByQuery</c> narrows it.
    /// </summary>
    [Fact]
    public async Task CacheQueryKeys_OnAnEndpointThatIsNoPage_ChangesNothing()
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapGet("/api/count", () => Interlocked.Increment(ref calls)).CacheOutput().CacheQueryKeys("page"),
            services: s => s.AddOutputCache(),
            configureApp: a => a.UseOutputCache(),
            environment: "Testing",
            mode: NetjesMode.Development);

        var other = await app.Client.GetStringAsync("/api/count?x=1", Ct);
        var otherAgain = await app.Client.GetAsync("/api/count?x=1", Ct);
        var anotherValue = await app.Client.GetStringAsync("/api/count?x=2", Ct);
        var emptySegment = await app.Client.GetStringAsync("/api/count?&", Ct);
        var bare = await app.Client.GetStringAsync("/api/count", Ct);

        Assert.Equal("1", other);
        Assert.Equal("1", await otherAgain.Content.ReadAsStringAsync(Ct));
        Assert.True(otherAgain.Headers.Age.HasValue);
        Assert.Null(otherAgain.Headers.CacheControl);
        Assert.Equal("2", anotherValue);
        // The parser reads both as no query at all, and ASP.NET Core's key is made from what it read: one entry.
        Assert.Equal(("3", "3"), (emptySegment, bare));
    }

    [Fact]
    public async Task WithoutOutputCaching_RevalidationIsANoOp()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"]);

        await app.App.Services.GetRequiredService<IPageCache>().RevalidateTagAsync("anything", Ct);
    }

    /// <summary>
    /// In the Development environment no page is stored, which would puzzle anyone who watches a cached page render
    /// for every request there: the app says why, once.
    /// </summary>
    [Fact]
    public async Task InDevelopment_AnOutputCachedPage_LogsOnceThatPagesAreNotCachedThere()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx", "app/about/page.tsx"],
            map: a =>
            {
                a.MapPageLoader("/", () => Page.Props()).CachePage(TimeSpan.FromMinutes(5));
                a.MapPageLoader("/about", () => Page.Props()).CachePage(TimeSpan.FromMinutes(5));
            },
            services: s => s.AddOutputCache(),
            configureApp: a => a.UseOutputCache(),
            developmentOutputCache: true);

        foreach (var url in new[] { "/", "/about", "/", "/about" })
        {
            await app.GetDataAsync(url);
        }

        var told = Assert.Single(app.Logs, l => l.Category == "Netjes.Rendering.PageCachePolicy");
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, told.Level);
        Assert.Contains("Development", told.Message, StringComparison.Ordinal);
    }

    /// <summary>Output caching that caches no page is no cause to say so: no page was going to be stored.</summary>
    [Fact]
    public async Task InDevelopment_WithOutputCachingButNoCachedPage_LogsNothing()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapGet("/api/time", () => "now").CacheOutput(),
            services: s => s.AddOutputCache(),
            configureApp: a => a.UseOutputCache(),
            developmentOutputCache: true);

        await app.GetDocumentAsync("/");
        await app.GetDataAsync("/");
        await app.Client.GetStringAsync("/api/time", Ct);

        Assert.DoesNotContain(app.Logs, l => l.Category == "Netjes.Rendering.PageCachePolicy");
    }

    /// <summary>
    /// Only pages render for every request in the Development environment: other endpoints, unless <c>CachePage</c>
    /// marks them, are output-cached as anywhere.
    /// </summary>
    [Fact]
    public async Task InDevelopment_EndpointsThatAreNotPages_AreStillOutputCached()
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapGet("/api/count", () => Interlocked.Increment(ref calls)).CacheOutput(),
            services: s => s.AddOutputCache(),
            configureApp: a => a.UseOutputCache(),
            developmentOutputCache: true);

        var first = await app.Client.GetStringAsync("/api/count", Ct);
        var second = await app.Client.GetStringAsync("/api/count", Ct);

        Assert.Equal(("1", "1"), (first, second));
        Assert.Equal(1, calls);
    }

    /// <summary>
    /// An endpoint that is no page but that <c>CachePage</c> marks, such as a JSON endpoint in a group it is put on, is
    /// under the page rules, so in the Development environment it runs for every request as pages do.
    /// </summary>
    [Fact]
    public async Task InDevelopment_AnEndpointThatIsNoPage_UnderCachePage_RunsForEveryRequest()
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapGroup("/api").CachePage(TimeSpan.FromMinutes(5)).MapGet("/count", () => Interlocked.Increment(ref calls)),
            services: s => s.AddOutputCache(),
            configureApp: a => a.UseOutputCache(),
            developmentOutputCache: true);

        var first = await app.Client.GetStringAsync("/api/count", Ct);
        var second = await app.Client.GetStringAsync("/api/count", Ct);

        Assert.Equal(("1", "2"), (first, second));
        Assert.Equal(2, calls);
    }

    /// <summary>
    /// Nothing is stored in the Development environment, so the cookie a render read reaches no other visitor there,
    /// but it would elsewhere: the warning says both.
    /// </summary>
    [Fact]
    public async Task InDevelopment_ARenderThatReadsACookie_WarnsThatOutsideDevelopmentItIsStored()
    {
        await using var app = await StartWithCookieSharedPropsAsync(development: true);

        await DataWithCookieAsync(app, "/posts/1", "theme=dark");
        await DataWithCookieAsync(app, "/posts/1", "theme=light");

        var warning = Assert.Single(app.Logs, l => l.Level == Microsoft.Extensions.Logging.LogLevel.Warning && l.Message.Contains("cookie", StringComparison.Ordinal));
        Assert.Contains("outside Development", warning.Message, StringComparison.Ordinal);
        Assert.Contains("stored", warning.Message, StringComparison.Ordinal);
        Assert.Contains("every visitor", warning.Message, StringComparison.Ordinal);
    }
}
