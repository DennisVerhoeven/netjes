using Netjes.Routing;

namespace Netjes.Tests.Routing;

public class RouteTableTests
{
    [Theory]
    [InlineData("app/page.tsx", "/", "/")]
    [InlineData("app/about/page.tsx", "/about", "/about")]
    [InlineData("app/about/page.jsx", "/about", "/about")]
    [InlineData("app/blog/[slug]/page.tsx", "/blog/[slug]", "/blog/{slug}")]
    [InlineData("app/shop/[category]/[id]/page.tsx", "/shop/[category]/[id]", "/shop/{category}/{id}")]
    [InlineData("app/docs/[...path]/page.tsx", "/docs/[...path]", "/docs/{**path:minlength(1)}")]
    [InlineData("app/shop/[[...filters]]/page.tsx", "/shop/[[...filters]]", "/shop/{**filters}")]
    [InlineData("app/(marketing)/pricing/page.tsx", "/pricing", "/pricing")]
    [InlineData("app/(marketing)/page.tsx", "/", "/")]
    [InlineData("app\\blog\\[slug]\\page.tsx", "/blog/[slug]", "/blog/{slug}")]
    public void Build_PageFile_MapsToPatternAndTemplate(string file, string pattern, string template)
    {
        var table = RouteTable.Build([file]);

        var route = Assert.Single(table.Pages);
        Assert.Equal(pattern, route.Pattern);
        Assert.Equal(template, route.Template);
        Assert.Equal(file.Replace('\\', '/'), route.File);
    }

    [Fact]
    public void Build_DynamicSegments_ExposeParametersWithKinds()
    {
        var table = RouteTable.Build(["app/[locale]/docs/[...path]/page.tsx", "app/shop/[[...rest]]/page.tsx"]);

        var docs = table.Find("/[locale]/docs/[...path]")!;
        Assert.Equal(
            [new RouteParameter("locale", RouteParameterKind.Single), new RouteParameter("path", RouteParameterKind.CatchAll)],
            docs.Parameters);
        var shop = table.Find("/shop/[[...rest]]")!;
        Assert.Equal([new RouteParameter("rest", RouteParameterKind.OptionalCatchAll)], shop.Parameters);
    }

    [Fact]
    public void Build_Layouts_AppliedOutermostFirstToPagesInTheirFolder()
    {
        var table = RouteTable.Build([
            "app/layout.tsx",
            "app/blog/layout.tsx",
            "app/blog/[slug]/page.tsx",
            "app/about/page.tsx",
        ]);

        Assert.Equal(["app/layout.tsx", "app/blog/layout.tsx"], table.Find("/blog/[slug]")!.Layouts);
        Assert.Equal(["app/layout.tsx"], table.Find("/about")!.Layouts);
    }

    [Fact]
    public void Build_RouteGroupLayout_AppliesOnlyInsideTheGroup()
    {
        var table = RouteTable.Build([
            "app/(shop)/layout.tsx",
            "app/(shop)/cart/page.tsx",
            "app/about/page.tsx",
        ]);

        Assert.Equal(["app/(shop)/layout.tsx"], table.Find("/cart")!.Layouts);
        Assert.Empty(table.Find("/about")!.Layouts);
    }

    [Fact]
    public void Build_RootLayouts_AreExposedForBuiltInSpecialPages()
    {
        var table = RouteTable.Build(["app/layout.tsx", "app/(group)/layout.tsx", "app/page.tsx"]);

        Assert.Equal(["app/layout.tsx"], table.RootLayouts);
    }

    [Fact]
    public void Build_WithoutSpecialPages_LeavesThemNull()
    {
        var table = RouteTable.Build(["app/layout.tsx", "app/page.tsx"]);

        Assert.Null(table.NotFound);
        Assert.Null(table.Error);
    }

    [Theory]
    [InlineData("app/_components/page.tsx")]
    [InlineData("app/blog/_helpers/page.tsx")]
    [InlineData("app/styles.css")]
    [InlineData("app/utils.ts")]
    [InlineData("app/about/Header.tsx")]
    [InlineData("src/components/Header.tsx")]
    [InlineData("appextra/about/page.tsx")]
    [InlineData("app/about/page.test.tsx")]
    [InlineData("app/blog/[slug]/page.test.tsx")]
    [InlineData("app/blog/page.spec.jsx")]
    [InlineData("pages/index.tsx")]
    [InlineData("pages/about.tsx")]
    [InlineData("pages/blog/[slug].tsx")]
    public void Build_NonPageFiles_AreIgnored(string file)
    {
        var table = RouteTable.Build([file]);

        Assert.Empty(table.Pages);
    }

