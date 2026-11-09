using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Netjes;
using Netjes.Examples.HelpCenter;

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

    [Fact]
    public async Task AnArticle_RendersInsideThePackagesCenterLayout_WithItsLayoutLoadersProps()
    {
        await using var app = new GuestbookRateLimitTests.BlogApp();
        var client = app.CreateClient();

        var (status, payload) = await PageAsync(client, "/help/articles/getting-started");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal($"{Package}/app/(center)/articles/[slug]/page.tsx", (string?)payload["page"]);
        Assert.Equal("/help/articles/[slug]", (string?)payload["route"]);
        Assert.Equal(["app/layout.tsx", $"{Package}/app/layout.tsx", $"{Package}/app/(center)/layout.tsx"], Layouts(payload));
        var center = payload["segments"]!.AsArray().Single(s => (string?)s!["id"] == "app/help/(center)")!;
        Assert.Contains(center["props"]!["articles"]!.AsArray(), a => (string?)a!["slug"] == "getting-started");
        Assert.Equal("Getting started", (string?)payload["props"]!["article"]!["title"]);
    }

    [Theory]
    [InlineData("/help/nope")]
    [InlineData("/HELP/nope")]
    [InlineData("/help/articles/nope")]
    public async Task AnUnknownUrlBelowTheMount_RendersThePackagesNotFound_InsideItsLayout(string url)
    {
        await using var app = new GuestbookRateLimitTests.BlogApp();
        var client = app.CreateClient();

        var (status, payload) = await PageAsync(client, url);

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal($"{Package}/app/not-found.tsx", (string?)payload["page"]);
        Assert.Equal(["app/layout.tsx", $"{Package}/app/layout.tsx"], Layouts(payload)[..2]);
    }

    [Theory]
    [InlineData("/nope")]
    [InlineData("/helpx/nope")]
    [InlineData("/help%2Fnope")]
    public async Task AnUnknownUrlOutsideTheMount_RendersTheBlogsNotFound(string url)
    {
        await using var app = new GuestbookRateLimitTests.BlogApp();
        var client = app.CreateClient();

        var (status, payload) = await PageAsync(client, url);

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("app/not-found.tsx", (string?)payload["page"]);
    }

    [Fact]
    public async Task ANote_PostedWithoutJavaScript_RedirectsToTheArticleBelowTheMount()
    {
        await using var app = new GuestbookRateLimitTests.BlogApp();
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        var (_, page) = await PageAsync(client, "/help/articles/privacy");
        var token = page["antiforgery"]!;

        var response = await client.PostAsync("/help/articles/privacy", new FormUrlEncodedContent([
            new("comment", "How long are backups kept?"),
            new((string)token["formFieldName"]!, (string)token["requestToken"]!),
        ]), Ct);
        var (_, after) = await PageAsync(client, response.Headers.Location!.OriginalString);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/help/articles/privacy?feedback=sent", response.Headers.Location?.OriginalString);
        Assert.True((bool?)after["props"]!["feedbackSent"]);
        Assert.Equal(1, (int?)after["props"]!["feedbackCount"]);
    }

    [Fact]
    public async Task ANote_PostedByTheClientRouter_GetsTheRedirectAsAPayload_AndAnEmptyOneIsRefused()
    {
        await using var app = new GuestbookRateLimitTests.BlogApp();
        var client = app.CreateClient();
        var (_, page) = await PageAsync(client, "/help/articles/your-account");
        var token = page["antiforgery"]!;

        async Task<(HttpStatusCode, JsonNode)> PostAsync(string comment)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/help/articles/your-account")
            {
                Content = new FormUrlEncodedContent([new("comment", comment)]),
            };
            request.Headers.Add(NetjesHeaders.Data, "1");
            request.Headers.Add((string)token["headerName"]!, (string)token["requestToken"]!);
            var response = await client.SendAsync(request, Ct);
            return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!);
        }

        var (emptyStatus, empty) = await PostAsync("  ");
        var (_, sent) = await PostAsync("Where do I change my email?");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, emptyStatus);
        Assert.Equal("Tell us what was missing.", (string?)empty["props"]!["errors"]!["comment"]);
        Assert.Equal("redirect", (string?)sent["kind"]);
        Assert.Equal("/help/articles/your-account?feedback=sent", (string?)sent["location"]);
    }

    [Fact]
    public async Task TheVoteServerAction_CountsTheAnswer_AndRefusesAnUnknownArticle()
    {
        await using var app = new GuestbookRateLimitTests.BlogApp();
        var client = app.CreateClient();
        var (_, page) = await PageAsync(client, "/help/articles/getting-started");
        var token = page["antiforgery"]!;

        async Task<(HttpStatusCode, JsonNode?)> VoteAsync(object input, string url = "/_netjes/actions/help.vote")
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(input) };
            request.Headers.Add(NetjesHeaders.Data, "1");
            request.Headers.Add((string)token["headerName"]!, (string)token["requestToken"]!);
            var response = await client.SendAsync(request, Ct);
            var body = await response.Content.ReadAsStringAsync(Ct);
            return (response.StatusCode, body.Length == 0 ? null : JsonNode.Parse(body));
        }

        var (status, votes) = await VoteAsync(new { slug = "getting-started", helpful = true });
        var (unknownStatus, unknown) = await VoteAsync(new { slug = "nope", helpful = true });
        // The client posts every server action to /_netjes/actions/{name}, so the mount's prefix stays out of its URL.
        var (belowStatus, _) = await VoteAsync(new { slug = "getting-started", helpful = true }, "/help/_netjes/actions/help.vote");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, (int?)votes!["result"]!["helpful"]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownStatus);
        Assert.Contains("slug", unknown!.ToJsonString(), StringComparison.Ordinal);
        Assert.NotEqual(HttpStatusCode.OK, belowStatus);
    }

    [Fact]
    public async Task TheMessagesEndpoint_IsBelowTheMount()
    {
        await using var app = new GuestbookRateLimitTests.BlogApp();
        var client = app.CreateClient();

        var sent = await client.PostAsJsonAsync("/help/messages", new { email = "reader@example.com", message = "Hello" }, Ct);
        var empty = await client.PostAsJsonAsync("/help/messages", new { email = "", message = "" }, Ct);
        var outside = await client.PostAsJsonAsync("/messages", new { email = "reader@example.com", message = "Hello" }, Ct);

        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.True((int?)(await sent.Content.ReadFromJsonAsync<JsonNode>(Ct))!["number"] >= 1);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, outside.StatusCode);
    }

    // A page action's or server action's body is limited by Netjes (ActionBodySizeLimit), an endpoint's by the server
    // alone, so the endpoint limits what it keeps.
    [Fact]
    public async Task TheMessagesEndpoint_RefusesAMessageOrAnAddressOverItsLimit()
    {
        await using var app = new GuestbookRateLimitTests.BlogApp();
        var client = app.CreateClient();
        const string email = "reader@example.com";

        var atTheLimit = await client.PostAsJsonAsync("/help/messages", new { email, message = new string('x', HelpCenterEndpoints.MaxMessageLength) }, Ct);
        var tooLong = await client.PostAsJsonAsync("/help/messages", new { email, message = new string('x', HelpCenterEndpoints.MaxMessageLength + 1) }, Ct);
        var longAddress = await client.PostAsJsonAsync("/help/messages", new { email = new string('a', HelpCenterEndpoints.MaxEmailLength - 11) + "@example.com", message = "Hello" }, Ct);

        Assert.Equal(HttpStatusCode.OK, atTheLimit.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, longAddress.StatusCode);
    }

    // The package's page action and server action map through its mount, and keep the antiforgery check and the
    // same-origin check of every page action and server action: a post from another site is refused, and nothing is kept.
    [Fact]
    public async Task ANote_WithoutTheAntiforgeryToken_OrFromAnotherSite_IsRefused_AndNotKept()
    {
        await using var app = new GuestbookRateLimitTests.BlogApp();
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        var (_, page) = await PageAsync(client, "/help/articles/privacy");
        var token = page["antiforgery"]!;
        FormUrlEncodedContent Note(bool withToken)
        {
            List<KeyValuePair<string, string>> fields = [new("comment", "Posted from another site.")];
            if (withToken)
            {
                fields.Add(new((string)token["formFieldName"]!, (string)token["requestToken"]!));
            }

            return new(fields);
        }

        var plain = await client.PostAsync("/help/articles/privacy", Note(withToken: false), Ct);
        var data = new HttpRequestMessage(HttpMethod.Post, "/help/articles/privacy") { Content = Note(withToken: false) };
        data.Headers.Add(NetjesHeaders.Data, "1");
        var byTheRouter = await client.SendAsync(data, Ct);
        var crossSite = new HttpRequestMessage(HttpMethod.Post, "/help/articles/privacy") { Content = Note(withToken: true) };
        crossSite.Headers.Add("Origin", "https://evil.example");
        var fromAnotherSite = await client.SendAsync(crossSite, Ct);
        var (_, after) = await PageAsync(client, "/help/articles/privacy");

        Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, byTheRouter.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, fromAnotherSite.StatusCode);
        Assert.Equal(0, (int?)after["props"]!["feedbackCount"]);
    }

    [Fact]
    public async Task TheVoteServerAction_WithoutTheAntiforgeryToken_OrFromAnotherSite_IsRefused_AndNotCounted()
    {
        await using var app = new GuestbookRateLimitTests.BlogApp();
        var client = app.CreateClient();
        var (_, page) = await PageAsync(client, "/help/articles/privacy");
        var token = page["antiforgery"]!;

        async Task<HttpStatusCode> VoteAsync(bool withToken, string? origin = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/_netjes/actions/help.vote") { Content = JsonContent.Create(new { slug = "privacy", helpful = true }) };
            request.Headers.Add(NetjesHeaders.Data, "1");
            if (withToken)
            {
                request.Headers.Add((string)token["headerName"]!, (string)token["requestToken"]!);
            }

            if (origin is not null)
            {
                request.Headers.Add("Origin", origin);
            }

            return (await client.SendAsync(request, Ct)).StatusCode;
        }

        var withoutToken = await VoteAsync(withToken: false);
        var fromAnotherSite = await VoteAsync(withToken: true, origin: "https://evil.example");
        var (_, after) = await PageAsync(client, "/help/articles/privacy");

        Assert.Equal(HttpStatusCode.BadRequest, withoutToken);
        Assert.Equal(HttpStatusCode.Forbidden, fromAnotherSite);
        Assert.Equal(0, (int?)after["props"]!["votes"]!["helpful"]);
    }

    // The endpoint takes no antiforgery token, since it binds its body from JSON only: a form on another site can send a
    // urlencoded, multipart or text/plain body without a preflight, never application/json.
    [Theory]
    [InlineData("application/x-www-form-urlencoded", "email=reader%40example.com&message=Hello")]
    [InlineData("text/plain", """{"email":"reader@example.com","message":"Hello"}""")]
    public async Task TheMessagesEndpoint_RefusesABodyAFormOnAnotherSiteCouldSend(string contentType, string body)
    {
        await using var app = new GuestbookRateLimitTests.BlogApp();
        var client = app.CreateClient();

        var response = await client.PostAsync("/help/messages", new StringContent(body, System.Text.Encoding.UTF8, contentType), Ct);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
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
