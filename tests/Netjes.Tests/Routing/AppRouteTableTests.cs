using Netjes.Routing;

namespace Netjes.Tests.Routing;

/// <summary>The app/ directory: Next.js App Router file conventions.</summary>
public class AppRouteTableTests
{
    [Fact]
    public void Build_SegmentFiles_FormTheSegmentChainOutermostFirst()
    {
        var table = RouteTable.Build([
            "app/layout.tsx",
            "app/blog/layout.tsx",
            "app/blog/loading.tsx",
            "app/blog/error.tsx",
            "app/blog/[slug]/page.tsx",
            "app/blog/[slug]/template.tsx",
            "app/blog/[slug]/not-found.tsx",
            "app/blog/[slug]/forbidden.tsx",
            "app/blog/[slug]/unauthorized.tsx",
        ]);

        var route = table.Find("/blog/[slug]")!;
        Assert.Equal(
            [
                new RouteSegment("app", "/", [], Layout: "app/layout.tsx"),
                new RouteSegment("app/blog", "/blog", [], Layout: "app/blog/layout.tsx", Loading: "app/blog/loading.tsx", Error: "app/blog/error.tsx"),
                new RouteSegment(
                    "app/blog/[slug]", "/blog/[slug]", ["slug"],
                    Template: "app/blog/[slug]/template.tsx",
                    NotFound: "app/blog/[slug]/not-found.tsx",
                    Forbidden: "app/blog/[slug]/forbidden.tsx",
                    Unauthorized: "app/blog/[slug]/unauthorized.tsx"),
            ],
            route.Segments);
        Assert.Equal(["app/layout.tsx", "app/blog/layout.tsx"], route.Layouts);
    }

    [Fact]
    public void Build_FoldersWithoutSegmentFiles_AreNotSegments()
    {
        var table = RouteTable.Build(["app/layout.tsx", "app/a/b/c/page.tsx"]);

        Assert.Equal(["app"], table.Find("/a/b/c")!.Segments.Select(s => s.Id));
    }

    [Fact]
    public void Build_RouteGroupSegment_KeepsItsFolderIdAndTheParentUrl()
    {
        var table = RouteTable.Build(["app/(shop)/layout.tsx", "app/(shop)/cart/page.tsx", "app/about/page.tsx"]);

        Assert.Equal([new RouteSegment("app/(shop)", "/", [], Layout: "app/(shop)/layout.tsx")], table.Find("/cart")!.Segments);
        Assert.Empty(table.Find("/about")!.Segments);
    }

    [Theory]
    [InlineData("app/blog/components/Card.tsx")]
    [InlineData("app/blog/utils.ts")]
    [InlineData("app/blog/page.test.tsx")]
    [InlineData("app/blog/page.spec.jsx")]
    [InlineData("app/_lib/page.tsx")]
    [InlineData("app/blog/_drafts/page.tsx")]
    [InlineData("app/blog/default.tsx")]
    public void Build_ColocatedAndPrivateFiles_AreNotRoutes(string file)
    {
        var table = RouteTable.Build([file]);

        Assert.Empty(table.Pages);
    }

    [Fact]
    public void Build_PrivateFolder_ContributesNoSegmentFiles()
    {
        var table = RouteTable.Build(["app/_lib/layout.tsx", "app/page.tsx"]);

        Assert.Empty(table.Find("/")!.Segments);
    }

    [Theory]
    [InlineData("app/api/route.ts")]
    [InlineData("app/api/route.tsx")]
    public void Build_RouteHandlerFile_ThrowsPointingAtMinimalApis(string file)
    {
        var ex = Assert.Throws<InvalidPageFileException>(() => RouteTable.Build([file]));

        Assert.Contains(file, ex.Message);
        Assert.Contains("minimal API", ex.Message);
    }

    // The Vite plugin stops a production build on the files refused above and on none of these (plugin.test.ts).
    [Fact]
    public void Build_RouteFilesThatAreNoRouteHandlers_AreLeftAlone()
    {
        var table = RouteTable.Build(["app/page.tsx", "app/api/route.test.ts", "app/_lib/route.ts", "app/@modal/route.ts", "app/api/Route.ts", "lib/route.ts"]);

        Assert.Equal(["/"], table.Pages.Select(p => p.Pattern));
    }

