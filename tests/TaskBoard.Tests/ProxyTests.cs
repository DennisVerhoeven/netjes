using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Netjes;

namespace TaskBoard.Tests;

/// <summary>
/// In production TaskBoard sits behind a proxy that terminates TLS (compose.prod.yaml): the app sees plain http from the
/// proxy and learns the visitor's scheme and address from X-Forwarded-Proto and -For, believed from the proxy's network only.
/// </summary>
[Collection(TaskBoardCollection.Name)]
[Trait("Category", "Docker")]
public sealed class ProxyTests(TaskBoardApp app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Proxy = "192.0.2.10";

    /// <summary>
    /// Signs up over http from <paramref name="from"/>, which says the visitor used https unless it is null. Cookies
    /// are carried by hand: a jar would not send a Secure cookie back over http, but the proxy does.
    /// </summary>
    private async Task<HttpResponseMessage> SignUpThroughAsync(string? from)
    {
        var client = app.CreateClient(new() { BaseAddress = new Uri("http://taskboard.example"), AllowAutoRedirect = false, HandleCookies = false });
        void Forwarded(HttpRequestMessage request)
        {
            if (from is null)
            {
                return;
            }

            request.Headers.Add(TaskBoardApp.RemoteAddressHeader, from);
            request.Headers.Add("X-Forwarded-For", "203.0.113.50");
            request.Headers.Add("X-Forwarded-Proto", "https");
        }

        var tokenRequest = new HttpRequestMessage(HttpMethod.Get, "/_netjes/antiforgery");
        Forwarded(tokenRequest);
        var tokenResponse = await client.SendAsync(tokenRequest, Ct);
        var token = JsonNode.Parse(await tokenResponse.Content.ReadAsStringAsync(Ct))!;
        var register = new HttpRequestMessage(HttpMethod.Post, "/_netjes/actions/auth.register")
        {
            Content = new FormUrlEncodedContent([
                new("name", "Behind A Proxy"),
                new("email", $"user-{Guid.NewGuid():N}@taskboard.test"),
                new("password", "correct horse battery"),
            ]),
        };
        Forwarded(register);
        register.Headers.Add(NetjesHeaders.Data, "1");
        register.Headers.Add((string)token["headerName"]!, (string)token["requestToken"]!);
        register.Headers.Add("Cookie", string.Join("; ", tokenResponse.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])));
        var response = await client.SendAsync(register, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response;
    }

    private static string AuthCookie(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("taskboard.auth=", StringComparison.Ordinal));

    [Fact]
    public async Task BehindTheProxy_AnHttpsVisitor_GetsASecureAuthCookie_AndHsts()
    {
        var response = await SignUpThroughAsync(from: Proxy);

        Assert.Contains("; secure", AuthCookie(response), StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Headers.Contains("Strict-Transport-Security"), "the request should count as https");
    }

    [Fact]
    public async Task OverPlainHttp_TheAuthCookieIsSecureAnyway()
    {
        // A proxy that does not pass the scheme on, or a visitor on http: the cookie must still never travel unencrypted.
        var response = await SignUpThroughAsync(from: null);

        Assert.Contains("; secure", AuthCookie(response), StringComparison.OrdinalIgnoreCase);
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Theory]
    [InlineData(Proxy, HttpStatusCode.OK)]
    [InlineData("198.51.100.7", HttpStatusCode.TemporaryRedirect)]
    [InlineData("127.0.0.1", HttpStatusCode.TemporaryRedirect)]
    public async Task OnlyTheProxy_IsBelievedAboutHttps(string from, HttpStatusCode expected)
    {
        // With an https port to send them to, requests that arrive over http are redirected.
        await using var withHttpsPort = app.WithWebHostBuilder(b => b.UseSetting("HTTPS_PORT", "443"));
        var client = withHttpsPort.CreateClient(new() { BaseAddress = new Uri("http://taskboard.example"), AllowAutoRedirect = false });
        var request = new HttpRequestMessage(HttpMethod.Get, "/login");
        request.Headers.Add(TaskBoardApp.RemoteAddressHeader, from);
        request.Headers.Add("X-Forwarded-For", "203.0.113.50");
        request.Headers.Add("X-Forwarded-Proto", "https");

        var response = await client.SendAsync(request, Ct);

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.TemporaryRedirect)
        {
            Assert.Equal("https://taskboard.example/login", response.Headers.Location?.ToString());
        }
    }
}
