using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Netjes.Tests.Endpoints;

/// <summary>
/// The client router's request for the URL a redirect sent it to (<c>X-Netjes-Redirected</c>): where no page answers
/// that URL, Netjes answers <c>{ "kind": "document" }</c> without running the endpoint, so the router loads the URL as
/// a document and the endpoint runs once, for that load. A page answers as it always does.
/// </summary>
public class RedirectTargetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Files = ["app/layout.tsx", "app/page.tsx", "app/files/page.tsx", "app/old/page.tsx"];

    /// <summary>How often the file endpoint ran.</summary>
    private sealed class Calls
    {
        public int Count;
    }

    private static Task<TestApp> StartAsync(Calls calls, string? pathBase = null, Action<WebApplication>? map = null) =>
        TestApp.StartAsync(
            Files,
            map: a =>
            {
                a.MapGet("/download/{name}", (string name) =>
                {
                    Interlocked.Increment(ref calls.Count);
                    return Results.File("%PDF-1.7"u8.ToArray(), "application/pdf", name);
                });
                a.MapPageAction("/files", ([FromForm] string name) => Page.Redirect($"/download/{name}"));
                a.MapPageLoader("/old", () => Page.Redirect("/files"));
                map?.Invoke(a);
            },
            pathBase: pathBase);

    private static Task<HttpResponseMessage> FollowAsync(TestApp app, string url, bool data = true, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        if (data)
        {
            request.Headers.Add(NetjesHeaders.Data, "1");
        }

        request.Headers.Add(NetjesHeaders.Redirected, "1");
        return app.Client.SendAsync(request, Ct);
    }

    private static async Task<JsonNode> JsonOf(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;

    [Fact]
    public async Task RedirectTarget_ThatNoPageAnswers_IsToldToLoadAsADocument_WithoutRunningTheEndpoint()
    {
        var calls = new Calls();
        await using var app = await StartAsync(calls);

        var response = await FollowAsync(app, "/download/report.pdf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("""{"kind":"document"}""", (await JsonOf(response)).ToJsonString());
        Assert.Equal(0, calls.Count);
        Assert.Equal("no-store, private", response.Headers.CacheControl?.ToString());
        Assert.Contains(NetjesHeaders.Redirected, response.Headers.Vary);
    }

    [Fact]
    public async Task PagePost_ThatRedirectsToAFile_RunsTheFileEndpointOnce_ForTheDocumentLoad()
    {
        var calls = new Calls();
        await using var app = await StartAsync(calls);
        var token = await AntiforgeryOf(app);

        // The router posts the form, follows the redirect payload with the header, and loads the URL it is told to.
        var post = new HttpRequestMessage(HttpMethod.Post, "/files")
        {
            Content = new FormUrlEncodedContent([new("name", "report.pdf"), new(token.Field, token.Value)]),
        };
        post.Headers.Add(NetjesHeaders.Data, "1");
        post.Headers.Add("Cookie", token.Cookie);
        var redirect = await JsonOf(await app.Client.SendAsync(post, Ct));
        Assert.Equal("redirect", (string?)redirect["kind"]);
        var follow = await FollowAsync(app, (string)redirect["location"]!);
        Assert.Equal("document", (string?)(await JsonOf(follow))["kind"]);
        var document = await app.GetDocumentAsync((string)redirect["location"]!);

        Assert.Equal("application/pdf", document.Content.Headers.ContentType?.MediaType);
        Assert.Equal(1, calls.Count);
    }

    [Fact]
    public async Task DocumentLoad_OfTheUrl_RunsTheEndpoint_EvenWithTheHeader()
    {
        var calls = new Calls();
        await using var app = await StartAsync(calls);

        var response = await FollowAsync(app, "/download/report.pdf", data: false);

        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(1, calls.Count);
    }

    [Fact]
    public async Task ClientNavigation_WithoutTheHeader_RunsTheEndpoint_AsBefore()
    {
        var calls = new Calls();
        await using var app = await StartAsync(calls);

        var response = await app.GetDataAsync("/download/report.pdf");

        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(1, calls.Count);
    }

    [Fact]
    public async Task RedirectTarget_ThatIsAPage_GetsItsPayload()
    {
        await using var app = await StartAsync(new Calls());

        var payload = await JsonOf(await FollowAsync(app, "/files"));

        Assert.Equal("page", (string?)payload["kind"]);
        Assert.Equal("/files", (string?)payload["route"]);
    }

    [Fact]
    public async Task RedirectTarget_ThatIsAPageWhoseLoaderRedirects_GetsTheRedirectPayload()
    {
        await using var app = await StartAsync(new Calls());

        var payload = await JsonOf(await FollowAsync(app, "/old"));

        Assert.Equal("redirect", (string?)payload["kind"]);
        Assert.Equal("/files", (string?)payload["location"]);
    }

    [Fact]
    public async Task RedirectTarget_ThatMatchesNoRoute_IsLoadedAsADocument()
    {
        await using var app = await StartAsync(new Calls());

        var payload = await JsonOf(await FollowAsync(app, "/nowhere/at/all"));

        Assert.Equal("document", (string?)payload["kind"]);
    }

    [Fact]
    public async Task RedirectTarget_ThatAnEndpointRendersAsAPage_IsLoadedAsADocument()
    {
        await using var app = await StartAsync(new Calls(), map: a => a.MapGet("/rendered", () => Page.Render("/files")));

        var payload = await JsonOf(await FollowAsync(app, "/rendered"));

        Assert.Equal("document", (string?)payload["kind"]);
    }

    [Fact]
    public async Task Post_WithTheHeader_GoesOnToItsEndpoint()
    {
        var calls = new Calls();
        await using var app = await StartAsync(calls, map: a => a.MapPost("/download/{name}", (string name) =>
        {
            Interlocked.Increment(ref calls.Count);
            return Results.Ok();
        }).DisableAntiforgery());

        var response = await FollowAsync(app, "/download/report.pdf", method: HttpMethod.Post);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, calls.Count);
    }

    [Fact]
    public async Task RedirectTarget_BelowABasePath_IsJudgedByItsRoute()
    {
        var calls = new Calls();
        await using var app = await StartAsync(calls, pathBase: "/base");

        var file = await JsonOf(await FollowAsync(app, "/base/download/report.pdf"));
        var page = await JsonOf(await FollowAsync(app, "/base/files"));

        Assert.Equal("document", (string?)file["kind"]);
        Assert.Equal("page", (string?)page["kind"]);
        Assert.Equal(0, calls.Count);
    }

    private sealed record Token(string Field, string Value, string Cookie);

    /// <summary>The page's antiforgery token and its cookie, as a document of the page hands them out.</summary>
    private static async Task<Token> AntiforgeryOf(TestApp app)
    {
        var response = await app.GetDataAsync("/files");
        var antiforgery = (await JsonOf(response))["antiforgery"]!;
        var cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        return new Token((string)antiforgery["formFieldName"]!, (string)antiforgery["requestToken"]!, cookie);
    }
}
