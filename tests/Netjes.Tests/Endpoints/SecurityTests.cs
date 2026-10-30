using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Netjes.Tests.Endpoints;

public class SecurityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("javascript:alert(document.cookie)")]
    [InlineData(" JavaScript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:msgbox(1)")]
    public void Redirect_ToAScriptOrDataUrl_IsRejected(string location)
    {
        Assert.Throws<ArgumentException>(() => Page.Redirect(location));
    }

    [Theory]
    [InlineData("javascript%3Aalert(1)")]
    [InlineData("java%73cript:alert(1)")]
    [InlineData("%6Aavascript:alert(1)")]
    [InlineData(" javascript%3aalert(1)")]
    public void Redirect_WithAPercentBeforeTheFirstColonOrSlash_IsRejected(string location)
    {
        // Anything that decodes the URL once (a proxy, a CMS, an old export) would turn it into a scheme.
        Assert.Throws<ArgumentException>(() => Page.Redirect(location));
    }

    [Theory]
    [InlineData("/login?returnUrl=%2Fadmin")]
    [InlineData("https://example.com/docs")]
    [InlineData("../sibling")]
    [InlineData("?page=2")]
    [InlineData("?q=a%20b")]
    [InlineData("#see%20also")]
    [InlineData("./caf%C3%A9")]
    public void Redirect_ToRelativeOrHttpUrls_IsAllowed(string location)
    {
        Page.Redirect(location);
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/")]
    [InlineData("/\\evil.example/")]
    [InlineData("javascript:alert(1)")]
    public void LocalRedirect_ToAnotherSite_IsRejected(string location)
    {
        Assert.Throws<ArgumentException>(() => Page.LocalRedirect(location));
    }

    [Fact]
    public async Task LocalRedirect_ToALocalPath_Redirects()
    {
        await using var app = await TestApp.StartAsync(
            ["app/login/page.tsx"],
            map: a => a.MapPageLoader("/login", (string returnUrl) => Page.LocalRedirect(returnUrl)));

        var response = await app.GetDocumentAsync("/login?returnUrl=%2Fadmin%3Ftab%3D1");

        Assert.Equal("/admin?tab=1", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task PageAction_WithoutFormBinding_StillRequiresAnAntiforgeryToken()
    {
        var deleted = false;
        await using var app = await TestApp.StartAsync(
            ["app/posts/[slug]/page.tsx"],
            map: a => a.MapPageAction("/posts/[slug]", (string slug) =>
            {
                deleted = true;
                return Page.Redirect("/posts");
            }));

        var response = await app.Client.PostAsync("/posts/hello", new FormUrlEncodedContent([]), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(deleted);
    }

    [Fact]
    public async Task PageAction_PlainTextCrossSitePost_IsRejected()
    {
        var deleted = false;
        await using var app = await TestApp.StartAsync(
            ["app/posts/[slug]/page.tsx"],
            map: a => a.MapPageAction("/posts/[slug]", (string slug) =>
            {
                deleted = true;
                return Page.Redirect("/posts");
            }));

        // text/plain is a "simple" cross-site request: no CORS preflight, and not a form the middleware checks.
        var response = await app.Client.PostAsync("/posts/hello", new StringContent("x"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(deleted);
    }

    [Fact]
    public async Task PageAction_WithAntiforgeryDisabled_AcceptsPostsWithoutAToken()
    {
        await using var app = await TestApp.StartAsync(
            ["app/webhook/page.tsx"],
            map: a => a.MapPageAction("/webhook", () => Page.Redirect("/")).DisableAntiforgery());

        var response = await app.Client.PostAsync("/webhook", new StringContent("x"), Ct);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
    }

    [Fact]
    public async Task PageAction_WithTheTokenFromThePagePayload_Succeeds()
    {
        await using var app = await TestApp.StartAsync(
            ["app/posts/[slug]/page.tsx"],
            map: a => a.MapPageAction("/posts/[slug]", (string slug) => Page.Redirect("/posts")));
        var page = await app.GetDataAsync("/posts/hello");
        var antiforgery = JsonNode.Parse(await page.Content.ReadAsStringAsync(Ct))!["antiforgery"]!;
        var request = new HttpRequestMessage(HttpMethod.Post, "/posts/hello")
        {
            Content = new FormUrlEncodedContent([
                new((string)antiforgery["formFieldName"]!, (string)antiforgery["requestToken"]!),
            ]),
        };
        request.Headers.Add("Cookie", page.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
    }

    private static async Task<HttpRequestMessage> PostWithAValidTokenAsync(TestApp app, string page)
    {
        var response = await app.GetDataAsync(page);
        var antiforgery = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["antiforgery"]!;
        var request = new HttpRequestMessage(HttpMethod.Post, page)
        {
            Content = new FormUrlEncodedContent([
                new((string)antiforgery["formFieldName"]!, (string)antiforgery["requestToken"]!),
            ]),
        };
        request.Headers.Add("Cookie", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        return request;
    }

    [Fact]
    public async Task PageAction_FromASiblingOrigin_IsForbiddenEvenWithAValidToken()
    {
        // A sibling subdomain can plant its own antiforgery cookie (cookie tossing); the origin still gives it away.
        var posted = false;
        await using var app = await TestApp.StartAsync(
            ["app/guestbook/page.tsx"],
            map: a => a.MapPageAction("/guestbook", () =>
            {
                posted = true;
                return Page.Redirect("/guestbook");
            }));
        var request = await PostWithAValidTokenAsync(app, "/guestbook");
        request.Headers.Add("Origin", "https://evil.localhost");
        request.Headers.Add("Sec-Fetch-Site", "same-site");

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(posted);
    }

    [Fact]
    public async Task PageAction_WithAntiforgeryDisabled_AcceptsPostsFromOtherSites()
    {
        // A payment or identity provider posting a form back (3-D Secure, SAML) comes from its own site.
        await using var app = await TestApp.StartAsync(
            ["app/payment-return/page.tsx"],
            map: a => a.MapPageAction("/payment-return", () => Page.Redirect("/")).DisableAntiforgery());
        var request = new HttpRequestMessage(HttpMethod.Post, "/payment-return") { Content = new FormUrlEncodedContent([new("status", "paid")]) };
        request.Headers.Add("Origin", "https://bank.example");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
    }

    [Fact]
    public async Task PageAction_InvalidToken_IsAClientErrorNotAServerError()
    {
        await using var app = await TestApp.StartAsync(
            ["app/contact/page.tsx"],
            map: a => a.MapPageAction("/contact", ([FromForm] string name) => Page.Redirect("/thanks")));

        var response = await app.Client.PostAsync(
            "/contact", new FormUrlEncodedContent([new("name", "Ada"), new("__RequestVerificationToken", "forged")]), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, (int?)app.Renderer.LastPayload["props"]!["status"]);
        Assert.DoesNotContain(app.Logs, entry => entry.Level >= Microsoft.Extensions.Logging.LogLevel.Error);
    }

    [Fact]
    public async Task PageAction_BadTokenInProduction_RendersTheErrorPageInsteadOfAnEmpty400()
    {
        // Outside Development, minimal APIs answer binding failures with an empty 400 instead of throwing.
        await using var app = await TestApp.StartAsync(
            ["app/error.tsx", "app/contact/page.tsx"],
            map: a => a.MapPageAction("/contact", ([FromForm] string name) => Page.Redirect("/thanks")),
            configure: o => o.Mode = NetjesMode.Development,
            environment: "Production");
        var request = new HttpRequestMessage(HttpMethod.Post, "/contact")
        {
            Content = new FormUrlEncodedContent([new("name", "Ada"), new("__RequestVerificationToken", "forged")]),
        };
        request.Headers.Add(NetjesHeaders.Data, "1");

        var response = await app.Client.SendAsync(request, Ct);
        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("app/error.tsx", (string?)payload["page"]);
        Assert.Equal(400, (int?)payload["props"]!["status"]);
    }

    [Fact]
    public async Task PageEndpoint_EmptyErrorStatusFromMiddleware_RendersTheErrorPage()
    {
        await using var app = await TestApp.StartAsync(
            ["app/error.tsx", "app/admin/page.tsx"],
            map: a => a.MapPageLoader("/admin", () => Page.Props()).AddEndpointFilter((context, next) =>
                ValueTask.FromResult<object?>(Results.StatusCode(StatusCodes.Status403Forbidden))));

        var response = await app.GetDocumentAsync("/admin");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(403, (int?)app.Renderer.LastPayload["props"]!["status"]);
    }

    /// <summary>
    /// The first visit stores the antiforgery cookie, and ASP.NET Core sends that response its own
    /// <c>no-cache, no-store</c>. A returning visitor's page reuses the cookie: no new one, and the pages' own default.
    /// </summary>
    [Fact]
    public async Task ReturningVisitor_GetsAWorkingTokenWithoutANewCookie()
    {
        await using var app = await TestApp.StartAsync(
            ["app/posts/[slug]/page.tsx"],
            map: a => a.MapPageAction("/posts/[slug]", (string slug) => Page.Redirect("/posts")));
        var first = await app.GetDataAsync("/posts/hello");
        var cookie = string.Join("; ", first.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));

        var again = new HttpRequestMessage(HttpMethod.Get, "/posts/hello");
        again.Headers.Add(NetjesHeaders.Data, "1");
        again.Headers.Add("Cookie", cookie);
        var second = await app.Client.SendAsync(again, Ct);
        var antiforgery = JsonNode.Parse(await second.Content.ReadAsStringAsync(Ct))!["antiforgery"]!;
        var post = new HttpRequestMessage(HttpMethod.Post, "/posts/hello")
        {
            Content = new FormUrlEncodedContent([
                new((string)antiforgery["formFieldName"]!, (string)antiforgery["requestToken"]!),
            ]),
        };
        post.Headers.Add("Cookie", cookie);
        var posted = await app.Client.SendAsync(post, Ct);

        Assert.Equal("no-store, private", second.Headers.CacheControl?.ToString());
        Assert.False(second.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.SeeOther, posted.StatusCode);
    }
}
