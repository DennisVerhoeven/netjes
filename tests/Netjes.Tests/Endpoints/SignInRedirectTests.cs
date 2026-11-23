using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Netjes.Tests.Endpoints;

/// <summary>
/// <c>Page.RedirectToSignIn</c> in cookie authentication's <c>OnRedirectToLogin</c>: a page goes to the sign-in page with
/// its URL to come back to, a document by a redirect and a client navigation by a redirect payload; a server action call
/// gets an empty 401.
/// </summary>
public class SignInRedirectTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Files = ["app/layout.tsx", "app/page.tsx", "app/account/page.tsx", "app/sign-in/page.tsx"];

    private static Task<TestApp> StartAsync(
        string signIn = "/sign-in", string? returnUrlParameter = null, string? pathBase = null, bool fallbackPolicy = false) =>
        TestApp.StartAsync(
            Files,
            map: a =>
            {
                a.MapPageLoader("/account", (string? tab) => Page.Props(new { Tab = tab })).RequireAuthorization();
                a.MapPageAction("/account", ([FromForm] string? name) => Page.Redirect("/account")).RequireAuthorization();
                a.MapAction("account.save", ([FromForm] string? name) => ServerAction.Ok()).RequireAuthorization();
                if (fallbackPolicy)
                {
                    a.MapPageLoader("/sign-in", () => Page.Props()).AllowAnonymous();
                }
            },
            services: s =>
            {
                s.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(cookie =>
                    cookie.Events.OnRedirectToLogin = context => returnUrlParameter is null
                        ? Page.RedirectToSignIn(context.HttpContext, signIn)
                        : Page.RedirectToSignIn(context.HttpContext, signIn, returnUrlParameter));
                var authorization = s.AddAuthorizationBuilder();
                if (fallbackPolicy)
                {
                    authorization.SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
                }
            },
            configureApp: a =>
            {
                a.UseAuthentication();
                a.UseAuthorization();
            },
            pathBase: pathBase);

    private static async Task<JsonNode> JsonOf(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;

    [Fact]
    public async Task Document_GoesToTheSignInPage_WithItsUrlToComeBackTo()
    {
        await using var app = await StartAsync();

        var response = await app.GetDocumentAsync("/account?tab=orders");

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal("/sign-in?returnUrl=%2Faccount%3Ftab%3Dorders", response.Headers.Location?.OriginalString);
        Assert.Equal("no-store, private", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task ClientNavigation_GetsARedirectPayload_WithItsUrlToComeBackTo()
    {
        await using var app = await StartAsync();

        var response = await app.GetDataAsync("/account?tab=orders");
        var payload = await JsonOf(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("redirect", (string?)payload["kind"]);
        Assert.Equal("/sign-in?returnUrl=%2Faccount%3Ftab%3Dorders", (string?)payload["location"]);
        Assert.Equal("no-store, private", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData(false, HttpStatusCode.SeeOther)]
    [InlineData(true, HttpStatusCode.OK)]
    public async Task PageActionPost_GoesToTheSignInPage_ComingBackToThePage(bool javaScript, HttpStatusCode status)
    {
        await using var app = await StartAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, "/account?tab=orders")
        {
            Content = new FormUrlEncodedContent([new("name", "Ada")]),
        };
        if (javaScript)
        {
            request.Headers.Add(NetjesHeaders.Data, "1");
        }

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(status, response.StatusCode);
        var location = javaScript ? (string?)(await JsonOf(response))["location"] : response.Headers.Location?.OriginalString;
        Assert.Equal("/sign-in?returnUrl=%2Faccount%3Ftab%3Dorders", location);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ServerActionCall_GetsA401_AndNoRedirect(bool javaScript)
    {
        await using var app = await StartAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, "/_netjes/actions/account.save")
        {
            Content = new FormUrlEncodedContent([new("name", "Ada")]),
        };
        if (javaScript)
        {
            request.Headers.Add(NetjesHeaders.Data, "1");
        }

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        if (javaScript)
        {
            var answer = await JsonOf(response);
            Assert.Equal("action", (string?)answer["kind"]);
            Assert.Equal(401, (int?)answer["status"]);
        }
    }

    [Theory]
    [InlineData("/_netjes/actions/account.save")]
    [InlineData("/_NETJES/actions/account.save")]
    public async Task ServerActionCall_InAnyCase_GetsA401(string url)
    {
        await using var app = await StartAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent([new("name", "Ada")]) };
        request.Headers.Add(NetjesHeaders.Data, "1");

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnderABasePath_TheSignInPageHasItAndTheUrlToComeBackToDoesNot(bool javaScript)
    {
        await using var app = await StartAsync(pathBase: "/docs");

        var response = javaScript ? await app.GetDataAsync("/docs/account") : await app.GetDocumentAsync("/docs/account");

        var location = javaScript ? (string?)(await JsonOf(response))["location"] : response.Headers.Location?.OriginalString;
        Assert.Equal("/docs/sign-in?returnUrl=%2Faccount", location);
    }

    [Fact]
    public async Task ASignInPageWithAQuery_KeepsIt_AndAnotherParameterNameCarriesTheUrl()
    {
        await using var app = await StartAsync(signIn: "/sign-in?as=staff", returnUrlParameter: "next");

        var response = await app.GetDocumentAsync("/account");

        Assert.Equal("/sign-in?as=staff&next=%2Faccount", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task AnEncodedPath_ComesBackEncoded()
    {
        // Decoded, %3F would end the path, and %23 would turn the rest into a fragment.
        await using var app = await StartAsync(fallbackPolicy: true);

        var response = await app.GetDocumentAsync("/a%3Fb%23c%20d?x=%2F");

        Assert.Equal("/sign-in?returnUrl=%2Fa%253Fb%2523c%2520d%3Fx%3D%252F", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("//elsewhere.example/account")]
    [InlineData("//elsewhere.example")]
    public async Task APathThatWouldLeaveTheSite_IsNoUrlToComeBackTo(string path)
    {
        // A request target that starts with "//" reaches the app as such; as a return URL it would lead to another site.
        await using var app = await StartAsync(fallbackPolicy: true);

        var response = await app.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(app.Client.BaseAddress!.GetLeftPart(UriPartial.Authority) + path)), Ct);

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal("/sign-in", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task ABackslashInThePath_ComesBackEncoded_AsALocalUrl()
    {
        // Browsers read "/\host" as "//host"; encoded, it stays a path on this site.
        await using var app = await StartAsync(fallbackPolicy: true);

        var response = await app.GetDocumentAsync("/%5Celsewhere.example");

        var location = response.Headers.Location?.OriginalString;
        Assert.Equal("/sign-in?returnUrl=%2F%255Celsewhere.example", location);
        Assert.True(Page.IsLocalUrl(Uri.UnescapeDataString(location!.Split("returnUrl=")[1])));
    }

    [Theory]
    [InlineData("https://elsewhere.example/sign-in")]
    [InlineData("//elsewhere.example/sign-in")]
    [InlineData("/\\elsewhere.example/sign-in")]
    [InlineData("sign-in")]
    [InlineData("")]
    public void ASignInPageOffTheSite_IsRefused(string path)
    {
        // Thrown as the event is called, before anything is written.
        Assert.Throws<ArgumentException>(() => { _ = Page.RedirectToSignIn(new DefaultHttpContext(), path); });
    }

    [Fact]
    public void AnEmptyParameterName_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => { _ = Page.RedirectToSignIn(new DefaultHttpContext(), "/sign-in", ""); });
    }
}
