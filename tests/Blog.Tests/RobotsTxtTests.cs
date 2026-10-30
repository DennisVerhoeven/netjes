using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Blog.Tests;

public class RobotsTxtTests
{
    [Fact]
    public async Task RobotsTxt_KeepsTheImageEndpointCrawlable_AndPointsAtTheSitemap()
    {
        await using var app = new GuestbookRateLimitTests.BlogApp();
        var client = app.CreateClient();

        var response = await client.GetAsync("/robots.txt", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Every <Image> is served from /_netjes/image, so crawlers must be able to fetch it.
        Assert.DoesNotContain("/_netjes/", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Disallow: /", body, StringComparison.Ordinal);
        Assert.Contains("Sitemap: ", body, StringComparison.Ordinal);
    }
}
