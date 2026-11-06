using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.StaticAssets;
using Microsoft.Extensions.DependencyInjection;
using Netjes.Routing;
using Netjes.Tests.Endpoints;

namespace Netjes.Tests.Routing;

public class AppEndpointsTests
{
    private static RouteEndpoint Endpoint(RoutePattern pattern, int order = 0, params object[] metadata) =>
        new(_ => Task.CompletedTask, pattern, order, new EndpointMetadataCollection(metadata), pattern.RawText);

    private static readonly HttpMethodMetadata Get = new([HttpMethods.Get]);

    [Fact]
    public void IsTheAppsOwn_LeavesOutConventionalRoutesFallbacksAndStaticAssets_AndKeepsAttributeRoutes()
    {
        var kept = new Dictionary<string, RouteEndpoint>
        {
            ["a minimal API"] = Endpoint(RoutePatternFactory.Parse("/api/ping"), 0, Get),
            // An MVC attribute route: its required values, which are defaults too, name its action, not parameters of its pattern.
            ["an attribute route"] = Endpoint(
                RoutePatternFactory.Parse(
                    "api/Products/{id:int}", defaults: new { controller = "Products", action = "Get" }, parameterPolicies: null,
                    requiredValues: new { controller = "Products", action = "Get" }),
                0, Get),
            ["a file next to the pages, ahead of them"] = Endpoint(RoutePatternFactory.Parse("/docs/{**path}"), -1, Get),
        };
        var left = new Dictionary<string, RouteEndpoint>
        {
            // MVC's conventional routes share one pattern, and each action's required values fill its parameters.
            ["a conventional route"] = Endpoint(
                RoutePatternFactory.Parse("{controller=Home}/{action=Index}/{id?}", defaults: null, parameterPolicies: null, requiredValues: new { controller = "Home", action = "Index" }),
                0, Get),
            ["a fallback"] = Endpoint(RoutePatternFactory.Parse("{**path:nonfile}"), int.MaxValue, Get),
            ["an endpoint without a method"] = Endpoint(RoutePatternFactory.Parse("/healthz")),
            ["a static asset"] = Endpoint(RoutePatternFactory.Parse("/app.css"), 0, Get, new StaticAssetDescriptor { Route = "app.css", AssetPath = "app.css" }),
            ["a page"] = Endpoint(RoutePatternFactory.Parse("/about"), 0, Get, new PageEndpointMetadata("/about")),
            ["a server action"] = Endpoint(RoutePatternFactory.Parse("/_netjes/actions/posts.like"), 0, new HttpMethodMetadata([HttpMethods.Post]), new PageEndpointMetadata(null)),
            ["one of Netjes's own"] = Endpoint(RoutePatternFactory.Parse("/_netjes/image"), 0, Get, NetjesEndpointMetadata.Instance),
        };

        Assert.Empty(kept.Where(e => !AppEndpoints.IsTheAppsOwn(e.Value)).Select(e => e.Key));
        Assert.Empty(left.Where(e => AppEndpoints.IsTheAppsOwn(e.Value)).Select(e => e.Key));
    }

    [Fact]
    public async Task IsTheAppsOwn_LeavesOutNetjesOwnEndpoints_AndKeepsTheSitemapAndRobotsTxt()
    {
        var site = new Uri("https://example.com");
        await using var app = await TestApp.StartAsync(
            ["app/[lang]/page.tsx"],
            configure: o => o.CollectWebVitals = true,
            services: s => s.AddNetjesLocalization(o => o.SupportedCultures = ["en", "nl"]),
            map: a =>
            {
                a.MapLocaleRedirect();
                a.MapSitemap(site);
                a.MapRobotsTxt(site);
                a.MapGet("/api/ping", () => "pong");
            });

        var endpoints = app.App.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        // The antiforgery token, the image optimizer, the vitals beacon and the locale redirect are mapped, and left out.
        Assert.All(
            ["/_netjes/antiforgery", "/_netjes/image", "/_netjes/vitals", "/"],
            path => Assert.Contains(endpoints, e => e.RoutePattern.RawText == path));
        Assert.Equal(
            ["/api/ping", "/robots.txt", "/sitemap.xml"],
            endpoints.Where(AppEndpoints.IsTheAppsOwn).Select(e => e.RoutePattern.RawText!).Order(StringComparer.Ordinal));
    }
}
