using System.Net;
using Blog.Features.Guestbook;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Netjes.Rendering;

namespace Blog.Tests;

public class GuestbookRateLimitTests
{
    [Fact]
    public async Task SigningTheGuestbook_IsLimitedPerClient()
    {
        await using var app = new BlogApp();
        var client = app.CreateClient();

        // No antiforgery token, so every post is refused with 400 until the limiter steps in with 429. The limiter
        // runs before antiforgery, so this proves the real app applies the policy to the real action.
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < GuestbookPage.SignsPerMinute + 3; i++)
        {
            statuses.Add((await client.PostAsync("/guestbook", content: null, TestContext.Current.CancellationToken)).StatusCode);
        }

        Assert.All(statuses.Take(GuestbookPage.SignsPerMinute), s => Assert.NotEqual(HttpStatusCode.TooManyRequests, s));
        Assert.All(statuses.Skip(GuestbookPage.SignsPerMinute), s => Assert.Equal(HttpStatusCode.TooManyRequests, s));
    }

    [Fact]
    public async Task TheLimit_CanBeRaisedByConfiguration_ForAnE2eServer()
    {
        await using var app = new BlogApp(signsPerMinute: "25");
        var client = app.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 27; i++)
        {
            statuses.Add((await client.PostAsync("/guestbook", content: null, TestContext.Current.CancellationToken)).StatusCode);
        }

        Assert.All(statuses.Take(25), s => Assert.NotEqual(HttpStatusCode.TooManyRequests, s));
        Assert.All(statuses.Skip(25), s => Assert.Equal(HttpStatusCode.TooManyRequests, s));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("many")]
    public async Task ANonsensicalConfiguredLimit_FallsBackToTheDefault(string configured)
    {
        await using var app = new BlogApp(signsPerMinute: configured);
        var client = app.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < GuestbookPage.SignsPerMinute + 1; i++)
        {
            statuses.Add((await client.PostAsync("/guestbook", content: null, TestContext.Current.CancellationToken)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.All(statuses.Take(GuestbookPage.SignsPerMinute), s => Assert.NotEqual(HttpStatusCode.TooManyRequests, s));
    }

    private const string Proxy = "192.0.2.10";

    /// <summary>Posts the guestbook form without a token (400 until the limiter says 429) as a client at <paramref name="from"/>.</summary>
    private static async Task<HttpStatusCode> PostAsync(HttpClient client, string from, string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/guestbook");
        request.Headers.Add(BlogApp.RemoteAddressHeader, from);
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return (await client.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode;
    }

    private static async Task UseUpAsync(HttpClient client, Func<int, (string From, string? ForwardedFor)> clientOf)
    {
        for (var i = 0; i < GuestbookPage.SignsPerMinute; i++)
        {
            var (from, forwardedFor) = clientOf(i);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, await PostAsync(client, from, forwardedFor));
        }
    }

    [Fact]
    public async Task IPv6AddressesInOne64_ShareTheirLimit()
    {
        await using var app = new BlogApp();
        var client = app.CreateClient();

        await UseUpAsync(client, i => ($"2001:db8:0:7::{i + 1}", null));

        Assert.Equal(HttpStatusCode.TooManyRequests, await PostAsync(client, "2001:db8:0:7:ffff::9"));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await PostAsync(client, "2001:db8:0:8::1"));
    }

    [Fact]
    public async Task AnIPv4AddressInItsIPv6Form_IsTheSameClient()
    {
        await using var app = new BlogApp();
        var client = app.CreateClient();

        await UseUpAsync(client, _ => ("198.51.100.9", null));

        Assert.Equal(HttpStatusCode.TooManyRequests, await PostAsync(client, "::ffff:198.51.100.9"));
    }

    [Fact]
    public async Task BehindAConfiguredProxy_TheVisitorsAddressIsLimited_NotTheProxys()
    {
        await using var app = new BlogApp(proxyNetwork: "192.0.2.0/24");
        var client = app.CreateClient();

        await UseUpAsync(client, _ => (Proxy, "203.0.113.60"));

        Assert.Equal(HttpStatusCode.TooManyRequests, await PostAsync(client, Proxy, "203.0.113.60"));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await PostAsync(client, Proxy, "203.0.113.61"));
    }

    [Fact]
    public async Task WithNoProxyConfigured_ForwardedForIsIgnored()
    {
        await using var app = new BlogApp();
        var client = app.CreateClient();

        // A visitor cannot get a fresh allowance by inventing an X-Forwarded-For on every post.
        await UseUpAsync(client, i => ("198.51.100.7", $"203.0.113.{i + 1}"));

        Assert.Equal(HttpStatusCode.TooManyRequests, await PostAsync(client, "198.51.100.7", "203.0.113.200"));
    }

    /// <summary>The real Blog with server rendering faked, so no Node or Vite is started.</summary>
    internal sealed class BlogApp(string? proxyNetwork = null, string? signsPerMinute = null) : WebApplicationFactory<Program>
    {
        /// <summary>TestServer requests have no client address; one in this header becomes the connection's.</summary>
        public const string RemoteAddressHeader = "X-Test-Remote-Address";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            if (proxyNetwork is not null)
            {
                builder.UseSetting("ForwardedHeaders:KnownNetworks:0", proxyNetwork);
            }

            if (signsPerMinute is not null)
            {
                builder.UseSetting("Guestbook:SignsPerMinute", signsPerMinute);
            }

            builder.ConfigureServices(services => services.AddTransient<IStartupFilter, RemoteAddressFromHeader>());
            builder.UseEnvironment("Testing");
            builder.UseContentRoot(ExampleRoot);
            builder.UseSetting("Netjes:Mode", "Development");
            builder.ConfigureServices(services => services.AddSingleton<ISsrRenderer>(new FakeRenderer()));
        }

        internal static string ExampleRoot
        {
            get
            {
                for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                {
                    var candidate = Path.Combine(dir.FullName, "examples", "Blog");
                    if (File.Exists(Path.Combine(candidate, "Blog.csproj")))
                    {
                        return candidate;
                    }
                }

                throw new InvalidOperationException("examples/Blog not found above the test output directory.");
            }
        }
    }

    private sealed class FakeRenderer : ISsrRenderer
    {
        public Task<string> RenderAsync(string payloadJson, CancellationToken cancellationToken) =>
            Task.FromResult("<!DOCTYPE html><html><body>rendered</body></html>");
    }

    /// <summary>Sets the connection's remote address from <see cref="BlogApp.RemoteAddressHeader"/>, before the app's middleware.</summary>
    private sealed class RemoteAddressFromHeader : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((http, nextMiddleware) =>
            {
                if (IPAddress.TryParse(http.Request.Headers[BlogApp.RemoteAddressHeader].ToString(), out var address))
                {
                    http.Connection.RemoteIpAddress = address;
                }

                return nextMiddleware(http);
            });
            next(app);
        };
    }
}