    [Fact]
    public void Build_RootNotFoundAndError_AreTheSpecialPagesWithTheRootSegment()
    {
        var table = RouteTable.Build(["app/layout.tsx", "app/not-found.tsx", "app/error.tsx", "app/global-error.tsx", "app/page.tsx"]);

        Assert.Equal("app/not-found.tsx", table.NotFound?.File);
        Assert.Equal(["app/layout.tsx"], table.NotFound?.Layouts);
        Assert.Equal("app/error.tsx", table.Error?.File);
        Assert.Equal(["app"], table.Error?.Segments.Select(s => s.Id));
        Assert.Equal("app/global-error.tsx", table.GlobalError?.File);
        Assert.Empty(table.GlobalError!.Segments);
    }

    [Fact]
    public void Build_NestedGlobalError_Throws()
    {
        var ex = Assert.Throws<InvalidPageFileException>(() => RouteTable.Build(["app/blog/global-error.tsx"]));

        Assert.Contains("app/global-error", ex.Message);
    }

    [Fact]
    public void Build_CustomAppDirectory_IsRespected()
    {
        var table = RouteTable.Build(["src/app/page.tsx", "app/ignored/page.tsx"], appDirectory: "src/app");

        Assert.Equal("src/app/page.tsx", Assert.Single(table.Pages).File);
    }
}

public class ParallelRouteTableTests
{
    private static readonly string[] Dashboard =
    [
        "app/dashboard/layout.tsx",
        "app/dashboard/page.tsx",
        "app/dashboard/settings/page.tsx",
        "app/dashboard/[project]/page.tsx",
        "app/dashboard/@team/page.tsx",
        "app/dashboard/@team/settings/page.tsx",
        "app/dashboard/@team/[project]/page.tsx",
        "app/dashboard/@team/default.tsx",
        "app/dashboard/@stats/default.tsx",
        "app/dashboard/@stats/Chart.tsx",
    ];

    [Fact]
    public void SlotFolders_AreNotRoutesOfTheirOwn()
    {
        var table = RouteTable.Build(Dashboard);

        Assert.Equal(["/dashboard", "/dashboard/[project]", "/dashboard/settings"], table.Pages.Select(p => p.Pattern));
    }

    [Fact]
    public void Slots_BelongToTheSegmentOfTheirParentFolder()
    {
        var segment = RouteTable.Build(Dashboard).Find("/dashboard/settings")!.Segments.Single();

        Assert.Equal(["stats", "team"], segment.Slots.Keys.Order());
        Assert.Equal("app/dashboard/@team/settings/page.tsx", segment.Slots["team"].Pages["/settings"]);
        Assert.Equal("app/dashboard/@team/page.tsx", segment.Slots["team"].Pages["/"]);
        Assert.Equal("app/dashboard/@team/[project]/page.tsx", segment.Slots["team"].Pages["/[project]"]);
        Assert.Equal("app/dashboard/@team/default.tsx", segment.Slots["team"].Default);
        Assert.Empty(segment.Slots["stats"].Pages);
        Assert.Equal("app/dashboard/@stats/default.tsx", segment.Slots["stats"].Default);
    }

    [Fact]
    public void SlotFor_PicksThePageMatchingThePagesPatternBelowTheSegment_ThenTheDefault()
    {
        var table = RouteTable.Build(Dashboard);
        var settings = table.Find("/dashboard/settings")!;
        var project = table.Find("/dashboard/[project]")!;
        var segment = settings.Segments.Single();

        Assert.Equal("app/dashboard/@team/settings/page.tsx", segment.SlotFor("team", settings));
        Assert.Equal("app/dashboard/@team/[project]/page.tsx", segment.SlotFor("team", project));
        Assert.Equal("app/dashboard/@stats/default.tsx", segment.SlotFor("stats", settings));
    }

