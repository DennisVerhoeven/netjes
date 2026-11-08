using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Netjes.Routing;

namespace Netjes.Tests.Endpoints;

/// <summary>
/// Loaders, page actions, layout loaders, server actions and endpoints mapped through a page package's mount, which map
/// below its URL and only for the package's own pages, and the app's page that replaces one with <c>.Override()</c>.
/// </summary>
public class PagePackageLoaderTests
{
    private const string Help = TestPagePackage.Name;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The app with the package mounted at <paramref name="prefix"/>: installed in Development, built in Production.</summary>
    private static Task<TestApp> StartAsync(
        string[] appFiles,
        string[] packageFiles,
        Action<PagePackageBuilder>? mount = null,
        string prefix = "/help",
        string environment = "Development",
        Action<WebApplication>? before = null,
        Action<WebApplication>? after = null,
        Action<IServiceCollection>? services = null,
        Action<WebApplication>? configureApp = null,
        Action<NetjesOptions>? configure = null,
        Action<WebApplication>? beforeNetjes = null) =>
        TestApp.StartAsync(
            environment == "Production" ? [] : appFiles,
            environment: environment,
            prepareRoot: root =>
            {
                if (environment == "Production")
                {
                    TestPagePackage.WriteBuild(root, appFiles, [new(Help, packageFiles)]);
                }
                else
                {
                    TestPagePackage.Install(root, packageFiles);
                }
            },
            map: a =>
            {
                before?.Invoke(a);
                var mounted = a.MapPagePackage(TestPagePackage.Descriptor, prefix);
                mount?.Invoke(mounted);
                after?.Invoke(a);
            },
            services: services,
            configureApp: configureApp,
            configure: configure,
            beforeNetjes: beforeNetjes);

    private static IEnumerable<RouteEndpoint> Endpoints(TestApp app) =>
        app.App.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task ALoaderThroughTheMount_ServesThePackagesPageBelowTheMount(string environment)
    {
        await using var app = await StartAsync(
            ["app/page.tsx"],
            ["page.tsx", "articles/[slug]/page.tsx"],
            environment: environment,
            mount: m =>
            {
                m.MapPageLoader("/articles/[slug]", (string slug) => Page.Props(new { Title = $"About {slug}" }));
                m.MapPageLoader("/", () => Page.Props(new { Title = "Help" }));
            });

        var article = await app.GetDataJsonAsync("/help/articles/x");
        var root = await app.GetDataJsonAsync("/help");

        Assert.Equal($"{Help}/app/articles/[slug]/page.tsx", (string?)article["page"]);
        Assert.Equal("About x", (string?)article["props"]!["title"]);
        Assert.Equal($"{Help}/app/page.tsx", (string?)root["page"]);
        Assert.Equal("Help", (string?)root["props"]!["title"]);
        var loader = Endpoints(app).Single(e => e.Metadata.GetMetadata<PageRouteMetadata>() is { IsLoader: true, Pattern: "/help/articles/[slug]" });
        Assert.Equal("/help/articles/{slug}", "/" + loader.RoutePattern.RawText!.TrimStart('/'));
        Assert.Equal(HttpStatusCode.NotFound, (await app.GetDataAsync("/help/help/articles/x")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.GetDataAsync("/articles/x")).StatusCode);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task ALoaderThroughAMountWithAValue_BindsThePrefixsValueAndThePagesOwn(string environment)
    {
        await using var app = await StartAsync(
            ["app/page.tsx"],
            ["articles/[slug]/page.tsx", "articles/[slug]/feedback/page.tsx"],
            prefix: "/[tenant]/help",
            environment: environment,
            mount: m =>
            {
                m.MapPageLoader("/articles/[slug]", (string tenant, string slug) => Page.Props(new { Title = $"{tenant}: {slug}" }));
                m.MapPageLoader("/articles/[slug]/feedback", ([FromRoute(Name = "tenant")] string site, [FromRoute] string slug) =>
                    Page.Props(new { Title = $"{site} on {slug}" }));
            });

        Assert.Equal("acme: x", (string?)(await app.GetDataJsonAsync("/acme/help/articles/x"))["props"]!["title"]);
        Assert.Equal("acme on x", (string?)(await app.GetDataJsonAsync("/acme/help/articles/x/feedback"))["props"]!["title"]);
        Assert.Contains(Endpoints(app), e => e.Metadata.GetMetadata<PageRouteMetadata>()?.Pattern == "/[tenant]/help/articles/[slug]");
    }

    [Fact]
    public async Task ALoaderThroughTheMount_BindingARouteValueOutsideTheMergedTemplate_FailsAtStartup()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => StartAsync(
            ["app/page.tsx"],
            ["articles/[slug]/page.tsx"],
            prefix: "/[tenant]/help",
            mount: m => m.MapPageLoader("/articles/[slug]", ([FromRoute] string id) => Page.Props())));

