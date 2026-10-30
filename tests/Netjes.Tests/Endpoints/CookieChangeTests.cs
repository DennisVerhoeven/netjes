using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Netjes.Tests.Endpoints;

/// <summary>
/// <c>X-Netjes-Cookies: changed</c>: a client call whose response sets or deletes a cookie (signing in or out). Script
/// cannot read Set-Cookie, so the server says so, and the client router stops showing the pages it kept for back and
/// forward, which showed whoever was signed in before.
/// </summary>
public class CookieChangeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Token(string Cookie, string Header, string Value);

    private static async Task<Token> TokenAsync(TestApp app)
    {
        var response = await app.GetDataAsync("/");
        var antiforgery = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["antiforgery"]!;
        return new Token(
            string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])),
            (string)antiforgery["headerName"]!,
            (string)antiforgery["requestToken"]!);
    }

    private static HttpRequestMessage Call(string action, Token? token, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/_netjes/actions/{action}") { Content = JsonContent.Create(new { }) };
        request.Headers.Add(NetjesHeaders.Data, "1");
        if (token is not null)
        {
            request.Headers.Add(token.Header, token.Value);
        }

        var cookies = string.Join("; ", new[] { token?.Cookie, cookie }.Where(c => !string.IsNullOrEmpty(c)));
        if (cookies.Length > 0)
        {
            request.Headers.Add("Cookie", cookies);
        }

        return request;
    }

    private static string? Marker(HttpResponseMessage response) =>
        response.Headers.TryGetValues(NetjesHeaders.Cookies, out var values) ? string.Join(",", values) : null;

    private static IEnumerable<string> SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];

    private static string AntiforgeryCookieName(TestApp app) =>
        app.App.Services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value.Cookie.Name!;

    [Theory]
    [InlineData("set", "changed")]
    [InlineData("delete", "changed")]
    [InlineData("none", null)]
    public async Task ACallsResponse_IsMarked_WhenTheActionSetsOrDeletesACookie(string action, string? expected)
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a =>
            {
                a.MapAction("set", (HttpContext http) =>
                {
                    http.Response.Cookies.Append("session", "second-user");
                    return ServerAction.Redirect("/");
                });
                a.MapAction("delete", (HttpContext http) =>
                {
                    http.Response.Cookies.Delete("session");
                    return ServerAction.Redirect("/");
                });
                a.MapAction("none", () => ServerAction.Ok("done"));
            });
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(Call(action, token), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, Marker(response));
    }

    [Fact]
    public async Task SigningInAndOut_WithCookieAuthentication_AreMarked()
    {
        // Antiforgery is beside the point here: its tokens are bound to the user, which these calls change.
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a =>
            {
                a.MapAction("signIn", async (HttpContext http) =>
                {
                    await http.SignInAsync(Principal("ada"));
                    return ServerAction.Redirect("/");
                }).DisableAntiforgery();
                a.MapAction("signOut", async (HttpContext http) =>
                {
                    await http.SignOutAsync();
                    return ServerAction.Redirect("/");
                }).DisableAntiforgery();
            },
            services: s => s.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(),
            configureApp: a => a.UseAuthentication());

        var signIn = await app.Client.SendAsync(Call("signIn", token: null), Ct);
        var auth = SetCookies(signIn).Single().Split(';')[0];
        var signOut = await app.Client.SendAsync(Call("signOut", token: null, cookie: auth), Ct);

        Assert.Equal("changed", Marker(signIn));
        Assert.Contains(SetCookies(signOut), c => c.StartsWith(".AspNetCore.Cookies=;", StringComparison.Ordinal));
        Assert.Equal("changed", Marker(signOut));
    }

    [Fact]
    public async Task TheAntiforgeryCookieAlone_DoesNotMarkTheResponse()
    {
        // It names no user, and the page payloads it comes with need it: a call without one stores it.
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapAction("token", (HttpContext http, IAntiforgery antiforgery) =>
            {
                antiforgery.GetAndStoreTokens(http);
                return ServerAction.Ok();
            }).DisableAntiforgery());

        var response = await app.Client.SendAsync(Call("token", token: null), Ct);

        Assert.Contains(SetCookies(response), c => c.StartsWith(AntiforgeryCookieName(app) + "=", StringComparison.Ordinal));
        Assert.Null(Marker(response));
    }

    [Theory]
    [InlineData("{0}x")]
    [InlineData("x{0}")]
    [InlineData("{0}.extra")]
    [InlineData("\u00A0{0}")] // a no-break space, which browsers keep: only spaces and tabs are trimmed
    public async Task ACookieNamedLikeTheAntiforgeryCookie_MarksTheResponse(string format)
    {
        // Cookie names are case-sensitive and only spaces and tabs around them are dropped: these are other cookies.
        string? name = null;
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapAction("set", (HttpContext http) =>
            {
                http.Response.Headers.Append("Set-Cookie", $"{name}=v; path=/");
                return ServerAction.Ok();
            }));
        name = string.Format(System.Globalization.CultureInfo.InvariantCulture, format, AntiforgeryCookieName(app));
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(Call("set", token), Ct);

        Assert.Equal("changed", Marker(response));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACookieNamedInAnotherCase_MarksTheResponse(bool upper)
    {
        string? name = null;
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapAction("set", (HttpContext http) =>
            {
                http.Response.Cookies.Append(name!, "v");
                return ServerAction.Ok();
            }));
        var antiforgery = AntiforgeryCookieName(app);
        name = upper ? antiforgery.ToUpperInvariant() : antiforgery.ToLowerInvariant();
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(Call("set", token), Ct);

        Assert.Equal("changed", Marker(response));
    }

    [Fact]
    public async Task TheAntiforgeryCookieWithAnother_MarksTheResponse()
    {
        // A sign-in that also hands out a token for the new user.
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapAction("signIn", (HttpContext http, IAntiforgery antiforgery) =>
            {
                http.Response.Cookies.Append("session", "ada");
                antiforgery.GetAndStoreTokens(http);
                return ServerAction.Ok();
            }).DisableAntiforgery());

        var response = await app.Client.SendAsync(Call("signIn", token: null), Ct);

        Assert.Equal(2, SetCookies(response).Count());
        Assert.Equal("changed", Marker(response));
    }

    [Fact]
    public async Task ACookieTheActionSetsAsTheResponseStarts_MarksIt()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapAction("late", (HttpContext http) =>
            {
                http.Response.OnStarting(() =>
                {
                    http.Response.Cookies.Append("session", "ada");
                    return Task.CompletedTask;
                });
                return ServerAction.Ok();
            }));
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(Call("late", token), Ct);

        Assert.Equal("changed", Marker(response));
    }

    [Fact]
    public async Task ACallThatFailsAfterSettingACookie_IsMarked()
    {
        // The failure goes out with the cookie, and the browser stores it all the same.
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapAction("boom", ServerActionResult (HttpContext http) =>
            {
                http.Response.Cookies.Append("session", "ada");
                throw new InvalidOperationException("db down");
            }));
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(Call("boom", token), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Single(SetCookies(response));
        Assert.Equal("changed", Marker(response));
    }

    [Fact]
    public async Task ACookieAMiddlewareSetBeforeTheAction_DoesNotMarkIt()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapAction("none", () => ServerAction.Ok()),
            configureApp: a => a.Use(async (http, next) =>
            {
                if (http.Request.Path.StartsWithSegments("/_netjes/actions"))
                {
                    http.Response.Cookies.Append("visitor", "1");
                }

                await next(http);
            }));
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(Call("none", token), Ct);

        Assert.Single(SetCookies(response));
        Assert.Null(Marker(response));
    }

    [Fact]
    public async Task ASlidingRenewalOfTheAuthenticationCookie_DoesNotMarkTheCall()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-29T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a =>
            {
                a.MapGet("/sign-in", (HttpContext http) => http.SignInAsync(Principal("ada")));
                a.MapAction("none", () => ServerAction.Ok()).DisableAntiforgery();
            },
            services: s => s.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
            {
                o.ExpireTimeSpan = TimeSpan.FromMinutes(10);
                o.SlidingExpiration = true;
                o.TimeProvider = clock;
            }),
            configureApp: a => a.UseAuthentication());
        var auth = SetCookies(await app.Client.GetAsync("/sign-in", Ct)).Single().Split(';')[0];
        clock.Now += TimeSpan.FromMinutes(6);

        var response = await app.Client.SendAsync(Call("none", token: null, cookie: auth), Ct);

        Assert.Contains(SetCookies(response), c => c.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal));
        Assert.Null(Marker(response));
    }

    [Theory]
    [InlineData(true, "changed")]
    [InlineData(false, null)]
    public async Task APageActionCall_IsMarked_WhenItSetsACookie(bool setsCookie, string? expected)
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx", "app/login/page.tsx"],
            map: a => a.MapPageAction("/login", (HttpContext http) =>
            {
                if (setsCookie)
                {
                    http.Response.Cookies.Append("session", "ada");
                }

                return Page.Redirect("/");
            }));
        var token = await TokenAsync(app);
        var request = new HttpRequestMessage(HttpMethod.Post, "/login") { Content = new FormUrlEncodedContent([]) };
        request.Headers.Add(NetjesHeaders.Data, "1");
        request.Headers.Add(token.Header, token.Value);
        request.Headers.Add("Cookie", token.Cookie);

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal("redirect", (string?)JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["kind"]);
        Assert.Equal(expected, Marker(response));
    }

    [Fact]
    public async Task APostWithoutJavaScript_IsNotMarked()
    {
        // The browser loads whatever comes next as a document; only the client router reads the header.
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapAction("set", ([FromForm] string name, HttpContext http) =>
            {
                http.Response.Cookies.Append("session", name);
                return ServerAction.Redirect("/");
            }));
        var token = await TokenAsync(app);
        var request = new HttpRequestMessage(HttpMethod.Post, "/_netjes/actions/set")
        {
            Content = new FormUrlEncodedContent([new("name", "ada"), new("__RequestVerificationToken", token.Value)]),
        };
        request.Headers.Add("Cookie", token.Cookie);

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Contains(SetCookies(response), c => c.StartsWith("session=", StringComparison.Ordinal));
        Assert.Null(Marker(response));
    }

    private static ClaimsPrincipal Principal(string name) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], CookieAuthenticationDefaults.AuthenticationScheme));

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