    [Theory]
    [InlineData("app/about/page.tsx", "app/(group)/about/page.tsx")]
    [InlineData("app/blog/[slug]/page.tsx", "app/blog/[id]/page.tsx")]
    [InlineData("app/[lang]/about/page.tsx", "app/[slug]/about/page.tsx")]
    [InlineData("app/page.tsx", "app/(group)/page.tsx")]
    [InlineData("app/docs/[...a]/page.tsx", "app/docs/[...b]/page.tsx")]
    [InlineData("app/@modal/photo/page.tsx", "app/@modal/(group)/photo/page.tsx")]
    // One convention in two files: which one won would depend on the order the files are listed in, which is the
    // file system's in development and the build manifest's in production.
    [InlineData("app/about/page.tsx", "app/about/page.jsx")]
    [InlineData("app/page.tsx", "app/page.JSX")]
    [InlineData("app/about/layout.tsx", "app/about/layout.jsx")]
    [InlineData("app/not-found.tsx", "app/not-found.jsx")]
    [InlineData("app/@modal/photo/page.tsx", "app/@modal/photo/page.jsx")]
    [InlineData("app/@modal/default.tsx", "app/@modal/default.jsx")]
    public void Build_TwoFilesForTheSameUrl_ThrowsConflictNamingBothFiles(string first, string second)
    {
        foreach (var files in new[] { [first, second], new[] { second, first } })
        {
            var ex = Assert.Throws<RouteConflictException>(() => RouteTable.Build(files));

            Assert.Contains(first, ex.Message);
            Assert.Contains(second, ex.Message);
        }
    }

    // Development lists the files as the file system enumerates them, production as the build manifest has them.
    [Theory]
    [InlineData("'app/blog/[id]/page.tsx' and 'app/blog/[slug]/page.tsx' both match the URL '/blog/[id]'.",
        "app/blog/[slug]/page.tsx", "app/blog/[id]/page.tsx")]
    [InlineData("'app/(group)/about/page.tsx' and 'app/about/page.tsx' both match the URL '/about'.",
        "app/about/page.tsx", "app/(group)/about/page.tsx")]
    [InlineData("'app/About/page.tsx' and 'app/about/page.tsx' both match the URL '/About'.",
        "app/about/page.tsx", "app/About/page.tsx")]
    // Three of one shape: the first two in ordinal order are named.
    [InlineData("'app/docs/[...a]/page.tsx' and 'app/docs/[...b]/page.tsx' both match the URL '/docs/[...a]'.",
        "app/docs/[...c]/page.tsx", "app/docs/[...b]/page.tsx", "app/docs/[...a]/page.tsx")]
    public void Build_AConflict_ReadsTheSameWhateverOrderTheFilesAreListedIn(string says, params string[] files)
    {
        foreach (var order in Orders(files))
        {
            var ex = Assert.Throws<RouteConflictException>(() => RouteTable.Build(order));

            Assert.Equal($"{says} Rename or remove one of them.", ex.Message);
        }
    }

    private static IEnumerable<string[]> Orders(string[] files) => files.Length <= 1
        ? [files]
        : files.SelectMany((file, i) => Orders([.. files[..i], .. files[(i + 1)..]]).Select(rest => (string[])[file, .. rest]));

    [Theory]
    [InlineData("app/[...rest]/edit/page.tsx", "catch-all")]
    [InlineData("app/[[...rest]]/edit/page.tsx", "catch-all")]
    [InlineData("app/[foo-bar]/page.tsx", "parameter name")]
    [InlineData("app/[id]/[id]/page.tsx", "more than once")]
    [InlineData("app/{id}/page.tsx", "not allowed")]
    public void Build_InvalidPageFile_ThrowsWithReason(string file, string reason)
    {
        var ex = Assert.Throws<InvalidPageFileException>(() => RouteTable.Build([file]));

        Assert.Contains(file, ex.Message);
        Assert.Contains(reason, ex.Message);
    }

    [Fact]
    public void Find_UnknownPattern_ReturnsNull()
    {
        var table = RouteTable.Build(["app/page.tsx"]);

        Assert.Null(table.Find("/missing"));
    }

    [Fact]
    public void Build_Pages_AreOrderedByPatternForStableOutput()
    {
        var table = RouteTable.Build(["app/zeta/page.tsx", "app/alpha/page.tsx", "app/page.tsx"]);

        Assert.Equal(["/", "/alpha", "/zeta"], table.Pages.Select(p => p.Pattern));
    }
}
