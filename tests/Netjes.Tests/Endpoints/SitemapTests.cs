using System.Net;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Netjes.Tests.Endpoints;

public class SitemapTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly XNamespace Ns = "http://www.sitemaps.org/schemas/sitemap/0.9";

    [Fact]
    public async Task Sitemap_ListsPublicPagesAndTheStaticParamsOfDynamicOnes()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx", "app/about/page.tsx", "app/blog/[slug]/page.tsx", "app/docs/[...path]/page.tsx", "app/admin/page.tsx", "app/drafts/page.tsx", "app/search/[q]/page.tsx"],
            map: a =>
            {
                a.MapPageLoader("/blog/[slug]", (string slug) => Page.Props()).StaticParams(_ => [new { slug = "hello world" }, new { slug = "second" }]);
                a.MapPageLoader("/docs/[...path]", (string path) => Page.Props()).StaticParams(_ => [new { path = new[] { "guides", "intro" } }]);
                a.MapPageLoader("/admin", () => Page.Props()).RequireAuthorization();
                a.MapPageLoader("/drafts", () => Page.Props()).ExcludeFromSitemap();
                a.MapSitemap(new Uri("https://blog.example/"));
            },
            services: s => s.AddAuthorization().AddAuthentication());

        var response = await app.Client.GetAsync("/sitemap.xml", Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        var xml = XDocument.Parse(text);

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", text);
        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            [
                "https://blog.example/",
                "https://blog.example/about",
                "https://blog.example/blog/hello%20world",
                "https://blog.example/blog/second",
                "https://blog.example/docs/guides/intro",
            ],
            xml.Root!.Elements(Ns + "url").Select(u => (string)u.Element(Ns + "loc")!));
    }

    [Fact]
    public async Task Sitemap_UnderAFallbackPolicy_ListsOnlyAnonymousPages_AndIsItselfPublic()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx", "app/about/page.tsx", "app/pricing/page.tsx", "app/account/page.tsx"],
            map: a =>
            {
                a.MapPageLoader("/about", () => Page.Props()).AllowAnonymous();
                a.MapPageLoader("/pricing", () => Page.Props()).RequireAuthorization().AllowAnonymous();
                a.MapPageLoader("/account", () => Page.Props());
                a.MapSitemap(new Uri("https://shop.example/"));
                a.MapRobotsTxt(new Uri("https://shop.example/"));
            },
            services: s => s
                .AddAuthorization(o => o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
                .AddAuthentication(),
            configureApp: a => a.UseAuthorization());

        var response = await app.Client.GetAsync("/sitemap.xml", Ct);
        var robots = await app.Client.GetAsync("/robots.txt", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, robots.StatusCode);
        Assert.Equal(
            ["https://shop.example/about", "https://shop.example/pricing"],
            XDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).Root!.Elements(Ns + "url").Select(u => (string)u.Element(Ns + "loc")!));
    }

    [Fact]
    public async Task Sitemap_IncludesTheBasePath_AndIsBuiltOnceForAWhile()
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx", "app/blog/[slug]/page.tsx"],
            pathBase: "/base",
            map: a =>
            {
                a.MapPageLoader("/blog/[slug]", (string slug) => Page.Props()).StaticParams(_ =>
                {
                    calls++;
                    return [new { slug = "hello" }];
                });
                a.MapSitemap(new Uri("https://blog.example"));
            });

        var first = await (await app.Client.GetAsync("/base/sitemap.xml", Ct)).Content.ReadAsStringAsync(Ct);
        var second = await app.Client.GetAsync("/base/sitemap.xml", Ct);

        Assert.Equal(
            ["https://blog.example/base/", "https://blog.example/base/blog/hello"],
            XDocument.Parse(first).Root!.Elements(Ns + "url").Select(u => (string)u.Element(Ns + "loc")!));
        Assert.Equal(first, await second.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, calls);
        Assert.Equal(TimeSpan.FromHours(1), second.Headers.CacheControl?.MaxAge);
    }

    [Fact]
    public async Task RobotsTxt_PointsAtTheSitemapAndListsDisallowedPaths()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a =>
            {
                a.MapSitemap(new Uri("https://blog.example"));
                a.MapRobotsTxt(new Uri("https://blog.example"), disallow: ["/admin", "/_netjes/"]);
            });

        var response = await app.Client.GetAsync("/robots.txt", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            "User-agent: *\nDisallow: /admin\nDisallow: /_netjes/\n\nSitemap: https://blog.example/sitemap.xml\n",
            await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task RobotsTxt_UnderABasePath_PointsAtTheSitemapUnderTheBasePath()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            pathBase: "/base",
            map: a =>
            {
                a.MapSitemap(new Uri("https://blog.example"));
                a.MapRobotsTxt(new Uri("https://blog.example"));
            });

        var robots = await (await app.Client.GetAsync("/base/robots.txt", Ct)).Content.ReadAsStringAsync(Ct);
        var sitemapUrl = robots.Split('\n').Single(l => l.StartsWith("Sitemap: ", StringComparison.Ordinal))["Sitemap: ".Length..];
        var sitemap = await app.Client.GetAsync(new Uri(sitemapUrl).AbsolutePath, Ct);

        Assert.Equal("https://blog.example/base/sitemap.xml", sitemapUrl);
        Assert.Equal(HttpStatusCode.OK, sitemap.StatusCode);
        var firstLoc = XDocument.Parse(await sitemap.Content.ReadAsStringAsync(Ct)).Descendants(Ns + "loc").First().Value;
        Assert.Equal("https://blog.example/base/", firstLoc);
    }

    [Theory]
    [InlineData("/x\nDisallow: /\n#")]
    [InlineData("/x\r\nDisallow: /")]
    [InlineData("/my docs")]
    [InlineData("/caf\u00e9")]
    public async Task RobotsTxt_EncodesABasePathItCannotTrust(string basePath)
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapRobotsTxt(new Uri("https://site.example")),
            beforeNetjes: a => a.Use((c, next) =>
            {
                c.Request.PathBase = new PathString(basePath);
                return next(c);
            }));

        var robots = await (await app.Client.GetAsync("/robots.txt", Ct)).Content.ReadAsStringAsync(Ct);
        var lines = robots.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(["User-agent: *", "Allow: /"], lines[..2]);
        Assert.DoesNotContain(lines, l => l.StartsWith("Disallow", StringComparison.Ordinal));
        var sitemapLine = Assert.Single(lines, l => l.StartsWith("Sitemap: ", StringComparison.Ordinal));
        Assert.True(Uri.TryCreate(sitemapLine["Sitemap: ".Length..], UriKind.Absolute, out var uri));
        Assert.EndsWith("/sitemap.xml", uri.AbsoluteUri);
        Assert.Equal(3, lines.Length);
    }

    [Theory]
    [InlineData("/my docs", "https://site.example/my%20docs/")]
    [InlineData("/caf\u00e9", "https://site.example/caf%C3%A9/")]
    [InlineData("/a#b", "https://site.example/a%23b/")]
    [InlineData("/a?b", "https://site.example/a%3Fb/")]
    [InlineData("/a\nb", "https://site.example/a%0Ab/")]
    public async Task Sitemap_EncodesABasePathInItsLocs(string basePath, string expectedLoc)
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapSitemap(new Uri("https://site.example")),
            beforeNetjes: a => a.Use((c, next) =>
            {
                c.Request.PathBase = new PathString(basePath);
                return next(c);
            }));

        var xml = await (await app.Client.GetAsync("/sitemap.xml", Ct)).Content.ReadAsStringAsync(Ct);
        var loc = XDocument.Parse(xml).Descendants(Ns + "loc").Single().Value;

        Assert.Equal(expectedLoc, loc);
        Assert.True(Uri.IsWellFormedUriString(loc, UriKind.Absolute));
        var uri = new Uri(loc);
        Assert.Equal("", uri.Query);
        Assert.Equal("", uri.Fragment);
    }

    /// <summary>
    /// A client that chooses the base path (an <c>X-Forwarded-Prefix</c> the app takes from anyone) would get, for one
    /// small request, a sitemap that repeats its prefix in every URL, built anew each time: a base path too long to
    /// keep is not found, and nothing is built for it. Its length is the one the XML would carry: percent-encoded, with
    /// each <c>&amp;</c> (which a URL path may hold as it is) written as <c>&amp;amp;</c>.
    /// </summary>
    [Theory]
    [InlineData("plain")]
    [InlineData("percent-encoded")]
    [InlineData("ampersands")]
    public async Task Sitemap_UnderABasePathTooLongToKeep_IsNotFound_AndNothingIsBuilt(string encoding)
    {
        var listings = 0;
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx", "app/blog/[slug]/page.tsx"],
            map: a =>
            {
                a.MapPageLoader("/blog/[slug]", (string slug) => Page.Props()).StaticParams(_ =>
                {
                    Interlocked.Increment(ref listings);
                    return Enumerable.Range(0, 500).Select(i => (object)new { slug = $"post-{i}" });
                });
                a.MapSitemap(new Uri("https://site.example"));
            },
            services: s => s.AddLogging(l => l.SetMinimumLevel(LogLevel.Debug)),
            beforeNetjes: a =>
            {
                var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedPrefix };
                forwarded.KnownIPNetworks.Clear();
                forwarded.KnownProxies.Clear();
                a.UseForwardedHeaders(forwarded);
            });
        // Under the limit as the decoded path (or the percent-encoded one), over it as the URLs in the XML would carry it.
        var prefix = encoding switch
        {
            "plain" => "/" + new string('a', SitemapCache.MaxBasePathLength),
            "percent-encoded" => "/" + string.Concat(Enumerable.Repeat("%C3%A9", SitemapCache.MaxBasePathLength / 2)),
            _ => "/" + new string('&', SitemapCache.MaxBasePathLength / 4),
        };

        var request = new HttpRequestMessage(HttpMethod.Get, "/sitemap.xml");
        request.Headers.Add("X-Forwarded-Prefix", prefix);
        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(0, listings);
        Assert.Contains(app.Logs, e => e.Level == LogLevel.Debug && e.Message.Contains("sitemap.xml", StringComparison.Ordinal));

        var atTheLimit = new HttpRequestMessage(HttpMethod.Get, "/sitemap.xml");
        // 256 characters in the XML either way: the ampersands take five characters each there.
        var longest = encoding == "ampersands"
            ? "/" + new string('&', (SitemapCache.MaxBasePathLength - 1) / 5)
            : "/" + new string('a', SitemapCache.MaxBasePathLength - 1);
        atTheLimit.Headers.Add("X-Forwarded-Prefix", longest);
        var served = await app.Client.SendAsync(atTheLimit, Ct);

        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        var locs = XDocument.Parse(await served.Content.ReadAsStringAsync(Ct)).Descendants(Ns + "loc").ToList();
        Assert.Equal(501, locs.Count);
        Assert.All(locs, loc => Assert.StartsWith("https://site.example" + longest + "/", loc.Value, StringComparison.Ordinal));
        Assert.Equal(1, listings);
    }

    [Fact]
    public async Task SitemapCache_KeepsTheXmlOfFewBasePaths_NoMatterHowManyAreRequested()
    {
        var cache = new SitemapCache("https://site.example", TimeSpan.FromHours(1));
        var now = DateTimeOffset.UnixEpoch;
        var builds = 0;
        Task<List<string>> Paths()
        {
            builds++;
            return Task.FromResult(new List<string> { "/" });
        }

        for (var i = 0; i < SitemapCache.MaxBasePaths * 10; i++)
        {
            var xml = await cache.GetAsync($"/prefix{i}", now, Paths);
            Assert.Contains($"https://site.example/prefix{i}/", System.Text.Encoding.UTF8.GetString(xml!));
        }

        Assert.Equal(SitemapCache.MaxBasePaths, cache.Count);
        Assert.Equal(1, builds);
    }

    [Fact]
    public async Task SitemapCache_BuildsNothingForALongBasePath()
    {
        var cache = new SitemapCache("https://site.example", TimeSpan.FromHours(1));
        var now = DateTimeOffset.UnixEpoch;
        var builds = 0;
        Task<List<string>> Paths()
        {
            builds++;
            return Task.FromResult(new List<string> { "/" });
        }

        var xml = await cache.GetAsync("/" + new string('a', SitemapCache.MaxBasePathLength), now, Paths);

        Assert.Null(xml);
        Assert.Equal(0, builds);
        Assert.Equal(0, cache.Count);
        await cache.GetAsync("/" + new string('a', SitemapCache.MaxBasePathLength - 1), now, Paths);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task SitemapCache_RebuildsAfterItsLifetime()
    {
        var cache = new SitemapCache("https://site.example", TimeSpan.FromHours(1));
        var builds = 0;
        Task<List<string>> Paths()
        {
            builds++;
            return Task.FromResult(new List<string> { "/" });
        }

        var start = DateTimeOffset.UnixEpoch;
        await cache.GetAsync("", start, Paths);
        await cache.GetAsync("", start.AddMinutes(59), Paths);
        await cache.GetAsync("", start.AddMinutes(61), Paths);

        Assert.Equal(2, builds);
    }
}
