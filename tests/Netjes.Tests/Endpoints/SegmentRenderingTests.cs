using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Netjes.Tests.Endpoints;

/// <summary>Segments in the payload, and the nested not-found, error, forbidden and unauthorized boundaries.</summary>
public class SegmentRenderingTests
{
    private static readonly string[] Blog =
    [
        "app/layout.tsx",
        "app/not-found.tsx",
        "app/error.tsx",
        "app/blog/layout.tsx",
        "app/blog/loading.tsx",
        "app/blog/error.tsx",
        "app/blog/not-found.tsx",
        "app/blog/[slug]/page.tsx",
        "app/blog/[slug]/template.tsx",
        "app/blog/[slug]/forbidden.tsx",
        "app/blog/[slug]/unauthorized.tsx",
    ];

    private static IEnumerable<string?> Ids(JsonNode payload) => payload["segments"]!.AsArray().Select(s => (string?)s!["id"]);

    [Fact]
    public async Task Payload_CarriesTheSegmentFilesTheClientComposes()
    {
        await using var app = await TestApp.StartAsync(Blog);

        var payload = await app.GetDataJsonAsync("/blog/hello");

        var segments = payload["segments"]!.AsArray();
        Assert.Equal("""{"id":"app","layout":"app/layout.tsx","error":"app/error.tsx","depth":0}""", segments[0]!.ToJsonString());
        Assert.Equal(
            """{"id":"app/blog","layout":"app/blog/layout.tsx","loading":"app/blog/loading.tsx","error":"app/blog/error.tsx","depth":1}""",
            segments[1]!.ToJsonString());
        Assert.Equal("""{"id":"app/blog/[slug]","template":"app/blog/[slug]/template.tsx"}""", segments[2]!.ToJsonString());
        Assert.Null(payload["layouts"]);
        Assert.Null(payload["pageKind"]);
    }

    [Fact]
    public async Task Layouts_CarryTheUrlDepthTheyCover_CountingWhatACatchAllMatched()
    {
        await using var app = await TestApp.StartAsync(
            ["src/app/layout.tsx", "src/app/(docs)/docs/[...path]/layout.tsx", "src/app/(docs)/docs/[...path]/page.tsx"],
            configure: o => o.AppDirectory = "src/app");

        var payload = await app.GetDataJsonAsync("/docs/a/b");

        // useSelectedLayoutSegment(s) count from these: the folder id has a directory prefix and a route group.
        Assert.Equal([0, 3], payload["segments"]!.AsArray().Select(s => (int?)s!["depth"]));
    }

    [Fact]
    public async Task LoaderNotFound_RendersTheNearestNotFoundInsideTheLayoutsAboveIt()
    {
        await using var app = await TestApp.StartAsync(Blog, map: a => a.MapPageLoader("/blog/[slug]", () => Page.NotFound()));

        var response = await app.GetDataAsync("/blog/missing");
        var payload = await app.GetDataJsonAsync("/blog/missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("app/blog/not-found.tsx", (string?)payload["page"]);
        Assert.Equal("notFound", (string?)payload["pageKind"]);
        Assert.Equal(["app", "app/blog"], Ids(payload));
        Assert.Equal("/blog/[slug]", (string?)payload["route"]);
    }

    [Fact]
    public async Task LoaderNotFound_WithoutANestedOne_UsesTheRootNotFound()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/not-found.tsx", "app/blog/[slug]/page.tsx"],
            map: a => a.MapPageLoader("/blog/[slug]", () => Page.NotFound()));

        var payload = await app.GetDataJsonAsync("/blog/missing");

