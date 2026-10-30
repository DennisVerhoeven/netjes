using System.Net;

namespace Netjes.Tests.Endpoints;

public class StreamingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Documents_AreStreamedFromTheRenderer()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"]);
        app.Renderer.OnStream = _ => ["<!DOCTYPE html><html><body>shell", "<p>later</p>", "</body></html>"];

        var response = await app.Client.GetAsync("/", HttpCompletionOption.ResponseHeadersRead, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("<!DOCTYPE html><html><body>shell<p>later</p></body></html>", body);
        Assert.False(Assert.Single(app.Renderer.StreamOptions).WaitForAll);
    }

    [Theory]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)")]
    [InlineData("Mozilla/5.0 (compatible; bingbot/2.0)")]
    [InlineData("facebookexternalhit/1.1")]
    public async Task Crawlers_GetTheCompleteDocument(string userAgent)
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"]);
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.UserAgent.ParseAdd(userAgent);

        await app.Client.SendAsync(request, Ct);

        Assert.True(Assert.Single(app.Renderer.StreamOptions).WaitForAll);
    }

    [Fact]
    public async Task Browsers_IncludingHeadlessOnes_AreStreamedTo()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"]);
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/140.0 Safari/537.36");

        await app.Client.SendAsync(request, Ct);

        Assert.False(Assert.Single(app.Renderer.StreamOptions).WaitForAll);
    }

    [Fact]
    public async Task StreamDocumentsOff_WaitsForEverything()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"], configure: o => o.StreamDocuments = false);

        await app.GetDocumentAsync("/");

        Assert.True(Assert.Single(app.Renderer.StreamOptions).WaitForAll);
    }
}
