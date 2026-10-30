using System.Net;
using Netjes.Routing;
using Netjes.Tests.Endpoints;

namespace Netjes.Tests.Routing;

/// <summary>Netjes routes app/ only: page files left in pages/ stop the app instead of quietly serving 404s.</summary>
public class PagesFolderTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task PageFilesInPages_FailStartup_NamingThemAndAppDirectory(string environment)
    {
        // A production app with a valid build, so it is the leftover pages/ that stops it.
        var ex = await Assert.ThrowsAsync<InvalidPageFileException>(() => TestApp.StartAsync(
            ["app/page.tsx", "pages/index.tsx", "pages/blog/[slug].tsx", "pages/_app.tsx"], environment: environment,
            prepareRoot: environment == "Production" ? ModeTests.WriteProductionBuild : null));

        Assert.Contains("routes app/ only", ex.Message);
        Assert.Contains("pages/index.tsx to app/page.tsx", ex.Message);
        Assert.Contains("pages/blog/[slug].tsx to app/blog/[slug]/page.tsx", ex.Message);
        Assert.Contains("pages/_app.tsx to app/layout.tsx", ex.Message);
    }

    [Theory]
    [InlineData("pages/index.tsx", "app/page.tsx")]
    [InlineData("pages/about.jsx", "app/about/page.jsx")]
    [InlineData("pages/blog/index.tsx", "app/blog/page.tsx")]
    [InlineData("pages/blog/[slug].tsx", "app/blog/[slug]/page.tsx")]
    [InlineData("pages/docs/[...path].tsx", "app/docs/[...path]/page.tsx")]
    // The Next.js pages router's special files, at the root of pages/ only.
    [InlineData("pages/_app.tsx", "app/layout.tsx")]
    [InlineData("pages/_document.jsx", "app/layout.jsx")]
    [InlineData("pages/404.tsx", "app/not-found.tsx")]
    [InlineData("pages/500.tsx", "app/error.tsx")]
    [InlineData("pages/_error.tsx", "app/error.tsx")]
    [InlineData("pages/blog/404.tsx", "app/blog/404/page.tsx")]
    // JavaScript pages, as create-next-app writes them: app/ routes .jsx, not .js.
    [InlineData("pages/index.js", "app/page.jsx")]
    [InlineData("pages/blog/[slug].js", "app/blog/[slug]/page.jsx")]
    [InlineData("pages/_app.js", "app/layout.jsx")]
    [InlineData("pages/_document.js", "app/layout.jsx")]
    [InlineData("pages/404.js", "app/not-found.jsx")]
    [InlineData("pages/About.JS", "app/About/page.JSX")]
    public async Task PageFileInPages_FailsStartup_NamingItsAppFile(string file, string appFile)
    {
        var ex = await Assert.ThrowsAsync<InvalidPageFileException>(() => TestApp.StartAsync([file]));

        Assert.Contains($"{file} to {appFile}", ex.Message);
    }

    [Theory]
    [InlineData("pages/api/hello.ts")]
    [InlineData("pages/api/users/[id].js")]
    [InlineData("pages/api/index.tsx")]
    [InlineData("pages/api/auth/[...nextauth].JSX")]
    public async Task ApiRouteInPages_FailsStartup_NamingItAsAMinimalApi(string file)
    {
        var ex = await Assert.ThrowsAsync<InvalidPageFileException>(() => TestApp.StartAsync(["app/page.tsx", file]));

        Assert.Contains($"Map the API routes ({file}) as C# minimal API endpoints instead, with app.MapGet", ex.Message);
        Assert.DoesNotContain("app/api", ex.Message);
    }

    [Fact]
    public async Task OnlyApiRoutesInPages_FailStartup_WithAMessageThatNeedsNoList()
    {
        // No moves to list: the opening says which files it means by itself.
        var ex = await Assert.ThrowsAsync<InvalidPageFileException>(() => TestApp.StartAsync(
            ["app/page.tsx", "pages/api/hello.ts"]));

        Assert.Equal("Netjes routes app/ only, so it would not serve the pages router files in pages/. Map the API " +
            "routes (pages/api/hello.ts) as C# minimal API endpoints instead, with app.MapGet or app.MapPost.", ex.Message);
    }

    [Theory]
    [InlineData("pages/_app.tsx", true)]
    [InlineData("pages/_document.tsx", true)]
    [InlineData("pages/about.tsx", false)]
    public async Task AppOrDocumentInPages_FailsStartup_SayingWhatTheRootLayoutDoes(string file, bool says)
    {
        var ex = await Assert.ThrowsAsync<InvalidPageFileException>(() => TestApp.StartAsync(["app/page.tsx", file]));

        const string rootLayout = "The root layout wraps every page. Netjes renders <html>, <head> and <body> " +
            "around it, and the <title>, <meta> and <link> tags a layout renders end up in <head>.";
        Assert.Equal(says, ex.Message.Contains(rootLayout, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PageFilesInPages_FailStartup_EvenWithoutAnAppDirectory()
    {
        var ex = await Assert.ThrowsAsync<InvalidPageFileException>(() => TestApp.StartAsync(["pages/about.jsx"]));

        Assert.Contains("pages/about.jsx", ex.Message);
    }

    [Fact]
    public async Task ManyPageFilesInPages_AreCounted_NotAllListed()
    {
        var ex = await Assert.ThrowsAsync<InvalidPageFileException>(() => TestApp.StartAsync(
            [.. Enumerable.Range(1, 8).Select(i => $"pages/p{i}.tsx")]));

        Assert.Contains("pages/p1.tsx", ex.Message);
        Assert.Contains("and 3 more", ex.Message);
        Assert.DoesNotContain("pages/p8.tsx", ex.Message);
    }

    [Fact]
    public async Task ManyPageFilesAndApiRoutesInPages_NameFiveInAll()
    {
        var ex = await Assert.ThrowsAsync<InvalidPageFileException>(() => TestApp.StartAsync(
            [.. Enumerable.Range(1, 8).Select(i => $"pages/p{i}.tsx"),
             .. Enumerable.Range(1, 3).Select(i => $"pages/api/a{i}.ts")]));

        Assert.Contains("pages/p4.tsx to app/p4/page.tsx, and 4 more the same way)", ex.Message);
        Assert.Contains("Map the API routes (pages/api/a1.ts and 2 more) as", ex.Message);
    }

    [Fact]
    public async Task ManyApiRoutesInPages_AreCounted_NotAllListed()
    {
        var ex = await Assert.ThrowsAsync<InvalidPageFileException>(() => TestApp.StartAsync(
            [.. Enumerable.Range(1, 8).Select(i => $"pages/api/a{i}.ts")]));

        Assert.Contains("pages/api/a5.ts, and 3 more) as", ex.Message);
    }

    [Fact]
    public async Task ManyPageFilesInPages_ListTheSpecialFilesFirst()
    {
        // [locale]/ sorts before _app.tsx; the special files do not move the same way as pages, so they are named, and
        // the five that are counted are pages.
        var ex = await Assert.ThrowsAsync<InvalidPageFileException>(() => TestApp.StartAsync(
            ["pages/_document.tsx", "pages/_app.tsx",
             .. Enumerable.Range(1, 6).Select(i => $"pages/[locale]/p{i}.tsx")]));

        Assert.Contains("(pages/_app.tsx to app/layout.tsx, pages/_document.tsx to app/layout.tsx, " +
            "pages/[locale]/p1.tsx to app/[locale]/p1/page.tsx,", ex.Message);
        Assert.Contains("pages/[locale]/p5.tsx to app/[locale]/p5/page.tsx, and 1 more the same way)", ex.Message);
    }

    [Fact]
    public async Task AllSpecialFilesInPages_AreNamed_AndThePagesCounted()
    {
        // Each special file has its own App Router file, so none is left to "the same way", however many there are.
        var ex = await Assert.ThrowsAsync<InvalidPageFileException>(() => TestApp.StartAsync(
            ["pages/_app.tsx", "pages/_document.tsx", "pages/_error.tsx", "pages/404.tsx", "pages/500.tsx",
             "pages/index.tsx", "pages/api/hello.ts"]));

        Assert.Contains("(pages/404.tsx to app/not-found.tsx, pages/500.tsx to app/error.tsx, " +
            "pages/_app.tsx to app/layout.tsx, pages/_document.tsx to app/layout.tsx, " +
            "pages/_error.tsx to app/error.tsx, pages/index.tsx to app/page.tsx)", ex.Message);
        Assert.Contains("Map the API routes (pages/api/hello.ts) as", ex.Message);
    }

    [Fact]
    public async Task PagesNextToACustomAppDirectory_FailStartup()
    {
        var ex = await Assert.ThrowsAsync<InvalidPageFileException>(() => TestApp.StartAsync(
            ["src/app/page.tsx", "src/pages/index.tsx"], configure: o => o.AppDirectory = "src/app"));

        Assert.Contains("routes src/app/ only", ex.Message);
        Assert.Contains("src/pages/index.tsx to src/app/page.tsx", ex.Message);
    }

    [Fact]
    public async Task PagesInTheRootNextToACustomAppDirectory_AreNamedForIt()
    {
        var ex = await Assert.ThrowsAsync<InvalidPageFileException>(() => TestApp.StartAsync(
            ["src/app/page.tsx", "pages/about.tsx"], configure: o => o.AppDirectory = "src/app"));

        Assert.Contains("pages/about.tsx to src/app/about/page.tsx", ex.Message);
    }

    [Fact]
    public async Task PagesWithoutPageFiles_Starts()
    {
        // What the pages router never served: tests, _ files other than its special files at the root, and _ folders.
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx", "pages/notes.md", "pages/index.test.tsx", "pages/about.spec.jsx", "pages/blog.test.js",
             "pages/data.json",
             "pages/_layout.tsx", "pages/_404.tsx", "pages/blog/_app.tsx", "pages/blog/_helpers.jsx",
             "pages/_components/Nav.tsx", "pages/_components/_app.tsx", "pages/lib.ts", "pages/api/hello.test.ts",
             "pages/api/types.d.ts", "pages/api/_db.ts", "pages/api/_lib/db.ts", "pages/api/schema.json"]);

        Assert.Equal(HttpStatusCode.OK, (await app.GetDataAsync("/")).StatusCode);
    }

    [Fact]
    public async Task AppDirectoryCalledPages_Starts()
    {
        await using var app = await TestApp.StartAsync(["pages/page.tsx"], configure: o => o.AppDirectory = "pages");

        Assert.Equal("pages/page.tsx", (string?)(await app.GetDataJsonAsync("/"))["page"]);
    }
}