        Assert.Equal("app/not-found.tsx", (string?)payload["page"]);
        Assert.Equal(["app"], Ids(payload));
    }

    [Fact]
    public async Task UnknownUrl_RendersTheRootNotFound()
    {
        await using var app = await TestApp.StartAsync(Blog);

        var payload = await app.GetDataJsonAsync("/blog/a/b/c");

        Assert.Equal("app/not-found.tsx", (string?)payload["page"]);
        Assert.Equal(["app"], Ids(payload));
    }

    private const string Help = TestPagePackage.Name;

    /// <summary>
    /// The app with <see cref="TestPagePackage.Descriptor"/>'s files (and <see cref="TestPagePackage.Other"/>'s, when
    /// given) mounted by <paramref name="map"/>: installed in Development, built in Production.
    /// </summary>
    private static Task<TestApp> MountedAsync(
        string[] appFiles,
        string[] packageFiles,
        Action<WebApplication> map,
        string environment = "Development",
        string[]? otherFiles = null,
        string? pathBase = null) =>
        TestApp.StartAsync(
            environment == "Production" ? [] : appFiles,
            environment: environment,
            pathBase: pathBase,
            prepareRoot: root =>
            {
                if (environment == "Production")
                {
                    TestPagePackage.WriteBuild(
                        root, appFiles, [new(Help, packageFiles), .. otherFiles is null ? [] : new TestPagePackage.Built[] { new(TestPagePackage.Other.Name, otherFiles) }]);
                    return;
                }

                TestPagePackage.Install(root, packageFiles);
                if (otherFiles is not null)
                {
                    TestPagePackage.Install(root, otherFiles, name: TestPagePackage.Other.Name);
                }
            },
            map: map);

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task UnknownUrlUnderAMount_RendersThePackagesNotFoundInsideItsLayoutAndTheRootLayout(string environment)
    {
        await using var app = await MountedAsync(
            ["app/layout.tsx", "app/not-found.tsx", "app/page.tsx"],
            ["layout.tsx", "not-found.tsx", "page.tsx"],
            a => a.MapPagePackage(TestPagePackage.Descriptor, "/help"),
            environment);

        var response = await app.GetDataAsync("/help/nope");
        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        var document = await app.GetDocumentAsync("/help/nope/deeper");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal($"{Help}/app/not-found.tsx", (string?)payload["page"]);
        Assert.Equal("notFound", (string?)payload["pageKind"]);
        Assert.Equal(["app", "app/help"], Ids(payload));
        Assert.Equal($"{Help}/app/layout.tsx", (string?)payload["segments"]![1]!["layout"]);
        Assert.Equal(HttpStatusCode.NotFound, document.StatusCode);
        Assert.Equal($"{Help}/app/not-found.tsx", (string?)app.Renderer.LastPayload["page"]);
        Assert.Equal("app/not-found.tsx", (string?)(await app.GetDataJsonAsync("/nope"))["page"]);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task UnknownUrlUnderAMountInsideAnother_RendersTheDeepestMountsNotFound(string environment)
    {
        await using var app = await MountedAsync(
            ["app/layout.tsx", "app/not-found.tsx", "app/page.tsx"],
            ["layout.tsx", "not-found.tsx", "page.tsx"],
            a =>
            {
                a.MapPagePackage(TestPagePackage.Other, "/a");
                a.MapPagePackage(TestPagePackage.Descriptor, "/a/(g)/b");
            },
            environment,
            otherFiles: ["layout.tsx", "not-found.tsx", "page.tsx"]);

        var inner = await app.GetDataJsonAsync("/a/b/nope");
        var outer = await app.GetDataJsonAsync("/a/nope");

        Assert.Equal($"{Help}/app/not-found.tsx", (string?)inner["page"]);
        Assert.Equal(["app", "app/a", "app/a/(g)/b"], Ids(inner));
        Assert.Equal($"{TestPagePackage.Other.Name}/app/not-found.tsx", (string?)outer["page"]);
        Assert.Equal(["app", "app/a"], Ids(outer));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task UnknownUrlUnderAMountWithAValue_BindsThePrefixsValueInTheLayoutLoaders(string environment)
    {
        await using var app = await MountedAsync(
            ["app/layout.tsx", "app/not-found.tsx", "app/page.tsx"],
            ["layout.tsx", "not-found.tsx", "page.tsx"],
            a => a.MapPagePackage(TestPagePackage.Descriptor, "/[tenant]/help")
                .MapLayoutLoader("/", (string tenant) => Page.Props(new { Tenant = tenant })),
            environment);

        var payload = await app.GetDataJsonAsync("/acme/help/nope");

        Assert.Equal($"{Help}/app/not-found.tsx", (string?)payload["page"]);
        Assert.Equal(["app", "app/[tenant]/help"], Ids(payload));
        Assert.Equal("acme", (string?)payload["segments"]![1]!["props"]!["tenant"]);
        Assert.Equal("acme", (string?)payload["params"]!["tenant"]);
        Assert.Equal("/acme/help", (string?)payload["mounts"]![Help]);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task AnAppCatchAllsNotFound_UnderAMount_RendersThePackagesNotFound(string environment)
    {
        await using var app = await MountedAsync(
            ["app/layout.tsx", "app/not-found.tsx", "app/(site)/layout.tsx", "app/(site)/[[...slug]]/page.tsx", "app/help/(center)/not-found.tsx", "app/help/(center)/shortcuts/page.tsx"],
            ["layout.tsx", "not-found.tsx", "page.tsx"],
            a =>
            {
                a.MapPageLoader("/[[...slug]]", () => Page.NotFound());
                a.MapPagePackage(TestPagePackage.Descriptor, "/help");
                a.MapPageLoader("/help/shortcuts", () => Page.NotFound());
            },
            environment);

        var response = await app.GetDataAsync("/help/nope");
        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        var elsewhere = await app.GetDataJsonAsync("/elsewhere");
        // A page inside the mount renders its own nearest not-found, as every page does.
        var inside = await app.GetDataJsonAsync("/help/shortcuts");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal($"{Help}/app/not-found.tsx", (string?)payload["page"]);
        Assert.Equal(["app", "app/help"], Ids(payload));
        Assert.Equal("app/not-found.tsx", (string?)elsewhere["page"]);
        Assert.Equal(["app"], Ids(elsewhere));
        Assert.Equal("app/help/(center)/not-found.tsx", (string?)inside["page"]);
        Assert.Equal(["app", "app/help", "app/help/(center)"], Ids(inside));
    }

    [Theory]
    [InlineData("at the mount's URL")]
    [InlineData("below the mount's URL")]
    public async Task AnAppPageOutsideTheMount_KeepsANotFoundOfItsOwn_AtOrBelowTheMountsUrl(string where)
    {
        var atTheUrl = where == "at the mount's URL";
        await using var app = await MountedAsync(
            atTheUrl
                ? ["app/layout.tsx", "app/not-found.tsx", "app/(x)/layout.tsx", "app/(x)/help/not-found.tsx", "app/(x)/help/faq/page.tsx"]
                : ["app/layout.tsx", "app/not-found.tsx", "app/blog/not-found.tsx", "app/blog/[slug]/page.tsx"],
            ["layout.tsx", "not-found.tsx", "page.tsx"],
            a =>
            {
                if (atTheUrl)
                {
                    a.MapPagePackage(TestPagePackage.Descriptor, "/help");
                    a.MapPageLoader("/help/faq", () => Page.NotFound());
                }
                else
                {
                    a.MapPagePackage(TestPagePackage.Other, "/(site)");
                    a.MapPageLoader("/blog/[slug]", () => Page.NotFound());
                }
            },
            otherFiles: atTheUrl ? null : ["layout.tsx", "not-found.tsx", "about/page.tsx"]);

        var response = await app.GetDataAsync(atTheUrl ? "/help/faq" : "/blog/missing");
        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(atTheUrl ? "app/(x)/help/not-found.tsx" : "app/blog/not-found.tsx", (string?)payload["page"]);
        Assert.Equal(atTheUrl ? ["app", "app/(x)", "app/(x)/help"] : ["app", "app/blog"], Ids(payload));
    }

    [Fact]
    public async Task UnknownUrl_BelowNoOtherMount_RendersTheRootNotFound_NotTheOneOfAMountAtTheRootUrl()
    {
        await using var app = await MountedAsync(
            ["app/layout.tsx", "app/not-found.tsx", "app/(shop)/layout.tsx", "app/(shop)/cart/page.tsx"],
            ["layout.tsx", "not-found.tsx", "page.tsx"],
            a =>
            {
                a.MapPagePackage(TestPagePackage.Other, "/(site)").MapPageLoader("/about", () => Page.NotFound());
                a.MapPagePackage(TestPagePackage.Descriptor, "/help");
            },
            otherFiles: ["layout.tsx", "not-found.tsx", "about/page.tsx"]);

        var anywhere = await app.GetDataJsonAsync("/nope");
        var help = await app.GetDataJsonAsync("/help/nope");
        // The package's own page still renders the package's not-found, its nearest.
        var about = await app.GetDataJsonAsync("/about");

        Assert.Equal("app/not-found.tsx", (string?)anywhere["page"]);
        Assert.Equal(["app"], Ids(anywhere));
        Assert.Equal($"{Help}/app/not-found.tsx", (string?)help["page"]);
        Assert.Equal(["app", "app/help"], Ids(help));
        Assert.Equal($"{TestPagePackage.Other.Name}/app/not-found.tsx", (string?)about["page"]);
        Assert.Equal(["app", "app/(site)"], Ids(about));
    }

    [Theory]
    [InlineData("/HELP/nope", true)]
    [InlineData("/Help/nope/", true)]
    [InlineData("/help", true)]
    [InlineData("/helpx/nope", false)]
    [InlineData("/help%2Fnope", false)]
    [InlineData("/help%2fnope", false)]
    [InlineData("//help/nope", false)]
    [InlineData("/x/help/nope", false)]
    public async Task UnknownUrl_IsBelowTheMountOnlyWhenItsSegmentsAreTheMounts_InAnyCase(string url, bool below)
    {
        foreach (var pathBase in new string?[] { null, "/base" })
        {
            await using var app = await MountedAsync(
                ["app/layout.tsx", "app/not-found.tsx", "app/page.tsx"],
                ["layout.tsx", "not-found.tsx", "contact/page.tsx"],
                a => a.MapPagePackage(TestPagePackage.Descriptor, "/help"),
                pathBase: pathBase);

            var response = await app.GetDataAsync(pathBase + url);
            var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(below ? $"{Help}/app/not-found.tsx" : "app/not-found.tsx", (string?)payload["page"]);
            Assert.Equal(below ? ["app", "app/help"] : ["app"], Ids(payload));
        }
    }

    [Theory]
    [InlineData("throws", 500, "app/members/error.tsx")]
    [InlineData("not found", 404, "app/members/not-found.tsx")]
    [InlineData("forbidden", 403, "app/members/forbidden.tsx")]
    public async Task ALayoutLoaderOnTheMountsChain_ThatStops_RendersTheBoundaryAboveItOnTheChain(string stops, int status, string boundary)
    {
        await using var app = await MountedAsync(
            ["app/layout.tsx", "app/not-found.tsx", "app/error.tsx", "app/members/layout.tsx", "app/members/not-found.tsx", "app/members/error.tsx", "app/members/forbidden.tsx", "app/members/page.tsx"],
            ["layout.tsx", "not-found.tsx", "error.tsx", "page.tsx"],
            a => a.MapPagePackage(TestPagePackage.Descriptor, "/members/help").MapLayoutLoader("/", PageResult () => stops switch
            {
                "throws" => throw new InvalidOperationException("down"),
                "not found" => Page.NotFound(),
                _ => Page.Forbidden(),
            }));

        var response = await app.GetDataAsync("/members/help/nope");
        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(boundary, (string?)payload["page"]);
        Assert.Equal(["app", "app/members"], Ids(payload));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownUrls_HaveOneFallbackEndpoint_LastInOrder_WithMountsOrWithout(bool mounted)
    {
        await using var app = await MountedAsync(
            ["app/layout.tsx", "app/not-found.tsx", "app/page.tsx"],
            ["layout.tsx", "not-found.tsx", "page.tsx"],
            a =>
            {
                if (mounted)
                {
                    a.MapPagePackage(TestPagePackage.Descriptor, "/help");
                    a.MapPagePackage(TestPagePackage.Other, "/[tenant]/status");
                }
            },
            otherFiles: ["not-found.tsx", "page.tsx"]);

        var endpoints = app.App.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<Netjes.Routing.PageEndpointMetadata>() is { Pattern: null })
            .ToList();

        var fallback = Assert.Single(endpoints);
        Assert.Equal(int.MaxValue, fallback.Order);
        Assert.Equal("{**path:nonfile}", fallback.RoutePattern.RawText);
    }

    [Fact]
    public async Task LoaderException_RendersTheNearestErrorBoundaryOutsideItsOwnSegment()
    {
        await using var app = await TestApp.StartAsync(
            Blog, map: a => a.MapPageLoader("/blog/[slug]", Func<PageResult> () => throw new InvalidOperationException("db down")));

        var response = await app.GetDataAsync("/blog/hello");
        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("app/blog/error.tsx", (string?)payload["page"]);
        Assert.Equal("error", (string?)payload["pageKind"]);
        Assert.Equal(["app", "app/blog"], Ids(payload));
        // The boundary renders in place of its segment's children, so it is not wrapped in itself.
        Assert.Null(payload["segments"]![1]!["error"]);
        Assert.Equal("db down", (string?)payload["props"]!["message"]);
    }

    [Theory]
    [InlineData("forbidden", 403)]
    [InlineData("unauthorized", 401)]
    public async Task ForbiddenAndUnauthorized_RenderTheNearestBoundary(string kind, int status)
    {
        await using var app = await TestApp.StartAsync(
            Blog, map: a => a.MapPageLoader("/blog/[slug]", () => kind == "forbidden" ? Page.Forbidden() : Page.Unauthorized()));

        var response = await app.GetDataAsync("/blog/secret");
        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal($"app/blog/[slug]/{kind}.tsx", (string?)payload["page"]);
        Assert.Equal(kind, (string?)payload["pageKind"]);
        Assert.Equal(["app", "app/blog", "app/blog/[slug]"], Ids(payload));
    }

    [Fact]
    public async Task EmptyForbiddenResponse_FromTheEndpoint_RendersTheNearestForbiddenBoundary()
    {
        await using var app = await TestApp.StartAsync(
            Blog, map: a => a.MapPageLoader("/blog/[slug]", () => TypedResults.StatusCode(StatusCodes.Status403Forbidden)));

        var response = await app.GetDocumentAsync("/blog/secret");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("app/blog/[slug]/forbidden.tsx", (string?)app.Renderer.LastPayload["page"]);
    }

    [Fact]
    public async Task Forbidden_WithoutABoundary_RendersTheErrorPageWith403()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/error.tsx", "app/admin/page.tsx"], map: a => a.MapPageLoader("/admin", () => Page.Forbidden()));

        var response = await app.GetDataAsync("/admin");
        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("app/error.tsx", (string?)payload["page"]);
        Assert.Equal(403, (int?)payload["props"]!["status"]);
    }

    [Fact]
    public async Task ErrorPageThatFailsToRender_FallsBackToGlobalError()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/error.tsx", "app/global-error.tsx", "app/page.tsx"],
            map: a => a.MapPageLoader("/", Func<PageResult> () => throw new InvalidOperationException("boom")));
        app.Renderer.OnRender = payload => payload.Contains("\"app/error.tsx\"")
            ? throw new SsrRenderException("the error page threw too", null)
            : "<!DOCTYPE html><html><body>global</body></html>";

        var response = await app.GetDocumentAsync("/");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("app/global-error.tsx", (string?)app.Renderer.LastPayload["page"]);
        Assert.Equal("[]", app.Renderer.LastPayload["segments"]!.ToJsonString());
    }
}

