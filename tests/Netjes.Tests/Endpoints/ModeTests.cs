using System.Net;

namespace Netjes.Tests.Endpoints;

public class ModeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Manifest = """
        {
          "node_modules/netjes/dist/client-entry.js": {
            "file": "assets/client-entry-AAA.js",
            "src": "node_modules/netjes/dist/client-entry.js",
            "isEntry": true,
            "css": ["assets/client-entry-BBB.css"]
          },
          "app/page.tsx": { "file": "assets/index-CCC.js", "src": "app/page.tsx", "isDynamicEntry": true },
          "app/about/page.tsx": { "file": "assets/about-DDD.js", "src": "app/about/page.tsx", "isDynamicEntry": true, "css": ["assets/about-EEE.css"] },
          "app/docs/page.tsx": { "file": "assets/page-FFF.js", "src": "app/docs/page.tsx", "isDynamicEntry": true }
        }
        """;

    internal static void WriteProductionBuild(string root)
    {
        var client = Path.Combine(root, "dist", "client");
        Directory.CreateDirectory(Path.Combine(client, ".vite"));
        Directory.CreateDirectory(Path.Combine(client, "assets"));
        File.WriteAllText(Path.Combine(client, ".vite", "manifest.json"), Manifest);
        File.WriteAllText(Path.Combine(client, "assets", "index-CCC.js"), "export {}");
        File.WriteAllText(Path.Combine(client, "favicon.svg"), "<svg/>");
    }

    [Fact]
    public async Task Production_RoutesComeFromTheBuildManifest()
    {
        // No page sources on disk: production serves whatever was built.
        await using var app = await TestApp.StartAsync([], environment: "Production", prepareRoot: WriteProductionBuild);

        var about = await app.GetDataJsonAsync("/about");

        Assert.Equal("app/about/page.tsx", (string?)about["page"]);
        Assert.Equal("app/docs/page.tsx", (string?)(await app.GetDataJsonAsync("/docs"))["page"]);
    }

    [Fact]
    public async Task Production_DocumentAssetsComeFromTheManifestAndVersionIdentifiesTheBuild()
    {
        await using var app = await TestApp.StartAsync([], environment: "Production", prepareRoot: WriteProductionBuild);

        await app.GetDocumentAsync("/about");

        var payload = app.Renderer.LastPayload;
        var assets = payload["document"]!["assets"]!;
        Assert.Equal(["/assets/client-entry-AAA.js"], assets["scripts"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(
            ["/assets/client-entry-BBB.css", "/assets/about-EEE.css"],
            assets["stylesheets"]!.AsArray().Select(n => (string?)n));
        Assert.Matches("^[0-9a-f]{12}$", (string?)payload["version"]);
    }

    [Fact]
    public async Task Production_HashedAssetsAreServedWithImmutableCaching()
    {
        await using var app = await TestApp.StartAsync([], environment: "Production", prepareRoot: WriteProductionBuild);

        var asset = await app.GetDocumentAsync("/assets/index-CCC.js");
        var publicFile = await app.GetDocumentAsync("/favicon.svg");

        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.True(asset.Headers.CacheControl?.Public);
        Assert.Equal(TimeSpan.FromDays(365), asset.Headers.CacheControl?.MaxAge);
        Assert.Equal(HttpStatusCode.OK, publicFile.StatusCode);
        Assert.Null(publicFile.Headers.CacheControl?.MaxAge);
    }

    [Theory]
    [InlineData("/.vite/manifest.json")]
    [InlineData("/%2evite/manifest.json")]
    [InlineData("/.VITE/manifest.json")]
    [InlineData("http://localhost//.vite/manifest.json")]
    [InlineData("http://localhost///.vite/manifest.json")]
    [InlineData("/%5C.vite/manifest.json")]
    [InlineData("/.vite%5Cmanifest.json")]
    public async Task Production_DoesNotServeTheViteManifest(string path)
    {
        await using var app = await TestApp.StartAsync([], environment: "Production", prepareRoot: WriteProductionBuild);

        var response = await app.GetDocumentAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("client-entry", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Production_WithoutBuild_FailsAtStartupWithInstructions()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => TestApp.StartAsync(["app/page.tsx"], environment: "Production"));

        Assert.Contains("npm run build", ex.ToString());
    }

    [Fact]
    public async Task Production_WithoutBuild_SaysHowToDevelopBeforeHowToBuild()
    {
        // Most often a fresh app started without the Development environment (dotnet run --no-launch-profile, an IDE).
        var ex = await Assert.ThrowsAsync<NetjesStartupException>(() => TestApp.StartAsync(["app/page.tsx"], environment: "Production"));

        var develop = ex.Message.IndexOf("npm run dev", StringComparison.Ordinal);
        var build = ex.Message.IndexOf("npm run build", StringComparison.Ordinal);
        Assert.True(develop >= 0 && develop < build, ex.Message);
        Assert.Contains("ASPNETCORE_ENVIRONMENT=Development", ex.Message);
    }

    [Fact]
    public async Task Development_NewPageFile_IsRoutableWithoutRestart()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"]);
        Assert.Equal(HttpStatusCode.NotFound, (await app.GetDataAsync("/fresh")).StatusCode);

        Directory.CreateDirectory(Path.Combine(app.Root, "app", "fresh"));
        await File.WriteAllTextAsync(Path.Combine(app.Root, "app", "fresh", "page.tsx"), "export default () => null", Ct);

        var status = HttpStatusCode.NotFound;
        for (var attempt = 0; attempt < 50 && status != HttpStatusCode.OK; attempt++)
        {
            await Task.Delay(100, Ct);
            status = (await app.GetDataAsync("/fresh")).StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, status);
    }
}
