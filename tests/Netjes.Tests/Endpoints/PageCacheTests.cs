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
            configureApp: a => a.UseOutputCache());
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
            configureApp: a => a.UseOutputCache());

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

    private static Task<TestApp> StartWithCookieSharedPropsAsync() =>
        TestApp.StartAsync(
            ["app/posts/[id]/page.tsx"],
            configure: o => o.SharedProps = http => ValueTask.FromResult<object?>(new { Theme = http.Request.Cookies["theme"] }),
            map: a => a.MapPageLoader("/posts/[id]", (string id) => Page.Props(new { Id = id })).CachePage(TimeSpan.FromMinutes(5)),
            services: s => s.AddOutputCache(),
            configureApp: a => a.UseOutputCache());

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
            });

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
            configureApp: a => a.UseOutputCache());

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
            });

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

    [Fact]
    public async Task WithoutOutputCaching_RevalidationIsANoOp()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"]);

        await app.App.Services.GetRequiredService<IPageCache>().RevalidateTagAsync("anything", Ct);
    }
}
