using Netjes.Routing;

namespace Netjes.Tests.Routing;

/// <summary>
/// The route table built from several sources: the app's own folder and page packages mounted at a prefix, merged by
/// folder.
/// </summary>
public class RouteTableSourcesTests
{
    private const string Help = "@acme/help-center";

    private static PageSource App(params string[] files) => PageSource.App(files);

    private static PageSource Package(string prefix, params string[] files) => NamedPackage(Help, prefix, files);

    private static PageSource NamedPackage(string name, string prefix, params string[] files) =>
        PageSource.Package(name, "app", MountPrefix.Parse(prefix), files.Select(f => $"{name}/app/{f}"));

    private static RouteTable Build(params PageSource[] sources) => RouteTable.Build(sources, []);

    [Fact]
    public void APackagesLayoutAtItsMount_WrapsItsPagesAndTheAppsPagesInThatFolder()
    {
        var table = Build(
            App("app/layout.tsx", "app/help/shortcuts/page.tsx", "app/about/page.tsx"),
            Package("/help", "layout.tsx", "page.tsx", "contact/page.tsx"));

        string[] layouts = ["app/layout.tsx", $"{Help}/app/layout.tsx"];
        Assert.Equal(layouts, table.Find("/help")!.Layouts);
        Assert.Equal(layouts, table.Find("/help/contact")!.Layouts);
        Assert.Equal(layouts, table.Find("/help/shortcuts")!.Layouts);
        Assert.Equal(["app/layout.tsx"], table.Find("/about")!.Layouts);
    }

    [Fact]
    public void APackagesGroupLayout_WrapsAnAppPageInThatGroupOnly()
    {
        var table = Build(
            App("app/help/(center)/shortcuts/page.tsx", "app/help/about/page.tsx"),
            Package("/help", "(center)/layout.tsx", "(center)/articles/[slug]/page.tsx"));

        Assert.Equal([$"{Help}/app/(center)/layout.tsx"], table.Find("/help/shortcuts")!.Layouts);
        Assert.Equal([$"{Help}/app/(center)/layout.tsx"], table.Find("/help/articles/[slug]")!.Layouts);
        Assert.Empty(table.Find("/help/about")!.Layouts);
    }

    [Fact]
    public void SegmentIds_AreTheMergedFolders_AndFileIdsKeepTheirSource()
    {
        var table = Build(
            App("app/layout.tsx", "app/help/(center)/shortcuts/page.tsx"),
            Package("/help", "(center)/layout.tsx", "(center)/not-found.tsx", "(center)/articles/[slug]/page.tsx"));

        var shortcuts = table.Find("/help/shortcuts")!;
        Assert.Equal(["app", "app/help/(center)"], shortcuts.Segments.Select(s => s.Id));
        Assert.Equal("/help", shortcuts.Segments[1].Pattern);
        Assert.Equal($"{Help}/app/(center)/not-found.tsx", shortcuts.Segments[1].NotFound);
        Assert.Equal("app/help/(center)/shortcuts/page.tsx", shortcuts.File);
        Assert.Equal("app", shortcuts.Source);

        var article = table.Find("/help/articles/[slug]")!;
        Assert.Equal($"{Help}/app/(center)/articles/[slug]/page.tsx", article.File);
        Assert.Equal(Help, article.Source);
        Assert.Equal(shortcuts.Segments, article.Segments);
    }

    [Theory]
    [InlineData("/help", "/help/articles/[slug]", "/help/articles/{slug}", "app/help", new[] { "slug" }, new string[0])]
    [InlineData("/(marketing)/help", "/help/articles/[slug]", "/help/articles/{slug}", "app/(marketing)/help", new[] { "slug" }, new string[0])]
    [InlineData("/support/(desk)/help", "/support/help/articles/[slug]", "/support/help/articles/{slug}", "app/support/(desk)/help", new[] { "slug" }, new string[0])]
    [InlineData("/[tenant]/help", "/[tenant]/help/articles/[slug]", "/{tenant}/help/articles/{slug}", "app/[tenant]/help", new[] { "tenant", "slug" }, new[] { "tenant" })]
    [InlineData("/", "/articles/[slug]", "/articles/{slug}", "app", new[] { "slug" }, new string[0])]
    public void ThePrefix_IsPartOfThePagesPatternTemplateParametersAndSegments(
        string prefix, string pattern, string template, string segment, string[] parameters, string[] segmentParameters)
    {
        var table = Build(App(), Package(prefix, "layout.tsx", "articles/[slug]/page.tsx"));

        var route = Assert.Single(table.Pages);
        Assert.Equal(pattern, route.Pattern);
        Assert.Equal(template, route.Template);
        Assert.Equal(parameters, route.Parameters.Select(p => p.Name));
        var layout = Assert.Single(route.Segments);
        Assert.Equal(segment, layout.Id);
        Assert.Equal(segmentParameters, layout.Parameters);
    }

    [Fact]
    public void AnAppWithACustomDirectory_MergesPackagesUnderIt()
    {
        var table = RouteTable.Build([PageSource.App(["src/app/layout.tsx"], "src/app"), Package("/help", "layout.tsx", "page.tsx")], []);

        Assert.Equal(["src/app", "src/app/help"], table.Find("/help")!.Segments.Select(s => s.Id));
    }

