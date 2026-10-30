using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Netjes.Tests.Endpoints;

/// <summary>Mounted under <c>app.UsePathBase("/base")</c>, like Next.js's <c>basePath</c>.</summary>
public class BasePathTests
{
    private const string Manifest = """
        {
          "node_modules/netjes/dist/client-entry.js": { "file": "assets/entry.js", "src": "node_modules/netjes/dist/client-entry.js", "isEntry": true, "css": ["assets/entry.css"] },
          "app/about/page.tsx": { "file": "assets/about.js", "src": "app/about/page.tsx", "isDynamicEntry": true }
        }
        """;

    private static void WriteBuild(string root)
    {
        var client = Path.Combine(root, "dist", "client");
        Directory.CreateDirectory(Path.Combine(client, ".vite"));
        File.WriteAllText(Path.Combine(client, ".vite", "manifest.json"), Manifest);
    }

    [Fact]
    public async Task Payloads_CarryTheBasePath_AndUrlsIncludeIt()
    {
        await using var app = await TestApp.StartAsync(["app/about/page.tsx"], pathBase: "/base");

        var payload = await app.GetDataJsonAsync("/base/about?tab=1");

        Assert.Equal("/base", (string?)payload["basePath"]);
        Assert.Equal("/base/about?tab=1", (string?)payload["url"]);
        Assert.Equal("/about", (string?)payload["route"]);
    }

    [Fact]
    public async Task DocumentUrls_ArePrefixed()
    {
        await using var app = await TestApp.StartAsync(
            [], environment: "Production", prepareRoot: WriteBuild, pathBase: "/base", configure: o => o.CollectWebVitals = true);

        await app.GetDocumentAsync("/base/about");

        var document = app.Renderer.LastPayload["document"]!;
        Assert.Equal(["/base/assets/entry.js"], document["assets"]!["scripts"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(["/base/assets/entry.css"], document["assets"]!["stylesheets"]!.AsArray().Select(n => (string?)n));
        Assert.Equal("/base/_netjes/image", (string?)document["images"]!["path"]);
        Assert.Equal("/base", (string?)document["images"]!["basePath"]);
        Assert.Equal("/base/_netjes/vitals", (string?)document["vitalsEndpoint"]);
    }

    [Fact]
    public async Task LocalRedirects_StayUnderTheBasePath()
    {
        await using var app = await TestApp.StartAsync(
            ["app/old/page.tsx", "app/new/page.tsx"],
            pathBase: "/base",
            map: a => a.MapPageLoader("/old", () => Page.Redirect("/new")));

        var document = await app.GetDocumentAsync("/base/old");
        var data = await app.GetDataJsonAsync("/base/old");

        Assert.Equal(HttpStatusCode.TemporaryRedirect, document.StatusCode);
        Assert.Equal("/base/new", document.Headers.Location?.ToString());
        Assert.Equal("/base/new", (string?)data["location"]);
    }

    [Fact]
    public async Task RedirectsThatAlreadyIncludeTheBasePath_AreNotPrefixedTwice()
    {
        // E.g. a path built with LinkGenerator or Url.Content("~/new"), which include PathBase.
        await using var app = await TestApp.StartAsync(
            ["app/old/page.tsx", "app/new/page.tsx"],
            pathBase: "/base",
            map: a => a.MapPageLoader("/old", (HttpContext http) => Page.Redirect($"{http.Request.PathBase}/new")));

        var document = await app.GetDocumentAsync("/base/old");

        Assert.Equal("/base/new", document.Headers.Location?.ToString());
    }

    [Fact]
    public async Task AbsoluteRedirects_AreLeftAlone()
    {
        await using var app = await TestApp.StartAsync(
            ["app/old/page.tsx"], pathBase: "/base", map: a => a.MapPageLoader("/old", () => Page.Redirect("https://example.com/x")));

        var document = await app.GetDocumentAsync("/base/old");

        Assert.Equal("https://example.com/x", document.Headers.Location?.ToString());
    }

    [Fact]
    public async Task WithoutABasePath_NothingChanges()
    {
        await using var app = await TestApp.StartAsync(["app/about/page.tsx"]);

        var payload = await app.GetDataJsonAsync("/about");

        Assert.Null(payload["basePath"]);
        Assert.Equal("/about", (string?)payload["url"]);
    }
}
