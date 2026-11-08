using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Netjes.Routing;

namespace Netjes.Tests.Endpoints;

/// <summary>
/// The URLs of a mounted page package: <see cref="PagePackageMount.Url"/> for a package's own services in C#, and the
/// <c>mounts</c> each page payload carries for its links in the browser.
/// </summary>
public class PagePackageMountTests
{
    private const string Help = TestPagePackage.Name;

    private static PagePackageMount MountAt(string prefix) => new(TestPagePackage.Descriptor, MountPrefix.Parse(prefix));

    private static Task<TestApp> StartAsync(string prefix, string[]? appFiles = null, Action<WebApplication>? map = null) =>
        TestApp.StartAsync(
            appFiles ?? ["app/page.tsx"],
            prepareRoot: root => TestPagePackage.Install(root, ["page.tsx", "x/page.tsx"]),
            map: a =>
            {
                a.MapPagePackage(TestPagePackage.Descriptor, prefix);
                map?.Invoke(a);
            });

    [Fact]
    public async Task Payload_MapsEachMountedPackageToItsUrl_OnEveryPage()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            prepareRoot: root =>
            {
                TestPagePackage.Install(root, ["page.tsx", "x/page.tsx"]);
                TestPagePackage.Install(root, ["status/page.tsx"], name: TestPagePackage.Other.Name);
            },
            map: a =>
            {
                a.MapPagePackage(TestPagePackage.Descriptor, "/support/(desk)/help");
                a.MapPagePackage(TestPagePackage.Other, "/");
            });

        foreach (var url in new[] { "/support/help/x", "/support/help", "/status", "/" })
        {
            var mounts = (await app.GetDataJsonAsync(url))["mounts"]!.AsObject();

            Assert.Equal(2, mounts.Count);
            Assert.Equal("/support/help", (string?)mounts[Help]);
            Assert.Equal("/", (string?)mounts[TestPagePackage.Other.Name]);
        }
    }

    [Fact]
    public async Task Payload_FillsAPrefixsValueFromEachRequest_AndIsNullWhereTheRequestHasNone()
    {
        await using var app = await StartAsync("/[tenant]/help");

        Assert.Equal("/acme/help", (string?)(await app.GetDataJsonAsync("/acme/help/x"))["mounts"]![Help]);
        Assert.Equal("/other/help", (string?)(await app.GetDataJsonAsync("/other/help"))["mounts"]![Help]);
        var outside = (await app.GetDataJsonAsync("/"))["mounts"]!.AsObject();
        Assert.True(outside.ContainsKey(Help));
        Assert.Null(outside[Help]);
    }

    [Fact]
    public async Task Payload_LeavesMountsOut_WhenNothingIsMounted()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"]);

        Assert.False((await app.GetDataJsonAsync("/")).AsObject().ContainsKey("mounts"));
        await app.GetDocumentAsync("/");
        Assert.False(app.Renderer.LastPayload.AsObject().ContainsKey("mounts"));
    }

    [Fact]
    public async Task Payload_CarriesMountsInTheDocument_AndOnAnUnknownUrl()
    {
        await using var app = await StartAsync("/help");

        await app.GetDocumentAsync("/help/x");
        Assert.Equal("/help", (string?)app.Renderer.LastPayload["mounts"]![Help]);
        await app.GetDocumentAsync("/nope");
        Assert.Equal("/help", (string?)app.Renderer.LastPayload["mounts"]![Help]);
    }

    [Fact]
    public async Task Payload_KeepsThePackagesName_UnderADictionaryKeyPolicy()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            prepareRoot: root => TestPagePackage.Install(root, ["page.tsx"]),
            map: a => a.MapPagePackage(TestPagePackage.Descriptor, "/help"),
            services: s => s.ConfigureHttpJsonOptions(o => o.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseUpper));

        Assert.Equal("/help", (string?)(await app.GetDataJsonAsync("/help"))["mounts"]![Help]);
    }

    // (security) The value is the segment the request had, encoded again for the browser: a link below it goes back to
    // the same tenant, never to another path or host.
    [Theory]
    [InlineData("/a%2Fb/help/x", "/a%252Fb/help")]
    [InlineData("/a%3Fb/help/x", "/a%3Fb/help")]
    [InlineData("/a%23b/help/x", "/a%23b/help")]
    [InlineData("/100%25/help/x", "/100%25/help")]
    [InlineData("/a%20b/help/x", "/a%20b/help")]
    [InlineData("/%C3%BC/help/x", "/%C3%BC/help")]
    [InlineData("/ACME/HELP/x", "/ACME/help")]
    public async Task Payload_EncodesAPrefixsValueAsOneSegment(string url, string expected)
    {
        await using var app = await StartAsync("/[tenant]/help");

        var payload = await app.GetDataJsonAsync(url);

        Assert.Equal($"{Help}/app/x/page.tsx", (string?)payload["page"]);
        Assert.Equal(expected, (string?)payload["mounts"]![Help]);
    }

    // (security) A value a browser would resolve away gives no URL: a link below /../help would land on /help.
    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task Payload_IsNullForAPrefixValueThatIsEmptyOrADotSegment(string tenant)
    {
        await using var app = await StartAsync(
            "/[tenant]/help",
            map: a => a.MapGet("/render/{tenant?}", (HttpContext http) =>
            {
                http.Request.RouteValues["tenant"] = tenant;
                return Page.Render("/[tenant]/help/x");
            }));

        var mounts = (await app.GetDataJsonAsync("/render/acme"))["mounts"]!.AsObject();

        Assert.True(mounts.ContainsKey(Help));
        Assert.Null(mounts[Help]);
    }

    [Theory]
    [InlineData("/help", "/articles", "/help/articles")]
    [InlineData("/help", "/", "/help")]
    [InlineData("/help", "/?page=2", "/help?page=2")]
    [InlineData("/help", "/#top", "/help#top")]
    [InlineData("/help", "/articles/x?page=2#top", "/help/articles/x?page=2#top")]
    [InlineData("/support/(desk)/help", "/articles", "/support/help/articles")]
    [InlineData("/(marketing)/help", "/", "/help")]
    [InlineData("/", "/articles", "/articles")]
    [InlineData("/", "/", "/")]
    public void Url_PutsAStaticPrefixInFrontOfTheMountRelativePath(string prefix, string path, string expected)
    {
        Assert.Equal(new Href(expected), MountAt(prefix).Url(path));
    }

    [Fact]
    public void Url_FillsThePrefixsValues_AndIgnoresTheOthers()
    {
        var mount = MountAt("/[tenant]/(desk)/help/[lang]");

        var url = mount.Url("/articles", new RouteValueDictionary { ["tenant"] = "acme", ["LANG"] = "nl", ["slug"] = "x" });

        Assert.Equal(new Href("/acme/help/nl/articles"), url);
    }

    // (security) A value is one segment of the URL: a "/" (from %2F), "?", "#" or "%" in it stays in that segment, so a
    // link stays below its tenant.
    [Theory]
    [InlineData("a/b", "/a%2Fb/help/x")]
    [InlineData("a%2Fb", "/a%252Fb/help/x")]
    [InlineData("a?b", "/a%3Fb/help/x")]
    [InlineData("a#b", "/a%23b/help/x")]
    [InlineData("100%", "/100%25/help/x")]
    [InlineData("a b", "/a%20b/help/x")]
    [InlineData("..a", "/..a/help/x")]
    [InlineData("ü", "/%C3%BC/help/x")]
    public void Url_EncodesEachValueAsOneSegment(string tenant, string expected)
    {
        Assert.Equal(new Href(expected), MountAt("/[tenant]/help").Url("/x", new RouteValueDictionary { ["tenant"] = tenant }));
    }

    // (security) A value the URL cannot carry as a segment of its own: none, an empty one, or a dot segment, which a
    // browser resolves away (/../help/x would be /help/x, outside every tenant).
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    public void Url_ThrowsForAValueThePrefixCannotTake(string? tenant)
    {
        var values = new RouteValueDictionary();
        if (tenant is not null)
        {
            values["tenant"] = tenant;
        }

        var ex = Assert.Throws<ArgumentException>(() => MountAt("/[tenant]/help").Url("/x", values));

        Assert.Equal("values", ex.ParamName);
        Assert.Contains($"'{TestPagePackage.Name}' is mounted at /[tenant]/help", ex.Message);
        Assert.Contains("'tenant'", ex.Message);
    }

    [Fact]
    public void Url_WithoutValues_ThrowsForAPrefixWithOne()
    {
        var ex = Assert.Throws<ArgumentException>(() => MountAt("/[tenant]/help").Url("/x"));

        Assert.Contains("'tenant'", ex.Message);
    }

    // (security) Only a path below the mount gets the prefix: another host, a scheme or a relative URL is refused, not
    // turned into a link that leaves the app.
    [Theory]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("https://evil.example")]
    [InlineData("HTTPS://evil.example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("mailto:a@b.example")]
    [InlineData("articles")]
    [InlineData("?page=2")]
    [InlineData("#top")]
    [InlineData("")]
    [InlineData(" /x")]
    [InlineData("/\t/evil.example")]
    [InlineData("/x\n")]
    public void Url_RefusesAnythingButAPathBelowTheMount(string path)
    {
        foreach (var prefix in new[] { "/help", "/" })
        {
            var ex = Assert.Throws<ArgumentException>(() => MountAt(prefix).Url(path));

            Assert.Equal("path", ex.ParamName);
        }
    }
}