public class ParallelRouteRenderingTests
{
    private static readonly string[] Dashboard =
    [
        "app/dashboard/layout.tsx",
        "app/dashboard/page.tsx",
        "app/dashboard/settings/page.tsx",
        "app/dashboard/@team/settings/page.tsx",
        "app/dashboard/@team/default.tsx",
        "app/dashboard/@stats/page.tsx",
    ];

    [Fact]
    public async Task Payload_NamesTheFileEachSlotRendersForThePage()
    {
        await using var app = await TestApp.StartAsync(Dashboard);

        var settings = await app.GetDataJsonAsync("/dashboard/settings");
        var overview = await app.GetDataJsonAsync("/dashboard");

        Assert.Equal(
            """{"stats":null,"team":"app/dashboard/@team/settings/page.tsx"}""",
            settings["segments"]![0]!["slots"]!.ToJsonString());
        Assert.Equal(
            """{"stats":"app/dashboard/@stats/page.tsx","team":"app/dashboard/@team/default.tsx"}""",
            overview["segments"]![0]!["slots"]!.ToJsonString());
    }

    [Fact]
    public async Task SegmentsWithoutSlots_HaveNoSlotsField()
    {
        await using var app = await TestApp.StartAsync(["app/layout.tsx", "app/page.tsx"]);

        Assert.Null((await app.GetDataJsonAsync("/"))["segments"]![0]!["slots"]);
    }
}

