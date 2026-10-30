using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Netjes.Tests.Endpoints;

/// <summary>
/// Pages carry per-user data (shared props, antiforgery tokens), so neither browsers nor shared caches keep them: a
/// browser shows a page it kept again on back and forward without revalidating it, so Back after a sign-out would show
/// the signed-out user's page. Responses the output cache stores and pages that set their own header are left alone.
/// </summary>
public class CacheControlTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A returning visitor: the antiforgery cookie from a first visit (which ASP.NET Core sends no-store).</summary>
    private static async Task<HttpResponseMessage> ReturningVisitAsync(TestApp app, string url, bool data)
    {
        var first = await app.Client.GetAsync(url, Ct);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (first.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            request.Headers.Add("Cookie", cookies.Select(c => c.Split(';')[0]));
        }

        if (data)
        {
            request.Headers.Add(NetjesHeaders.Data, "1");
        }

        return await app.Client.SendAsync(request, Ct);
    }

    /// <summary>
    /// A visitor's page too: its document stays when the client router signs the visitor in, and then shows their
    /// pages, so a browser that kept it would show them again on Back after a sign-out in a later document.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pages_AreKeptOutOfEveryCache(bool data)
    {
        await using var app = await TestApp.StartAsync(["app/about/page.tsx"]);

        var response = await ReturningVisitAsync(app, "/about", data);

        Assert.Equal("no-store, private", response.Headers.CacheControl?.ToString());
    }

    /// <summary>
    /// Browsers show a page they keep again on back and forward without revalidating it, <c>no-cache</c> or not, so
    /// after signing out (without JavaScript, or after a full page load) Back would show the signed-out user's page.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PagesForASignedInUser_AreNotStored(bool data)
    {
        await using var app = await TestApp.StartAsync(
            ["app/about/page.tsx"],
            configureApp: a => a.Use((http, next) =>
            {
                http.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim("sub", "ada")], "Test"));
                return next(http);
            }));

        var response = await ReturningVisitAsync(app, "/about", data);

        Assert.Equal("no-store, private", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task NotFoundPages_GetTheDefaultToo()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"]);

        var response = await ReturningVisitAsync(app, "/missing", data: true);

        Assert.Equal("no-store, private", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task CachedPages_AreLeftToOutputCaching()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props()).CachePage(TimeSpan.FromMinutes(1)),
            services: s => s.AddOutputCache(),
            configureApp: a => a.UseOutputCache());

        var response = await app.GetDataAsync("/");

        Assert.Null(response.Headers.CacheControl);
    }

    /// <summary>
    /// A cached page's render that the output cache does not store is one visitor's page (their shared props, their
    /// nonce): a CDN or proxy in front must not keep it, and neither must the browser.
    /// </summary>
    [Theory]
    [InlineData("authorization header")]
    [InlineData("signed-in user")]
    [InlineData("nonce")]
    [InlineData("not found")]
    [InlineData("cookie")]
    public async Task CachedPages_RenderedButNotStored_AreKeptOutOfEveryCache(string reason)
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configure: o => o.ContentSecurityPolicy = reason == "nonce" ? "script-src 'nonce-{nonce}'" : null,
            map: a => a.MapPageLoader("/", (HttpContext http) =>
            {
                if (reason == "cookie")
                {
                    http.Response.Cookies.Append("visited", "1");
                }

                return reason == "not found" ? Page.NotFound() : Page.Props();
            }).CachePage(TimeSpan.FromMinutes(1)),
            services: s => s.AddOutputCache(),
            configureApp: a =>
            {
                if (reason == "signed-in user")
                {
                    a.Use((http, next) =>
                    {
                        http.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                            [new System.Security.Claims.Claim("sub", "ada")], "Test"));
                        return next(http);
                    });
                }

                a.UseOutputCache();
            });
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        if (reason == "authorization header")
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "token");
        }

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal("no-store, private", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task CachedPages_StoredDocuments_AreLeftToOutputCaching()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props()).CachePage(TimeSpan.FromMinutes(1)),
            services: s => s.AddOutputCache(),
            configureApp: a => a.UseOutputCache());

        var first = await app.GetDocumentAsync("/");
        var stored = await app.GetDocumentAsync("/");

        Assert.Null(first.Headers.CacheControl);
        Assert.Null(stored.Headers.CacheControl);
        Assert.Single(app.Renderer.Payloads);
    }

    [Fact]
    public async Task APageThatSetsItsOwnHeader_KeepsIt()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", (HttpContext http) =>
            {
                http.Response.Headers[HeaderNames.CacheControl] = "public, max-age=60";
                return Page.Props();
            }));

        var response = await ReturningVisitAsync(app, "/", data: true);

        Assert.Equal("public, max-age=60", response.Headers.CacheControl?.ToString());
    }
}
