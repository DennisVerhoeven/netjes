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

    private static List<RouteEndpoint> Endpoints(TestApp app) =>
        [.. app.App.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()];

    private static RouteEndpoint Endpoint(TestApp app, string route) => Endpoints(app).Single(e => e.RoutePattern.RawText == route);

    private static RouteEndpoint Get(TestApp app, string route) =>
        Endpoints(app).Single(e => e.RoutePattern.RawText == route && Methods(e).Contains(HttpMethods.Get));

    private static IReadOnlyList<string> Methods(Endpoint endpoint) => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];
}
