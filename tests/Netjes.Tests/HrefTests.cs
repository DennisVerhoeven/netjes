namespace Netjes.Tests;

/// <summary>Href holds a link a loader sends to the browser; C# checks its shape, the props parse checks its page.</summary>
public class HrefTests
{
    [Fact]
    public void Constructor_RefusesUrlsThatAreNotTheAppsOrAListedScheme()
    {
        // Browsers drop tabs and newlines anywhere in a URL, so "/\t/evil.example" is "//evil.example", another host.
        string[] refused =
        [
            "", "docs", "./docs", "../docs", "//evil.example", "/\\evil.example", "/\t/evil.example", "/\n/x", "/\r/x", "\t/x",
            " /x", "/x ", "/a\u0000b", "/a\u007Fb", "#top\u001F", "https://example.com/\r\n", "javascript:alert(1)",
            "JaVaScRiPt:alert(1)", "data:text/html,x", "ftp://x", "https//example.com",
        ];
        string[] accepted =
        [
            "/", "/docs/guides/caching#keys", "/search?q=a", "?page=2", "#top", "https://example.com/a", "HTTP://EXAMPLE.COM/",
            "Https://example.com", "mailto:team@example.com", "tel:+31201234567", "/a b",
        ];

        Assert.All(refused, value => Assert.Throws<ArgumentException>("value", () => new Href(value)));
        Assert.All(accepted, value => Assert.Equal(value, new Href(value).Value));
        Assert.Throws<ArgumentNullException>("value", () => new Href(null!));
    }

    [Fact]
    public void TryCreate_ChecksTheShape_NotThePages()
    {
        // No C# check knows the app's pages: an endpoint's file, or a path that already has the base path, has the shape.
        Assert.True(Href.TryCreate("/llms.txt", out var file));
        Assert.Equal("/llms.txt", file.Value);
        Assert.True(Href.TryCreate("/docs/blog/x", out var prefixed));
        Assert.Equal("/docs/blog/x", prefixed.Value);

        Assert.False(Href.TryCreate("javascript:x", out var script));
        Assert.Null(script);
        Assert.False(Href.TryCreate(null, out var none));
        Assert.Null(none);
    }
}
