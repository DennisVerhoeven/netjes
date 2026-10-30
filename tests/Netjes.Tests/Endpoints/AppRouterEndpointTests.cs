using Microsoft.AspNetCore.Builder;
using System.Net;

namespace Netjes.Tests.Endpoints;

public class AppRouterEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Get_AppRouterPage_IsServedInsideItsLayouts()
    {
        await using var app = await TestApp.StartAsync(["app/layout.tsx", "app/blog/[slug]/page.tsx", "app/blog/[slug]/Card.tsx"]);

        var payload = await app.GetDataJsonAsync("/blog/hello");

        Assert.Equal("app/blog/[slug]/page.tsx", (string?)payload["page"]);
        Assert.Equal("/blog/[slug]", (string?)payload["route"]);
        Assert.Equal("hello", (string?)payload["params"]!["slug"]);
    }

    [Fact]
    public async Task Development_NewAppPage_IsRoutableWithoutRestart()
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

    [Fact]
    public async Task MapPageLoader_UnknownPattern_NamesTheAppFile()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/blog/[slug]", () => Page.Props())));

        Assert.Contains("app/blog/[slug]/page.tsx", ex.Message);
        Assert.DoesNotContain("pages/", ex.Message);
    }
}