public class InterceptingRouteRenderingTests
{
    private static readonly string[] Photos =
    [
        "app/layout.tsx",
        "app/feed/page.tsx",
        "app/photo/[id]/page.tsx",
        "app/@modal/default.tsx",
        "app/@modal/(.)photo/[id]/page.tsx",
    ];

    [Fact]
    public async Task DataPayload_ListsTheSlotsThatCanInterceptThePage()
    {
        await using var app = await TestApp.StartAsync(Photos);

        var payload = await app.GetDataJsonAsync("/photo/7");

        Assert.Equal("app/photo/[id]/page.tsx", (string?)payload["page"]);
        Assert.Equal("""[{"segment":"app","slot":"modal","page":"app/@modal/(.)photo/[id]/page.tsx","params":{"id":"7"}}]""", payload["intercepts"]!.ToJsonString());
    }

    [Fact]
    public async Task Intercepts_GetTheirOwnParams_ByPosition()
    {
        await using var app = await TestApp.StartAsync(["app/layout.tsx", "app/page.tsx", "app/photo/[id]/page.tsx", "app/@modal/(.)photo/[photoId]/page.tsx"]);

        var payload = await app.GetDataJsonAsync("/photo/7");

        Assert.Equal("""{"photoId":"7"}""", payload["intercepts"]![0]!["params"]!.ToJsonString());
    }

