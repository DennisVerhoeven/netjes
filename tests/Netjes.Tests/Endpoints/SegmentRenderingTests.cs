using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

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
