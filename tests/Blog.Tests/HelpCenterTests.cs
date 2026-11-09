using System.Net;
using System.Text.Json.Nodes;
using Netjes;

namespace Blog.Tests;

/// <summary>
/// The help center page package (examples/HelpCenter), which the Blog mounts at /help: its pages inside the Blog's
/// root layout and the package's own, its loaders, page action, server action and endpoint, and its 404.
/// </summary>
public class HelpCenterTests
{
    private const string Package = "@netjes-examples/help-center";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheHelpCentersHome_RendersAtHelp_InsideTheBlogsLayoutAndThePackages()
    {
        await using var app = new GuestbookRateLimitTests.BlogApp();
        var client = app.CreateClient();

        var (status, payload) = await PageAsync(client, "/help");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal($"{Package}/app/page.tsx", (string?)payload["page"]);
        Assert.Equal(["app/layout.tsx", $"{Package}/app/layout.tsx"], Layouts(payload));
        Assert.Equal("/help", (string?)payload["mounts"]![Package]);
        Assert.NotEmpty(payload["props"]!["articles"]!.AsArray());
    }

    /// <summary>A client navigation: the page payload as JSON.</summary>
    private static async Task<(HttpStatusCode Status, JsonNode Payload)> PageAsync(HttpClient client, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(NetjesHeaders.Data, "1");
        var response = await client.SendAsync(request, Ct);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!);
    }

    /// <summary>The layout files around the page, outermost first.</summary>
    private static string[] Layouts(JsonNode payload) =>
        [.. payload["segments"]!.AsArray().Select(s => (string?)s!["layout"]).OfType<string>()];
}
