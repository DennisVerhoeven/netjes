using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;

namespace Netjes.Tests.Endpoints;

/// <summary>
/// What the caching guide's "Endpoints that are not pages" says about the JSON APIs, text files and webhooks an app maps
/// next to its pages: Netjes sets no <c>Cache-Control</c> on them, and its page rules leave their output-cache entries to
/// ASP.NET Core's keys and rules. Each test pins one claim of that section, so whoever changes it is sent to the guide.
/// </summary>
public class NonPageEndpointCacheTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// An app with output caching, in the Testing environment with Netjes in development mode: in the Development
    /// environment no page is output-cached, which the endpoints under test would not notice, but a page next to them
    /// would.
    /// </summary>
    private static Task<TestApp> StartAsync(
        Action<WebApplication> map,
        Action<OutputCacheOptions>? outputCache = null,
        Action<WebApplication>? beforeTheOutputCache = null) =>
        TestApp.StartAsync(
            ["app/page.tsx"],
            map: map,
            services: s => s.AddOutputCache(outputCache ?? (_ => { })),
            configureApp: a =>
            {
                beforeTheOutputCache?.Invoke(a);
                a.UseOutputCache();
            },
            environment: "Testing",
            mode: NetjesMode.Development);

    [Fact]
    public async Task AnEndpointOfTheApp_GetsNoCacheControlFromNetjes()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a =>
            {
                a.MapGet("/api/items", (int? take) => Results.Ok(new { Take = take }));
                a.MapGet("/llms.txt", () => Results.Text("Netjes", "text/plain"));
            },
            environment: "Testing",
            mode: NetjesMode.Development);

        var json = await app.Client.GetAsync("/api/items?take=3", Ct);
        var text = await app.Client.GetAsync("/llms.txt", Ct);
        // Minimal API binding refuses the value itself, before the handler runs.
        var refused = await app.Client.GetAsync("/api/items?take=x", Ct);

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.BadRequest], [json.StatusCode, text.StatusCode, refused.StatusCode]);
        Assert.All([json, text, refused], response => Assert.False(response.Headers.Contains("Cache-Control")));

        // The page of the same app, for contrast (its first visit also sets the antiforgery cookie, whose own
        // Cache-Control wins, so a returning visitor).
        var firstVisit = await app.GetDocumentAsync("/");
        var returning = new HttpRequestMessage(HttpMethod.Get, "/");
        returning.Headers.Add("Cookie", firstVisit.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        var page = await app.Client.SendAsync(returning, Ct);
        Assert.Equal("no-store, private", page.Headers.CacheControl?.ToString());
    }

    /// <summary>
    /// The output cache stores the headers the handler set before it wrote the body, and sends them with every answer
    /// it serves; it adds an <c>Age</c> and no <c>Cache-Control</c> of its own.
    /// </summary>
    [Fact]
    public async Task AnEndpointOfTheApp_KeepsItsOwnCacheControl_AlsoFromTheOutputCache()
    {
        var calls = 0;
        await using var app = await StartAsync(a => a.MapGet("/api/search", (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "public, max-age=300";
            return Results.Ok(new { Call = Interlocked.Increment(ref calls) });
        }).CacheOutput());

        var first = await app.Client.GetAsync("/api/search", Ct);
        var second = await app.Client.GetAsync("/api/search", Ct);

        Assert.Equal(1, calls);
        Assert.Equal("public, max-age=300", first.Headers.CacheControl?.ToString());
        Assert.Equal("public, max-age=300", second.Headers.CacheControl?.ToString());
        Assert.False(first.Headers.Contains("Age"));
        Assert.True(second.Headers.Contains("Age"));
    }

    /// <summary>
    /// The key holds the path upper-cased unless <c>UseCaseSensitivePaths</c> is set, route values included: the
    /// handler answers the first spelling to arrive, and every other spelling is served that answer.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnOutputCachedEndpointOfTheApp_SharesOneEntryForThePathInAnyCase_UnlessPathsAreCaseSensitive(bool caseSensitive)
    {
        var calls = 0;
        await using var app = await StartAsync(
            a => a.MapGet("/codes/{code}", (string code) => $"{code}#{Interlocked.Increment(ref calls)}").CacheOutput(),
            outputCache: o => o.UseCaseSensitivePaths = caseSensitive);

        var answers = new[]
        {
            await app.Client.GetStringAsync("/codes/Ab", Ct),
            await app.Client.GetStringAsync("/codes/aB", Ct),
            await app.Client.GetStringAsync("/CODES/Ab", Ct),
        };

        Assert.Equal(caseSensitive ? ["Ab#1", "aB#2", "Ab#3"] : ["Ab#1", "Ab#1", "Ab#1"], answers);
        Assert.Equal(caseSensitive ? 3 : 1, calls);
    }

    /// <summary>A condition on the policy stores one spelling: any other is neither looked up nor stored.</summary>
    [Fact]
    public async Task AnOutputCachedEndpointOfTheApp_WithAPathCondition_StoresOnlyThatSpelling()
    {
        var calls = 0;
        await using var app = await StartAsync(a => a
            .MapGet("/llms.txt", () => Results.Text($"call {Interlocked.Increment(ref calls)}", "text/plain"))
            .CacheOutput(p => p.With(context => context.HttpContext.Request.Path.Value == "/llms.txt")));

        var answers = new[]
        {
            await app.Client.GetStringAsync("/llms.txt", Ct),
            await app.Client.GetStringAsync("/llms.txt", Ct),
            await app.Client.GetStringAsync("/LLMS.TXT", Ct),
            await app.Client.GetStringAsync("/LLMS.TXT", Ct),
        };

        Assert.Equal(["call 1", "call 1", "call 2", "call 3"], answers);
    }

    /// <summary>
    /// ASP.NET Core's default policy keys on every query key. <c>SetVaryByQuery</c> narrows that to the keys it names,
    /// or to none with an empty array: the call has no form without arguments.
    /// </summary>
    [Theory]
    [InlineData("every key", 3)]
    [InlineData("no key", 1)]
    [InlineData("page", 2)]
    public async Task AnOutputCachedEndpointOfTheApp_StoresACopyPerQueryString_UnlessItNarrowsTheQuery(string varyBy, int expectedCalls)
    {
        var calls = 0;
        await using var app = await StartAsync(a =>
        {
            var endpoint = a.MapGet("/api/items", () => Interlocked.Increment(ref calls));
            _ = varyBy switch
            {
                "every key" => endpoint.CacheOutput(),
                "no key" => endpoint.CacheOutput(p => p.SetVaryByQuery([])),
                _ => endpoint.CacheOutput(p => p.SetVaryByQuery(varyBy)),
            };
        });

        foreach (var url in new[] { "/api/items?page=1", "/api/items?page=2", "/api/items?page=1&utm_source=feed", "/api/items?page=1" })
        {
            await app.Client.GetStringAsync(url, Ct);
        }

        Assert.Equal(expectedCalls, calls);
    }

    /// <summary>
    /// ASP.NET Core's default policy stores the answer to a <c>GET</c> or <c>HEAD</c> that has no <c>Authorization</c>
    /// header and no signed-in user, when the status is 200 and the response sets no cookie.
    /// </summary>
    [Theory]
    [InlineData("an anonymous request for a 200 without a cookie", 1)]
    [InlineData("an Authorization header", 3)]
    [InlineData("a signed-in user", 3)]
    [InlineData("a cookie set by the response", 3)]
    [InlineData("a status other than 200", 3)]
    public async Task AnOutputCachedEndpointOfTheApp_IsStoredOnlyForAnAnonymous200WithoutACookie(string scenario, int expectedCalls)
    {
        var calls = 0;
        await using var app = await StartAsync(
            a => a.MapGet("/api/items", (HttpContext http) =>
            {
                Interlocked.Increment(ref calls);
                if (scenario == "a cookie set by the response")
                {
                    http.Response.Cookies.Append("visit", "1");
                }

                return scenario == "a status other than 200" ? Results.NotFound() : Results.Ok();
            }).CacheOutput(),
            beforeTheOutputCache: a => a.Use((http, next) =>
            {
                if (scenario == "a signed-in user")
                {
                    http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "Test"));
                }

                return next(http);
            }));

        for (var i = 0; i < 3; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/items");
            if (scenario == "an Authorization header")
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "token");
            }

            await app.Client.SendAsync(request, Ct);
        }

        Assert.Equal(expectedCalls, calls);
    }

    /// <summary>The policy the guide shows for a text file, run as written.</summary>
    [Fact]
    public async Task ATextFileCachedWithTheGuidesPolicy_IsStoredOnceForAnyQueryString_UntilItsTagIsRevalidated()
    {
        var calls = 0;
        await using var app = await StartAsync(a => a
            .MapGet("/llms.txt", () => Results.Text($"call {Interlocked.Increment(ref calls)}", "text/plain; charset=utf-8"))
            .CacheOutput(p => p.SetVaryByQuery([]).Expire(TimeSpan.FromMinutes(10)).Tag("docs")));

        var stored = new[]
        {
            await app.Client.GetStringAsync("/llms.txt", Ct),
            await app.Client.GetStringAsync("/llms.txt?v=1", Ct),
            await app.Client.GetStringAsync("/llms.txt?v=2&utm_source=feed", Ct),
        };
        await app.App.Services.GetRequiredService<IPageCache>().RevalidateTagAsync("docs", Ct);
        var evicted = await app.Client.GetStringAsync("/llms.txt?v=1", Ct);

        Assert.Equal(["call 1", "call 1", "call 1"], stored);
        Assert.Equal("call 2", evicted);
    }

    /// <summary>
    /// The path tag that <c>RevalidatePath</c> evicts by belongs to the page rules, which an endpoint of the app does
    /// not get. A tag the policy sets is evicted by <c>Revalidate</c>, as for a page.
    /// </summary>
    [Fact]
    public async Task RevalidatePath_LeavesEndpointsOfTheAppInTheCache_RevalidateTagEvictsThem()
    {
        var calls = 0;
        await using var app = await StartAsync(a => a
            .MapGet("/api/count", () => Interlocked.Increment(ref calls))
            .CacheOutput(p => p.Tag("docs")));
        var cache = app.App.Services.GetRequiredService<IPageCache>();

        await app.Client.GetStringAsync("/api/count", Ct);
        await cache.RevalidatePathAsync("/api/count", Ct);
        var afterThePath = await app.Client.GetStringAsync("/api/count", Ct);
        await cache.RevalidateTagAsync("docs", Ct);
        var afterTheTag = await app.Client.GetStringAsync("/api/count", Ct);

        Assert.Equal(("1", "2"), (afterThePath, afterTheTag));
        Assert.Equal(2, calls);
    }

    /// <summary>
    /// The draft cookie is sent by hand, as in the image endpoint's tests: outside Development it is
    /// <c>SameSite=None; Secure</c>, which a browser would not send over plain HTTP.
    /// </summary>
    [Fact]
    public async Task InDraftMode_AnOutputCachedEndpointOfTheApp_IsNeitherServedNorStored()
    {
        var calls = 0;
        await using var app = await StartAsync(a =>
        {
            a.MapGet("/api/draft/on", (IDraftMode draft) =>
            {
                draft.Enable();
                return Results.Ok();
            });
            a.MapGet("/api/count", () => Interlocked.Increment(ref calls)).CacheOutput();
        });
        var on = await app.Client.GetAsync("/api/draft/on", Ct);
        var draft = on.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(DraftMode.CookieName + "=", StringComparison.Ordinal)).Split(';')[0];

        async Task<string> CountAsync(string? cookie)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/count");
            if (cookie is not null)
            {
                request.Headers.Add("Cookie", cookie);
            }

            return await (await app.Client.SendAsync(request, Ct)).Content.ReadAsStringAsync(Ct);
        }

        var visitor = await CountAsync(null);
        var editor = await CountAsync(draft);
        var editorAgain = await CountAsync(draft);
        var visitorAgain = await CountAsync(null);

        // The editor is not served the visitors' entry, and what answers the editor is not stored for the next request.
        Assert.Equal(["1", "2", "3", "1"], [visitor, editor, editorAgain, visitorAgain]);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task TheSitemapSendsACacheControl_TheRobotsFileSendsNone()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a =>
            {
                a.MapSitemap(new Uri("https://blog.example/"));
                a.MapRobotsTxt(new Uri("https://blog.example/"));
            });

        var sitemap = await app.Client.GetAsync("/sitemap.xml", Ct);
        var robots = await app.Client.GetAsync("/robots.txt", Ct);

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK], [sitemap.StatusCode, robots.StatusCode]);
        Assert.Equal("public, max-age=3600", sitemap.Headers.CacheControl?.ToString());
        Assert.False(robots.Headers.Contains("Cache-Control"));
    }
}