    [Fact]
    public void WithoutAppFiles_ThePackagesAreStillRouted()
    {
        var table = Build(App(), Package("/help", "page.tsx"));

        Assert.Equal(["/help"], table.Pages.Select(p => p.Pattern));
    }

    [Fact]
    public void TheSingleSourceOverload_GivesTheSameTableAsTheAppSource()
    {
        string[] files =
        [
            "app/layout.tsx", "app/not-found.tsx", "app/error.tsx", "app/global-error.tsx", "app/page.tsx",
            "app/(shop)/layout.tsx", "app/(shop)/cart/page.tsx", "app/blog/[slug]/page.tsx", "app/blog/loading.tsx",
            "app/@modal/default.tsx", "app/@modal/(.)photo/[id]/page.tsx", "app/photo/[id]/page.tsx", "app/blog/Card.tsx",
        ];

        Assert.Equal(Describe(RouteTable.Build(files)), Describe(Build(App(files))));
    }

    private static string Describe(RouteTable table) => string.Join("\n", [
        .. table.Pages.Select(p =>
            $"{p.Pattern} {p.Template} {p.File} {p.Source} [{string.Join(",", p.Parameters)}] " +
            $"[{string.Join(",", p.Segments.Select(Describe))}]"),
        $"not-found {table.NotFound?.File} [{string.Join(",", table.NotFound?.Segments.Select(Describe) ?? [])}]",
        $"error {table.Error?.File} [{string.Join(",", table.Error?.Segments.Select(Describe) ?? [])}]",
        $"global-error {table.GlobalError?.File}",
        $"root [{string.Join(",", table.RootSegments.Select(Describe))}]",
        .. table.Intercepts.Select(i => $"intercept {i}"),
    ]);

    private static string Describe(RouteSegment s) =>
        $"{s.Id} {s.Pattern} ({string.Join(",", s.Parameters)}) {s.Layout} {s.Template} {s.Loading} {s.Error} {s.NotFound} " +
        $"{s.Forbidden} {s.Unauthorized} {{{string.Join(";", s.Slots.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}={x.Value.Default}:{string.Join("|", x.Value.Pages.OrderBy(y => y.Key, StringComparer.Ordinal))}"))}}}";

    [Theory]
    [InlineData("/help", "help", "/help", "/help", new string[0])]
    [InlineData("/(marketing)/help", "(marketing)/help", "/help", "/help", new string[0])]
    [InlineData("/support/(desk)/help", "support/(desk)/help", "/support/help", "/support/help", new string[0])]
    [InlineData("/[tenant]/help", "[tenant]/help", "/[tenant]/help", "/{tenant}/help", new[] { "tenant" })]
    [InlineData("/", "", "/", "/", new string[0])]
    public void MountPrefix_GivesItsFolderPatternTemplateAndParameters(
        string prefix, string folder, string pattern, string template, string[] parameters)
    {
        var mount = MountPrefix.Parse(prefix);

        Assert.Equal(folder, mount.Folder);
        Assert.Equal(pattern, mount.Pattern);
        Assert.Equal(template, mount.Template);
        Assert.Equal(parameters, mount.Parameters);
    }

    // A prefix is a folder path that every page of the package goes under: anything that would match more than one
    // folder, hide the package, or climb out of the app directory is refused.
    [Theory]
    [InlineData("/help/[...x]", "catch-all")]
    [InlineData("/help/[[...x]]", "catch-all")]
    [InlineData("/[...x]/help", "catch-all")]
    [InlineData("/@x/help", "slot")]
    [InlineData("/_x/help", "private")]
    [InlineData("/(.)x", "intercepting")]
    [InlineData("/(..)x", "intercepting")]
    [InlineData("/(...)x", "intercepting")]
    [InlineData("/help/.", "'.'")]
    [InlineData("/./help", "'.'")]
    [InlineData("/help/..", "'..'")]
    [InlineData("/../help", "'..'")]
    [InlineData("/%2e%2e/help", "'%'")]
    [InlineData("/help%2Fx", "'%'")]
    [InlineData("/help\\x", "'\\'")]
    [InlineData("\\help", "'\\'")]
    [InlineData("/help//x", "empty segment")]
    [InlineData("//help", "empty segment")]
    [InlineData("/help/", "empty segment")]
    [InlineData("", "starts with '/'")]
    [InlineData("help", "starts with '/'")]
    [InlineData("/[a]/[A]", "more than once")]
    [InlineData("/[foo-bar]", "parameter name")]
    [InlineData("/[]", "parameter name")]
    [InlineData("/()", "not allowed")]
    [InlineData("/{tenant}/help", "not allowed")]
    [InlineData("/help?x", "not allowed")]
    [InlineData("/help#x", "not allowed")]
    [InlineData("/help*", "not allowed")]
    [InlineData("/he[l]p", "not allowed")]
    [InlineData("/he lp", "not allowed")]
    [InlineData("/help:x", "not allowed")]
    public void MountPrefix_Refuses_WithItsReason(string prefix, string reason)
    {
        var ex = Assert.Throws<ArgumentException>(() => MountPrefix.Parse(prefix));

        Assert.Contains($"'{prefix}'", ex.Message);
        Assert.Contains(reason, ex.Message);
        Assert.Equal("prefix", ex.ParamName);
    }
}
