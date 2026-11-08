using Microsoft.AspNetCore.Routing;
using Netjes.Routing;

namespace Netjes.Tests.Endpoints;

/// <summary>
/// The URLs of a mounted page package: <see cref="PagePackageMount.Url"/> for a package's own services in C#, and the
/// <c>mounts</c> each page payload carries for its links in the browser.
/// </summary>
public class PagePackageMountTests
{
    private static PagePackageMount MountAt(string prefix) => new(TestPagePackage.Descriptor, MountPrefix.Parse(prefix));

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