        Assert.Contains($"{Help}/app/articles/[slug]/page.tsx", ex.Message);
        Assert.Contains("'id'", ex.Message);
    }

    private static readonly string[] AppPages = ["app/page.tsx", "app/help/(center)/shortcuts/page.tsx", "app/help/faq/layout.tsx", "app/help/faq/page.tsx"];

    private static readonly string[] PackagePages = ["layout.tsx", "page.tsx", "contact/page.tsx", "(center)/layout.tsx", "(center)/articles/[slug]/page.tsx"];

    public static TheoryData<string, Action<PagePackageBuilder>, string[]> ForTheApp => new()
    {
        {
            "a loader on the mount", m => m.MapPageLoader("/shortcuts", () => Page.Props()),
            ["on the mount of '@acme/help-center' at /help", "'app/help/(center)/shortcuts/page.tsx'", "app.MapPageLoader(\"/help/shortcuts\")"]
        },
        {
            "a page action on the mount", m => m.MapPageAction("/faq", () => Page.Redirect("/")),
            ["on the mount of '@acme/help-center' at /help", "'app/help/faq/page.tsx'", "app.MapPageAction(\"/help/faq\")"]
        },
        {
            "a layout loader on the mount, by folder", m => m.MapLayoutLoader("app/faq", () => Page.Props()),
            ["on the mount of '@acme/help-center' at /help", "'app/help/faq/layout.tsx'", "app.MapLayoutLoader"]
        },
        {
            "a layout loader on the mount, by URL", m => m.MapLayoutLoader("/faq", () => Page.Props()),
            ["on the mount of '@acme/help-center' at /help", "'app/help/faq/layout.tsx'", "app.MapLayoutLoader"]
        },
    };

    [Theory]
    [MemberData(nameof(ForTheApp))]
    public async Task TheMount_RefusesTheAppsPagesAndLayouts_NamingTheMount(string what, Action<PagePackageBuilder> map, string[] message)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(AppPages, PackagePages, mount: map));

        Assert.All(message, part => Assert.Contains(part, ex.Message));
        Assert.NotNull(what);
    }

    public static TheoryData<string, Action<WebApplication>, string[]> ForThePackage => new()
    {
        {
            "a loader", a => a.MapPageLoader("/help/contact", () => Page.Props()),
            ["'@acme/help-center/app/contact/page.tsx'", "the page package '@acme/help-center' mounted at /help", ".Override(\"/help/contact\")", "app/help/contact/page.tsx"]
        },
        {
            "a page action", a => a.MapPageAction("/help/articles/[slug]", () => Page.Redirect("/")),
            ["'@acme/help-center/app/(center)/articles/[slug]/page.tsx'", "the page package '@acme/help-center' mounted at /help", "app/help/(center)/articles/[slug]/page.tsx"]
        },
        {
            "a layout loader, by URL", a => a.MapLayoutLoader("/help", () => Page.Props()),
            ["'@acme/help-center/app/layout.tsx'", "the page package '@acme/help-center' mounted at /help", "through its mount"]
        },
        {
            "a layout loader, by folder", a => a.MapLayoutLoader("app/help/(center)", () => Page.Props()),
            ["'@acme/help-center/app/(center)/layout.tsx'", "the page package '@acme/help-center' mounted at /help", "through its mount"]
        },
    };

    [Theory]
    [MemberData(nameof(ForThePackage))]
    public async Task TheApp_IsRefusedThePackagesPagesAndLayouts_NamingTheMount(string what, Action<WebApplication> map, string[] message)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(AppPages, PackagePages, after: map));

        Assert.All(message, part => Assert.Contains(part, ex.Message));
        Assert.NotNull(what);
    }
}