    [Theory]
    [InlineData("/photo/a%2541")]
    [InlineData("/photo/a%252Fb")]
    public async Task InterceptParams_AreDecodedOnce_LikeThePagesOwn(string url)
    {
        await using var app = await TestApp.StartAsync(["app/layout.tsx", "app/page.tsx", "app/photo/[id]/page.tsx", "app/@modal/(.)photo/[photoId]/page.tsx"]);

        var payload = await app.GetDataJsonAsync(url);

        Assert.Equal((string?)payload["params"]!["id"], (string?)payload["intercepts"]![0]!["params"]!["photoId"]);
    }

    [Fact]
    public async Task SlotPages_GetTheirOwnParams_ACatchAllEveryUrlSegmentBelowTheLayout()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx", "app/blog/[slug]/comments/page.tsx", "app/@breadcrumbs/[...slug]/page.tsx", "app/@breadcrumbs/default.tsx"]);

        var comments = await app.GetDataJsonAsync("/blog/hello%20world/comments");
        var home = await app.GetDataJsonAsync("/");

        Assert.Equal("""{"breadcrumbs":{"slug":["blog","hello world","comments"]}}""", comments["segments"]![0]!["slotParams"]!.ToJsonString());
        Assert.Null(home["segments"]![0]!["slotParams"]);
    }

    [Fact]
    public async Task Documents_RenderTheRealPage_WithoutIntercepts()
    {
        await using var app = await TestApp.StartAsync(Photos);

        await app.GetDocumentAsync("/photo/7");

        Assert.Equal("app/photo/[id]/page.tsx", (string?)app.Renderer.LastPayload["page"]);
        Assert.Null(app.Renderer.LastPayload["intercepts"]);
    }
}
