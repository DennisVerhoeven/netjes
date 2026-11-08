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

    // Held rather than thrown: the mount's .Override(), which runs after the table is built, can still resolve it, and
    // MapPages() throws whatever is left.
    [Theory]
    [InlineData("app/help/contact/page.tsx", "contact/page.tsx")]
    [InlineData("app/help/contact/page.tsx", "(center)/contact/page.tsx")]
    [InlineData("app/help/(x)/contact/page.tsx", "contact/page.tsx")]
    public void AnAppPageAndAPackagePageOfOneShape_AreAPendingConflict_AndThePackagesPageStays(string appFile, string packageFile)
    {
        var table = Build(App(appFile), Package("/help", packageFile));

        var conflict = Assert.Single(table.PendingConflicts);
        Assert.Equal("/help/contact", conflict.Pattern);
        Assert.Equal(appFile, conflict.AppFile);
        Assert.Equal($"{Help}/app/{packageFile}", conflict.PackageFile);
        Assert.Equal(
            $"'{Help}/app/{packageFile}' and '{appFile}' both match the URL '/help/contact'. Rename or remove the app's " +
            "page, or replace the package's page with .Override(\"/help/contact\") on its mount.",
            conflict.Message);
        Assert.Equal($"{Help}/app/{packageFile}", Assert.Single(table.Pages).File);
        Assert.Same(conflict, table.PendingConflictFor("/HELP/Contact"));
        Assert.Null(table.PendingConflictFor("/help"));
    }

    [Fact]
    public void PendingConflicts_ArePerShape_InOrderOfTheirUrl()
    {
        var table = Build(
            App("app/help/page.tsx", "app/help/b/page.tsx", "app/help/[id]/page.tsx", "app/help/a/page.tsx"),
            Package("/help", "a/page.tsx", "page.tsx", "[slug]/page.tsx", "c/page.tsx"));

        Assert.Equal(["/help", "/help/[slug]", "/help/a"], table.PendingConflicts.Select(c => c.Pattern));
        Assert.Equal(["/help", "/help/[slug]", "/help/a", "/help/b", "/help/c"], table.Pages.Select(p => p.Pattern));
    }

    [Theory]
    [InlineData("contact/page.tsx", "contact/page.tsx")]
    [InlineData("contact/page.tsx", "(center)/contact/page.tsx")]
    public void TwoPackagesPagesOfOneShape_Throw_NamingBoth(string one, string another)
    {
        var ex = Assert.Throws<RouteConflictException>(() =>
            Build(App(), NamedPackage("@acme/a", "/help", one), NamedPackage("@acme/b", "/help", another)));

        Assert.Equal(
            $"'@acme/a/app/{one}' and '@acme/b/app/{another}' both match the URL '/help/contact'. Mount one of them at another prefix.",
            ex.Message);
    }

    [Theory]
    [InlineData("layout")]
    [InlineData("not-found")]
    [InlineData("loading")]
    [InlineData("template")]
    [InlineData("error")]
    [InlineData("forbidden")]
    [InlineData("unauthorized")]
    public void TwoSegmentFilesFromTwoSourcesInOneFolder_Throw_NamingBoth(string name)
    {
        var app = Assert.Throws<RouteConflictException>(() => Build(App($"app/help/{name}.tsx"), Package("/help", $"{name}.tsx")));
        Assert.Equal(
            $"'{Help}/app/{name}.tsx' and 'app/help/{name}.tsx' are both the {name} of app/help. Remove the app's file, " +
            "or mount the package at another prefix.",
            app.Message);

        var packages = Assert.Throws<RouteConflictException>(() =>
            Build(App(), NamedPackage("@acme/a", "/help", $"(g)/{name}.tsx"), NamedPackage("@acme/b", "/help/(g)", $"{name}.tsx")));
        Assert.Equal(
            $"'@acme/a/app/(g)/{name}.tsx' and '@acme/b/app/{name}.tsx' are both the {name} of app/help/(g). Mount one of " +
            "them at another prefix.",
            packages.Message);
    }

    [Fact]
    public void SlotFilesFromTwoSourcesInOneSlot_Throw_NamingBoth()
    {
        var page = Assert.Throws<RouteConflictException>(() =>
            Build(App("app/help/@modal/photo/page.tsx"), Package("/help", "@modal/photo/page.tsx")));
        Assert.Contains($"'{Help}/app/@modal/photo/page.tsx' and 'app/help/@modal/photo/page.tsx'", page.Message);

        var fallback = Assert.Throws<RouteConflictException>(() =>
            Build(App("app/help/@modal/default.tsx"), Package("/help", "@modal/default.tsx")));
        Assert.Contains($"'{Help}/app/@modal/default.tsx' and 'app/help/@modal/default.tsx'", fallback.Message);
    }

    // A slot shows the first of its pages that ranks best, so one source's would silently win.
    [Fact]
    public void SlotPagesOfOneShapeFromTwoSources_Throw_NamingBoth()
    {
        var ex = Assert.Throws<RouteConflictException>(() =>
            Build(App("app/help/@modal/[id]/page.tsx"), Package("/help", "@modal/[slug]/page.tsx")));

        Assert.Equal(
            $"'{Help}/app/@modal/[slug]/page.tsx' and 'app/help/@modal/[id]/page.tsx' both match the URL '/[slug]' in " +
            "app/help/@modal. Remove the app's file, or mount the package at another prefix.",
            ex.Message);
    }

    [Fact]
    public void SlotPagesOfOneShapeFromOneSource_AreLeftAsTheyAre()
    {
        var table = Build(App("app/page.tsx", "app/@modal/[id]/page.tsx", "app/@modal/[slug]/page.tsx"), Package("/help", "page.tsx"));

        Assert.Equal(["/[id]", "/[slug]"], table.RootSegments.Single().Slots["modal"].Pages.Keys.Order(StringComparer.Ordinal));
    }

    // The client shows the first intercepting route whose segment the page is in, so one source's would silently win.
    [Fact]
    public void TwoSourcesInterceptingOneUrlFromOneSlot_Throw_NamingBoth()
    {
        var ex = Assert.Throws<RouteConflictException>(() =>
            Build(App("app/help/@modal/(.)photo/[id]/page.tsx"), Package("/help", "@modal/(.)photo/[slug]/page.tsx")));

        Assert.Equal(
            $"'{Help}/app/@modal/(.)photo/[slug]/page.tsx' and 'app/help/@modal/(.)photo/[id]/page.tsx' both intercept the URL " +
            "'/help/photo/[slug]' from the pages in app/help. Remove the app's file, or mount the package at another prefix.",
            ex.Message);
    }

    [Fact]
    public void TwoSourcesInterceptingOneUrlFromNestedSegments_Throw()
    {
        var ex = Assert.Throws<RouteConflictException>(() =>
            Build(App("app/help/(a)/@drawer/(.)photo/page.tsx"), Package("/help", "@modal/(.)photo/page.tsx")));

        Assert.Contains("both intercept the URL '/help/photo' from the pages in app/help/(a).", ex.Message);
    }

    [Fact]
    public void TwoSourcesInterceptingOneUrlFromSeparateSegments_AreBothKept()
    {
        var table = Build(App("app/(site)/@modal/(...)help/photo/page.tsx"), Package("/help", "@modal/(.)photo/page.tsx"));

        Assert.Equal(["app/(site)", "app/help"], table.Intercepts.Select(i => i.Segment));
    }

    [Fact]
    public void TwoAppPagesOfOneShape_StillThrowAtOnce_AlsoWhenAPackageHasOneToo()
    {
        var ex = Assert.Throws<RouteConflictException>(() =>
            Build(App("app/help/contact/page.tsx", "app/help/(x)/contact/page.tsx"), Package("/help", "contact/page.tsx")));

        Assert.Equal(
            "'app/help/(x)/contact/page.tsx' and 'app/help/contact/page.tsx' both match the URL '/help/contact'. Rename or remove one of them.",
            ex.Message);
    }

    [Theory]
    [InlineData("/help/contact")]
    [InlineData("/HELP/Contact")]
    public void AnOverride_ResolvesThePendingConflict_WithTheAppsFileInThePackagesRoute(string pattern)
    {
        var table = RouteTable.Build(
            [App("app/layout.tsx", "app/help/(site)/layout.tsx", "app/help/(site)/contact/page.tsx"), Package("/help", "contact/page.tsx")],
            [pattern]);

        Assert.Empty(table.PendingConflicts);
        var route = Assert.Single(table.Pages);
        Assert.Equal("/help/contact", route.Pattern);
        Assert.Equal("app/help/(site)/contact/page.tsx", route.File);
        Assert.Equal($"{Help}/app/contact/page.tsx", route.Overrides);
        Assert.Equal("app", route.Source);
        Assert.Equal(["app/layout.tsx", "app/help/(site)/layout.tsx"], route.Layouts);
        Assert.Same(route, table.Find("/help/contact"));
    }

    [Fact]
    public void AnOverride_KeepsThePackagesSpellingOfTheUrl()
    {
        var table = RouteTable.Build([App("app/help/(site)/Contact/page.tsx"), Package("/help", "contact/page.tsx")], ["/help/contact"]);

        var route = Assert.Single(table.Pages);
        Assert.Equal("/help/contact", route.Pattern);
        Assert.Equal("/help/contact", route.Template);
        Assert.Equal("app/help/(site)/Contact/page.tsx", route.File);
    }

    [Fact]
    public void WithoutAnOverride_PagesLeftAloneHaveNone()
    {
        var table = Build(App("app/page.tsx"), Package("/help", "page.tsx"));

        Assert.All(table.Pages, p => Assert.Null(p.Overrides));
    }

    [Theory]
    [InlineData("/help/nope", new[] { "app/help/nope/page.tsx" }, new[] { "contact/page.tsx" }, "no page package has a page at '/help/nope'")]
    [InlineData("/about", new[] { "app/about/page.tsx" }, new[] { "contact/page.tsx" }, "no page package has a page at '/about'")]
    [InlineData("/help/contact", new string[0], new[] { "contact/page.tsx" }, "the app has no page at '/help/contact' (app/help/contact/page.tsx)")]
    [InlineData("/help/contact", new string[0], new[] { "(center)/contact/page.tsx" }, "the app has no page at '/help/contact' (app/help/(center)/contact/page.tsx)")]
    [InlineData("/help/articles/[slug]", new[] { "app/help/articles/[id]/page.tsx" }, new[] { "articles/[slug]/page.tsx" }, "name their parameters alike")]
    [InlineData("/help/articles/[id]", new[] { "app/help/articles/[slug]/page.tsx" }, new[] { "articles/[slug]/page.tsx" }, "name their parameters alike")]
    public void AnOverride_Throws_WhenItDoesNotFitTheFiles(string pattern, string[] appFiles, string[] packageFiles, string reason)
    {
        var ex = Assert.Throws<RouteConflictException>(() => RouteTable.Build([App(appFiles), Package("/help", packageFiles)], [pattern]));

        Assert.Contains($".Override(\"{pattern}\")", ex.Message);
        Assert.Contains(reason, ex.Message);
    }

    [Fact]
    public void TheTableAndItsErrors_AreTheSameWhateverOrderTheSourcesAndFilesComeIn()
    {
        PageSource[] sources =
        [
            App("app/layout.tsx", "app/help/(center)/shortcuts/page.tsx", "app/help/contact/page.tsx", "app/page.tsx"),
            Package("/help", "layout.tsx", "page.tsx", "(center)/layout.tsx", "(center)/articles/[slug]/page.tsx", "contact/page.tsx", "not-found.tsx"),
            NamedPackage("@acme/b", "/[tenant]/b", "layout.tsx", "page.tsx", "@modal/(.)x/page.tsx", "x/page.tsx"),
        ];
        var expected = Describe(Build(sources));

        foreach (var order in Orders(sources))
        {
            Assert.Equal(expected, Describe(Build(order)));
            Assert.Equal(expected, Describe(Build([.. order.Select(s => s with { Files = [.. s.Files.Reverse()] })])));
        }

        PageSource[] broken =
        [
            App("app/help/layout.tsx", "app/b/layout.tsx"),
            Package("/help", "layout.tsx"),
            NamedPackage("@acme/b", "/b", "layout.tsx"),
        ];
        var error = Assert.Throws<RouteConflictException>(() => Build(broken)).Message;
        foreach (var order in Orders(broken))
        {
            Assert.Equal(error, Assert.Throws<RouteConflictException>(() => Build(order)).Message);
        }

        PageSource[] outside =
        [
            App(),
            PageSource.Package("@acme/a", "app", MountPrefix.Parse("/a"), ["@acme/a/app/../y/page.tsx", "@acme/a/app/../x/page.tsx"]),
            PageSource.Package("@acme/b", "app", MountPrefix.Parse("/b"), ["@acme/b/app/../x/page.tsx"]),
        ];
        var invalid = Assert.Throws<InvalidPageFileException>(() => Build(outside)).Message;
        Assert.Contains("'@acme/a/app/../x/page.tsx'", invalid);
        foreach (var order in Orders(outside))
        {
            Assert.Equal(invalid, Assert.Throws<InvalidPageFileException>(() => Build(order)).Message);
            Assert.Equal(invalid, Assert.Throws<InvalidPageFileException>(() =>
                Build([.. order.Select(s => s with { Files = [.. s.Files.Reverse()] })])).Message);
        }
    }

    private static IEnumerable<PageSource[]> Orders(PageSource[] sources) => sources.Length <= 1
        ? [sources]
        : sources.SelectMany((source, i) => Orders([.. sources[..i], .. sources[(i + 1)..]]).Select(rest => (PageSource[])[source, .. rest]));

    // A file id names the package and a path inside its pages folder; one that leaves it could pass for another
    // package's file or the app's.
    [Theory]
    [InlineData("@acme/help-center/app/../../app/account/page.tsx")]
    [InlineData("@acme/help-center/app/../page.tsx")]
    [InlineData("@acme/help-center/app/./page.tsx")]
    [InlineData("@acme/help-center/app/a//page.tsx")]
    [InlineData("@acme/help-center/app/a\\page.tsx")]
    [InlineData("@acme/help-center/app\\page.tsx")]
    [InlineData("/@acme/help-center/app/page.tsx")]
    [InlineData("@acme/other/app/page.tsx")]
    [InlineData("app/page.tsx")]
    public void APackageFileId_OutsideItsPagesFolder_IsRefused(string file)
    {
        var source = PageSource.Package(Help, "app", MountPrefix.Parse("/help"), [file]);

        var ex = Assert.Throws<InvalidPageFileException>(() => Build(App(), source));

        Assert.Contains($"'{file}'", ex.Message);
        Assert.Contains($"'{Help}/app/'", ex.Message);
    }

    // Its ids would read as the app's files: app/app/page.tsx, or src/app/page.tsx for an app in src/app.
    [Theory]
    [InlineData("app", "app")]
    [InlineData("App", "app")]
    [InlineData("src", "src/app")]
    public void APackageNamedLikeTheAppDirectory_IsRefused(string name, string appDirectory)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            RouteTable.Build([PageSource.App([], appDirectory), PageSource.Package(name, "app", MountPrefix.Parse("/help"), [$"{name}/app/page.tsx"])], []));

        Assert.Contains($"'{name}'", ex.Message);
        Assert.Contains($"'{appDirectory}'", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("@acme")]
    [InlineData("@acme/")]
    [InlineData("@/x")]
    [InlineData("@acme/x/y")]
    [InlineData("x/y")]
    [InlineData("x\\y")]
    [InlineData(".x")]
    [InlineData("_x")]
    [InlineData("x y")]
    [InlineData("C:x")]
    public void APackageWhoseNameIsNoNpmName_IsRefused(string name)
    {
        var ex = Assert.Throws<ArgumentException>(() => Build(App(), PageSource.Package(name, "app", MountPrefix.Parse("/help"), [])));

        Assert.Contains($"'{name}'", ex.Message);
        Assert.Contains("npm", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("a/../b")]
    [InlineData("a/./b")]
    [InlineData("/app")]
    [InlineData("app/")]
    [InlineData("a//b")]
    [InlineData("a\\b")]
    [InlineData("C:/app")]
    public void APackageWhosePagesFolderLeavesThePackage_IsRefused(string pages)
    {
        var ex = Assert.Throws<ArgumentException>(() => Build(App(), PageSource.Package(Help, pages, MountPrefix.Parse("/help"), [])));

        Assert.Contains($"'{Help}/{pages}/'", ex.Message);
    }

    [Fact]
    public void ASourceWhoseFolderIsNoMountPrefix_IsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => Build(App(), new PageSource(Help, "../x", $"{Help}/app/", [])));

        Assert.Contains("'/../x' is not a mount prefix", ex.Message);
    }

    [Theory]
    [InlineData("@acme/help-center")]
    [InlineData("@ACME/Help-Center")]
    public void TwoSourcesOfOneName_AreRefused(string another)
    {
        var ex = Assert.Throws<ArgumentException>(() => Build(App(), Package("/help"), NamedPackage(another, "/other")));

        Assert.Contains("more than once", ex.Message);
    }

    [Fact]
    public void ATableWithoutTheAppsSourceOrWithTwo_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => Build(Package("/help")));
        Assert.Throws<ArgumentException>(() => Build(App(), App()));
    }

    [Theory]
    [InlineData("global-error.tsx")]
    [InlineData("(center)/global-error.tsx")]
    public void AGlobalErrorInAPackage_IsRefused(string file)
    {
        var ex = Assert.Throws<InvalidPageFileException>(() => Build(App(), Package("/help", file)));

        Assert.Equal(
            $"'{Help}/app/{file}': a page package cannot have a global-error file. The app's own (app/global-error) renders " +
            "when its root layout fails.",
            ex.Message);
    }

    [Theory]
    [InlineData("/help", "@modal/(...)photo/page.tsx")]
    [InlineData("/help", "@modal/(..)photo/page.tsx")]
    [InlineData("/help", "a/@modal/(..)(..)photo/page.tsx")]
    [InlineData("/help", "(center)/@modal/(..)photo/page.tsx")]
    [InlineData("/support/(desk)/help", "a/(g)/@modal/(..)(..)photo/page.tsx")]
    public void AnInterceptingRouteThatClimbsAboveTheMount_IsRefused(string prefix, string file)
    {
        var ex = Assert.Throws<InvalidPageFileException>(() => Build(App(), Package(prefix, file)));

        Assert.Contains($"'{Help}/app/{file}'", ex.Message);
        Assert.Contains($"above the mount prefix '{prefix}'", ex.Message);
    }

    [Theory]
    [InlineData("/help", "@modal/(.)photo/page.tsx", "/help/photo")]
    [InlineData("/help", "a/@modal/(..)photo/page.tsx", "/help/photo")]
    [InlineData("/help", "a/(g)/b/@modal/(..)(..)photo/page.tsx", "/help/photo")]
    [InlineData("/", "@modal/(...)photo/page.tsx", "/photo")]
    [InlineData("/(g)", "a/@modal/(...)photo/page.tsx", "/photo")]
    public void AnInterceptingRouteInsideTheMount_IsKept(string prefix, string file, string pattern)
    {
        var table = Build(App(), Package(prefix, file));

        Assert.Equal(pattern, Assert.Single(table.Intercepts).Pattern);
    }

    [Theory]
    [InlineData("[tenant]/page.tsx")]
    [InlineData("[Tenant]/page.tsx")]
    [InlineData("docs/[...tenant]/page.tsx")]
    [InlineData("docs/[[...tenant]]/page.tsx")]
    [InlineData("[tenant]/layout.tsx")]
    [InlineData("@modal/[tenant]/page.tsx")]
    public void APackageFileThatUsesAPrefixParameterAgain_IsRefused(string file)
    {
        var ex = Assert.Throws<InvalidPageFileException>(() => Build(App(), Package("/[tenant]/help", file)));

        Assert.Contains($"'{Help}/app/{file}'", ex.Message);
        Assert.Contains("'/[tenant]/help'", ex.Message);
    }

    [Fact]
    public void AColocatedFileInAFolderNamedLikeAPrefixParameter_IsLeftAlone()
    {
        var table = Build(App(), Package("/[tenant]/help", "page.tsx", "[tenant]/Card.tsx", "_lib/[tenant]/page.tsx"));

        Assert.Equal(["/[tenant]/help"], table.Pages.Select(p => p.Pattern));
    }

    // The folders would differ, so the package's layouts would not wrap the app's pages there, nor the app's the
    // package's, while the URLs match whatever their case.
    [Theory]
    [InlineData("/Help", "app/help/shortcuts/page.tsx", "page.tsx", "app/Help", "app/help")]
    [InlineData("/help", "app/Help/shortcuts/page.tsx", "page.tsx", "app/Help", "app/help")]
    [InlineData("/(Marketing)/help", "app/(marketing)/page.tsx", "page.tsx", "app/(Marketing)", "app/(marketing)")]
    [InlineData("/[Tenant]/help", "app/[tenant]/page.tsx", "page.tsx", "app/[Tenant]", "app/[tenant]")]
    [InlineData("/help", "app/help/(center)/shortcuts/page.tsx", "(Center)/layout.tsx", "app/help/(Center)", "app/help/(center)")]
    public void AFolderThatMatchesAnotherSourcesOnlyWhenCaseIsIgnored_IsRefused(
        string prefix, string appFile, string packageFile, string one, string another)
    {
        var ex = Assert.Throws<RouteConflictException>(() => Build(App(appFile), Package(prefix, packageFile)));

        Assert.Contains($"'{one}'", ex.Message);
        Assert.Contains($"'{another}'", ex.Message);
        Assert.Contains("differ only in case", ex.Message);
    }

    [Fact]
    public void FoldersOfOneSourceThatDifferOnlyInCase_AreLeftAsTheyAre()
    {
        var table = Build(App("app/About/layout.tsx", "app/about/page.tsx"), Package("/help", "page.tsx"));

        Assert.Equal(["/about", "/help"], table.Pages.Select(p => p.Pattern));
    }

    private static string Describe(RouteTable table) => string.Join("\n", [
        .. table.Pages.Select(p =>
            $"{p.Pattern} {p.Template} {p.File} {p.Source} {p.Overrides} [{string.Join(",", p.Parameters)}] " +
            $"[{string.Join(",", p.Segments.Select(Describe))}]"),
        $"not-found {table.NotFound?.File} [{string.Join(",", table.NotFound?.Segments.Select(Describe) ?? [])}]",
        $"error {table.Error?.File} [{string.Join(",", table.Error?.Segments.Select(Describe) ?? [])}]",
        $"global-error {table.GlobalError?.File}",
        $"root [{string.Join(",", table.RootSegments.Select(Describe))}]",
        .. table.Intercepts.Select(i => $"intercept {i}"),
        .. table.PendingConflicts.Select(c => $"pending {c}"),
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
