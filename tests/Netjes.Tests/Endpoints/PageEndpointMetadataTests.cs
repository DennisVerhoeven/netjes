using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Netjes.Routing;

namespace Netjes.Tests.Endpoints;

/// <summary>What an app or a test reads from the endpoints to find its pages, their loaders and their CachePage settings.</summary>
public class PageEndpointMetadataTests
{
    [Fact]
    public async Task EveryPageEndpoint_CarriesPageRouteMetadata_WithItsPatternAndWhetherItIsTheLoader()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx", "app/blog/[slug]/page.tsx", "app/contact/page.tsx", "app/projects/page.tsx"],
            map: a =>
            {
                a.MapPageLoader("/blog/[slug]", (string slug) => Page.Props());
                a.MapPageAction("/blog/[slug]", () => Page.Redirect("/"));
                a.MapPageAction("/contact", () => Page.Redirect("/contact"));
                a.MapGroup("/{tenant}").MapPageLoader("/projects", (string tenant) => Page.Props());
                a.MapAction("posts.like", () => ServerAction.Ok());
                a.MapSitemap(new Uri("https://example.com"));
            });

        var pages = Endpoints(app)
            .Select(e => (Endpoint: e, Page: e.Metadata.GetMetadata<PageRouteMetadata>()))
            .Where(e => e.Page is not null)
            .Select(e => (Route: e.Endpoint.RoutePattern.RawText, Methods: string.Join(",", Methods(e.Endpoint)), e.Page!.Pattern, e.Page.IsLoader))
            .OrderBy(e => e.Route, StringComparer.Ordinal)
            .ThenBy(e => e.Methods, StringComparer.Ordinal);

        Assert.Equal(
            [
                ("/", "GET,HEAD", "/", false),
                ("/blog/{slug}", "GET,HEAD", "/blog/[slug]", true),
                ("/blog/{slug}", "POST", "/blog/[slug]", false),
                ("/contact", "GET,HEAD", "/contact", false),
                ("/contact", "POST", "/contact", false),
                // The page's own URL, without the group's prefix.
                ("/{tenant}/projects", "GET,HEAD", "/projects", true),
            ],
            pages);
        Assert.Null(Endpoint(app, "{**path:nonfile}").Metadata.GetMetadata<PageRouteMetadata>());
        Assert.Null(Endpoint(app, "/_netjes/actions/posts.like").Metadata.GetMetadata<PageRouteMetadata>());
        Assert.Null(Endpoint(app, "/_netjes/image").Metadata.GetMetadata<PageRouteMetadata>());
        Assert.Null(Endpoint(app, "/sitemap.xml").Metadata.GetMetadata<PageRouteMetadata>());
    }

    [Fact]
    public async Task CachePage_PutsItsDurationAndTagsOnTheEndpoint()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx", "app/about/page.tsx", "app/blog/[slug]/page.tsx", "app/plain/page.tsx"],
            map: a =>
            {
                a.MapPageLoader("/blog/[slug]", (string slug) => Page.Props()).CachePage(TimeSpan.FromSeconds(30), "posts", "blog");
                a.MapGet("/render", () => Page.Render("/about")).CachePage(TimeSpan.FromMinutes(5));
                a.MapPageLoader("/plain", () => Page.Props()).CacheOutput(policy => policy.Expire(TimeSpan.FromMinutes(2)));
            },
            pages: p => p.CachePage(TimeSpan.FromHours(1)));

        var blog = Get(app, "/blog/{slug}").Metadata.GetMetadata<CachedPageMetadata>();
        Assert.Equal(TimeSpan.FromSeconds(30), blog?.Duration);
        Assert.Equal(["posts", "blog"], blog?.Tags ?? []);
        Assert.All(["/", "/about"], route =>
        {
            var page = Get(app, route).Metadata.GetMetadata<CachedPageMetadata>();
            Assert.Equal(TimeSpan.FromHours(1), page?.Duration);
            Assert.Empty(page?.Tags ?? ["no metadata"]);
        });
        Assert.Equal(TimeSpan.FromMinutes(5), Get(app, "/render").Metadata.GetMetadata<CachedPageMetadata>()?.Duration);
        Assert.Null(Get(app, "/plain").Metadata.GetMetadata<CachedPageMetadata>());
    }

    [Fact]
    public async Task CacheOutput_AfterCachePage_ReplacesItsPolicy_WhileTheMetadataKeepsWhatCachePageWasGiven()
    {
        var loads = new Dictionary<string, int>();
        await using var app = await TestApp.StartAsync(
            ["app/posts/page.tsx", "app/news/page.tsx"],
            map: a =>
            {
                a.MapPageLoader("/posts", () => Load(loads, "posts")).CachePage(TimeSpan.FromHours(1), "content");
                a.MapPageLoader("/news", () => Load(loads, "news")).CachePage(TimeSpan.FromHours(1), "content")
                    .CacheOutput(policy => policy.Expire(TimeSpan.FromHours(1)));
            },
            services: s => s.AddOutputCache(),
            configureApp: a => a.UseOutputCache(),
            environment: "Testing",
            mode: NetjesMode.Development);
        await app.GetDataAsync("/posts");
        await app.GetDataAsync("/news");

        await app.App.Services.GetRequiredService<IPageCache>().RevalidateTagAsync("content", TestContext.Current.CancellationToken);
        await app.GetDataAsync("/posts");
        var news = await app.GetDataAsync("/news");

        // CachePage's tag evicts /posts. The later policy has no tag, so /news is still served from the cache.
        Assert.Equal((2, 1), (loads["posts"], loads["news"]));
        Assert.NotNull(news.Headers.Age);
        Assert.Equal(["content"], Get(app, "/news").Metadata.GetMetadata<CachedPageMetadata>()?.Tags ?? []);

        // The page rules tag every entry with its path, under any policy.
        await app.App.Services.GetRequiredService<IPageCache>().RevalidatePathAsync("/news", TestContext.Current.CancellationToken);
        await app.GetDataAsync("/news");
        Assert.Equal(2, loads["news"]);
    }

    private static PageResult<NoProps> Load(Dictionary<string, int> loads, string page)
    {
        loads[page] = loads.GetValueOrDefault(page) + 1;
        return Page.Props();
    }

    private static List<RouteEndpoint> Endpoints(TestApp app) =>
        [.. app.App.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()];

    private static RouteEndpoint Endpoint(TestApp app, string route) => Endpoints(app).Single(e => e.RoutePattern.RawText == route);

    private static RouteEndpoint Get(TestApp app, string route) =>
        Endpoints(app).Single(e => e.RoutePattern.RawText == route && Methods(e).Contains(HttpMethods.Get));

    private static IReadOnlyList<string> Methods(Endpoint endpoint) => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];
}
