using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Netjes;
using Netjes.Examples.HelpCenter;
using Netjes.Rendering;

namespace Blog.Tests;

/// <summary>
/// The help center mounted at a prefix the Blog does not use, one with a value, next to the Blog's own pages: the
/// package holds no URL of its own, so its pages, its page action's redirect, its endpoint and its 404 follow the prefix
/// the app picks.
/// </summary>
public class HelpCenterPrefixTests
{
    private const string Package = HelpCenterPackage.Name;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AtAPrefixWithAValue_APageAndItsMountFollowThePrefix()
    {
        await using var app = await StartAsync("/[tenant]/help");
        using var client = ClientOf(app);

        var (status, payload) = await PageAsync(client, "/acme/help/articles/privacy");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal($"{Package}/app/(center)/articles/[slug]/page.tsx", (string?)payload["page"]);
        Assert.Equal("/[tenant]/help/articles/[slug]", (string?)payload["route"]);
        Assert.Equal("/acme/help", (string?)payload["mounts"]![Package]);
    }

    [Fact]
    public async Task AtAPrefixWithAValue_ANoteRedirectsToTheArticleBelowThatValue()
    {
        await using var app = await StartAsync("/[tenant]/help");
        using var client = ClientOf(app);
        var (_, page) = await PageAsync(client, "/acme/help/articles/privacy");
        var token = page["antiforgery"]!;

        var response = await client.PostAsync("/acme/help/articles/privacy", new FormUrlEncodedContent([
            new("comment", "How long are backups kept?"),
            new((string)token["formFieldName"]!, (string)token["requestToken"]!),
        ]), Ct);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/acme/help/articles/privacy?feedback=sent", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task AtAPrefixWithAValue_TheEndpointAndTheNotFoundAreBelowThePrefix()
    {
        await using var app = await StartAsync("/[tenant]/help");
        using var client = ClientOf(app);

        var below = await client.PostAsJsonAsync("/acme/help/messages", new { email = "reader@example.com", message = "Hello" }, Ct);
        var atHelp = await client.PostAsJsonAsync("/help/messages", new { email = "reader@example.com", message = "Hello" }, Ct);
        var (status, notFound) = await PageAsync(client, "/acme/help/nope");

        Assert.Equal(HttpStatusCode.OK, below.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, atHelp.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal($"{Package}/app/not-found.tsx", (string?)notFound["page"]);
        Assert.Equal("/acme/help", (string?)notFound["mounts"]![Package]);
    }

    /// <summary>
    /// The Blog's pages and the help center at <paramref name="prefix"/>, routed from the files as in Development, with
    /// server rendering faked and no generated file written.
    /// </summary>
    private static async Task<WebApplication> StartAsync(string prefix)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ContentRootPath = GuestbookRateLimitTests.BlogApp.ExampleRoot,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddNetjes(options =>
        {
            options.Mode = NetjesMode.Development;
            options.TypesFile = null;
        });
        builder.Services.AddSingleton<ISsrRenderer>(new FakeRenderer());
        builder.Services.AddHelpCenter();

        var app = builder.Build();
        app.UseNetjes();
        app.UseAntiforgery();
        app.MapHelpCenter(prefix);
        app.MapPages();
        await app.StartAsync(Ct);
        return app;
    }

    /// <summary>A client that keeps cookies, as a browser does, for the antiforgery cookie, and follows no redirect.</summary>
    private static HttpClient ClientOf(WebApplication app)
    {
        var server = app.GetTestServer();
        return new HttpClient(new CookieContainerHandler { InnerHandler = server.CreateHandler() }) { BaseAddress = server.BaseAddress };
    }

    /// <summary>A client navigation: the page payload as JSON.</summary>
    private static async Task<(HttpStatusCode Status, JsonNode Payload)> PageAsync(HttpClient client, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(NetjesHeaders.Data, "1");
        var response = await client.SendAsync(request, Ct);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!);
    }

    private sealed class FakeRenderer : ISsrRenderer
    {
        public Task<string> RenderAsync(string payloadJson, CancellationToken cancellationToken) =>
            Task.FromResult("<!DOCTYPE html><html><body>rendered</body></html>");
    }
}