    [Fact]
    public void SlotWithoutDefault_RendersNothingWhereNothingMatches()
    {
        var table = RouteTable.Build(["app/layout.tsx", "app/page.tsx", "app/about/page.tsx", "app/@modal/about/page.tsx"]);
        var root = table.Find("/")!.Segments.Single();

        Assert.Null(root.SlotFor("modal", table.Find("/")!));
        Assert.Equal("app/@modal/about/page.tsx", root.SlotFor("modal", table.Find("/about")!));
    }

    [Fact]
    public void SlotFor_MatchesParametersByPosition_NotByName()
    {
        var table = RouteTable.Build(["app/dashboard/layout.tsx", "app/dashboard/[id]/page.tsx", "app/dashboard/@team/[project]/page.tsx"]);
        var page = table.Find("/dashboard/[id]")!;

        Assert.Equal("app/dashboard/@team/[project]/page.tsx", page.Segments.Single().SlotFor("team", page));
    }

    [Fact]
    public void SlotFor_ACatchAllCoversEveryPageBelow_AndMoreSpecificSlotPagesWin()
    {
        var table = RouteTable.Build([
            "app/layout.tsx", "app/page.tsx", "app/blog/[slug]/page.tsx", "app/blog/[slug]/comments/page.tsx", "app/about/page.tsx",
            "app/@breadcrumbs/[...slug]/page.tsx", "app/@breadcrumbs/about/page.tsx", "app/@breadcrumbs/default.tsx",
        ]);
        var root = table.Find("/")!.Segments.Single();

        Assert.Equal("app/@breadcrumbs/[...slug]/page.tsx", root.SlotFor("breadcrumbs", table.Find("/blog/[slug]")!));
        Assert.Equal("app/@breadcrumbs/[...slug]/page.tsx", root.SlotFor("breadcrumbs", table.Find("/blog/[slug]/comments")!));
        Assert.Equal("app/@breadcrumbs/about/page.tsx", root.SlotFor("breadcrumbs", table.Find("/about")!));
        // A catch-all needs at least one segment.
        Assert.Equal("app/@breadcrumbs/default.tsx", root.SlotFor("breadcrumbs", table.Find("/")!));
    }

    [Fact]
    public void SlotWithoutALayout_StillMakesASegment()
    {
        var table = RouteTable.Build(["app/page.tsx", "app/@aside/default.tsx"]);

        Assert.Equal(["aside"], table.Find("/")!.Segments.Single().Slots.Keys);
    }
}

public class InterceptingRouteTableTests
{
    [Fact]
    public void InterceptingFolders_InASlot_InterceptTheUrlTheyPointAt()
    {
        var table = RouteTable.Build([
            "app/layout.tsx",
            "app/feed/page.tsx",
            "app/photo/[id]/page.tsx",
            "app/@modal/default.tsx",
            "app/@modal/(.)photo/[id]/page.tsx",
            "app/feed/@detail/(..)photo/[id]/page.tsx",
            "app/feed/@detail/default.tsx",
        ]);

        Assert.Equal(["/feed", "/photo/[id]"], table.Pages.Select(p => p.Pattern));
        Assert.Equal(
            [("app", "modal", "app/@modal/(.)photo/[id]/page.tsx"), ("app/feed", "detail", "app/feed/@detail/(..)photo/[id]/page.tsx")],
            table.InterceptsFor("/photo/[id]").Select(i => (i.Segment, i.Slot, i.Page)));
        Assert.Empty(table.InterceptsFor("/feed"));
    }

    [Fact]
    public void Intercepts_MatchTheTargetByShape_NotByParameterNames()
    {
        var table = RouteTable.Build(["app/layout.tsx", "app/page.tsx", "app/photo/[id]/page.tsx", "app/@modal/(.)photo/[photoId]/page.tsx"]);

        Assert.Equal("app/@modal/(.)photo/[photoId]/page.tsx", Assert.Single(table.InterceptsFor("/photo/[id]")).Page);
    }

    [Fact]
    public void RootLevelMarker_InterceptsFromTheRoot()
    {
        var table = RouteTable.Build(["app/shop/[id]/page.tsx", "app/shop/@quick/(...)cart/page.tsx", "app/cart/page.tsx", "app/shop/layout.tsx"]);

        Assert.Equal("app/shop/@quick/(...)cart/page.tsx", Assert.Single(table.InterceptsFor("/cart")).Page);
    }
}
