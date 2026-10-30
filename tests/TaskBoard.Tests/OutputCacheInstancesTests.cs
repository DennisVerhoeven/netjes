using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Netjes;
using Netjes.Rendering;
using TaskBoard.Infrastructure;

namespace TaskBoard.Tests;

/// <summary>
/// Two instances of an app behind one load balancer, sharing one output-cache store (as Redis would) and one Data
/// Protection key ring. TaskBoard caches no page, so each instance is a test host built like it (TaskBoard's app
/// folder, cookie authentication, the signed-in user in shared props, the same middleware order) with one cached page:
/// the landing page, under a layout with a loader.
/// <list type="bullet">
///   <item>An entry stored by one instance is served by the other: documents and payloads, without cookies or tokens.</item>
///   <item>Revalidating on one instance (a server action, <see cref="IPageCache"/>) evicts the page for both.</item>
///   <item>A signed-in visitor is never served the anonymous entry, nor stored for others, on either instance.</item>
///   <item>Layout keys signed by one instance: what the other does with them, with and without a shared secret.</item>
/// </list>
/// </summary>
public sealed class OutputCacheInstancesTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string LayoutSegment = "app/(marketing)";

    private readonly DirectoryInfo _keys = Directory.CreateTempSubdirectory("taskboard-keys-");
    private readonly IOutputCacheStore _store = SharedStore();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _keys.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    /// <summary>ASP.NET Core's in-memory store, taken out of a container of its own so two hosts can share it.</summary>
    private static IOutputCacheStore SharedStore()
    {
        var services = new ServiceCollection().AddOptions().AddOutputCache().BuildServiceProvider();
        return services.GetRequiredService<IOutputCacheStore>();
    }

    private Task<Instance> StartAsync(string name, string? segmentKeySecret = null) => Instance.StartAsync(name, _store, _keys, segmentKeySecret);

    public sealed record LandingProps(string Instance, int Load);

    public sealed record LayoutProps(string Instance);

    private sealed class Instance : IAsyncDisposable
    {
        private int _pageLoads;

        private Instance(string name, WebApplication app)
        {
            Name = name;
            App = app;
        }

        public string Name { get; }
        public WebApplication App { get; }
        public FakeRenderer Renderer { get; } = new();
        public int PageLoads => Volatile.Read(ref _pageLoads);

        public static async Task<Instance> StartAsync(string name, IOutputCacheStore store, DirectoryInfo keys, string? segmentKeySecret)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Testing",
                ContentRootPath = TaskBoardApp.ExampleRoot,
            });
            builder.WebHost.UseTestServer();
            // What a multi-instance deployment shares: the key ring (cookies, antiforgery) and the output-cache store.
            builder.Services.AddDataProtection().SetApplicationName("TaskBoard").PersistKeysToFileSystem(keys);
            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(options => options.Cookie.Name = "taskboard.auth");
            builder.Services.AddAuthorization();
            builder.Services.AddNetjes(options =>
            {
                // Routes from TaskBoard's app/ on disk; the fake renderer below renders nothing, so no Node is needed.
                options.Mode = NetjesMode.Development;
                // The example's generated file describes the real app, not this host.
                options.TypesFile = null;
                options.SegmentKeySecret = segmentKeySecret;
                options.UseSharedProps(http => ValueTask.FromResult(new SharedProps(SignedInUser.From(http.User))));
            });
            builder.Services.AddOutputCache();
            builder.Services.Replace(ServiceDescriptor.Singleton(store));
            var instance = default(Instance);
            builder.Services.AddSingleton<ISsrRenderer>(_ => instance!.Renderer);

            var app = builder.Build();
            instance = new Instance(name, app);
            app.UseNetjes();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseAntiforgery();
            app.UseOutputCache();
            app.MapLayoutLoader(LayoutSegment, () => Page.Props(new LayoutProps(name)));
            app.MapPageLoader("/", () => Page.Props(new LandingProps(name, Interlocked.Increment(ref instance._pageLoads))))
                .CachePage(TimeSpan.FromMinutes(5), "landing");
            app.MapAction("landing.publish", () => ServerAction.Ok().RevalidatePath("/"));
            app.MapGet("/test/sign-in", (HttpContext http) => http.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new SignedInUser(Guid.NewGuid(), "Signed-in visitor", "visitor@taskboard.test").ToPrincipal(CookieAuthenticationDefaults.AuthenticationScheme)));
            app.MapPages();
            await app.StartAsync(Ct);
            return instance;
        }

        public HttpMessageHandler Handler => App.GetTestServer().CreateHandler();

        public async ValueTask DisposeAsync() => await App.DisposeAsync();
    }

    /// <summary>One browser behind the load balancer: its cookies go to whichever instance answers.</summary>
    private sealed class Visitor
    {
        private readonly CookieContainer _cookies = new();

        public async Task<HttpResponseMessage> SendAsync(Instance to, HttpRequestMessage request)
        {
            using var client = new HttpClient(to.Handler, disposeHandler: false) { BaseAddress = new Uri("https://localhost") };
            request.RequestUri = new Uri(client.BaseAddress, request.RequestUri!);
            if (_cookies.GetCookieHeader(request.RequestUri) is { Length: > 0 } cookie)
            {
                request.Headers.Add("Cookie", cookie);
            }

            var response = await client.SendAsync(request, Ct);
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var setCookie in setCookies)
                {
                    _cookies.SetCookies(request.RequestUri, setCookie);
                }
            }

            return response;
        }

        public async Task<(HttpResponseMessage Response, string Body)> DocumentAsync(Instance to)
        {
            var response = await SendAsync(to, new HttpRequestMessage(HttpMethod.Get, "/"));
            return (response, await response.Content.ReadAsStringAsync(Ct));
        }

        public async Task<(HttpResponseMessage Response, JsonNode Payload)> NavigateAsync(Instance to, string url = "/", params string[] held)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add(NetjesHeaders.Data, "1");
            if (held.Length > 0)
            {
                request.Headers.Add(NetjesHeaders.Segments, string.Join(",", held));
            }

            var response = await SendAsync(to, request);
            return (response, JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!);
        }

        public async Task<HttpStatusCode> CallAsync(Instance on, string action)
        {
            var token = JsonNode.Parse(await (await SendAsync(on, new HttpRequestMessage(HttpMethod.Get, "/_netjes/antiforgery"))).Content.ReadAsStringAsync(Ct))!;
            var request = new HttpRequestMessage(HttpMethod.Post, $"/_netjes/actions/{action}") { Content = new FormUrlEncodedContent([]) };
            request.Headers.Add(NetjesHeaders.Data, "1");
            request.Headers.Add((string)token["headerName"]!, (string)token["requestToken"]!);
            return (await SendAsync(on, request)).StatusCode;
        }
    }

    private static JsonNode Layout(JsonNode payload) => payload["segments"]!.AsArray().Single(s => (string?)s!["id"] == LayoutSegment)!;

    /// <summary>
    /// What the client router can use: every segment the payload marks reused must be one whose key it sent, or the
    /// router has no props for it and loads the page again without keys.
    /// </summary>
    private static void AssertReusesOnlyWhatWasSent(JsonNode payload, params string[] sent)
    {
        foreach (var segment in payload["segments"]!.AsArray().Where(s => (bool?)s!["reused"] == true))
        {
            Assert.Contains((string?)segment!["key"], sent);
        }
    }

    [Fact]
    public async Task AnEntryStoredByOneInstance_IsServedByTheOther_WithoutCookiesOrTokens()
    {
        await using var a = await StartAsync("a");
        await using var b = await StartAsync("b");
        var visitor = new Visitor();

        var (storedDocument, storedHtml) = await visitor.DocumentAsync(a);
        var (servedDocument, servedHtml) = await new Visitor().DocumentAsync(b);
        var (_, storedPayload) = await visitor.NavigateAsync(a);
        var (servedNavigation, servedPayload) = await new Visitor().NavigateAsync(b);

        Assert.Equal(HttpStatusCode.OK, storedDocument.StatusCode);
        Assert.Equal(HttpStatusCode.OK, servedDocument.StatusCode);
        Assert.Equal(storedHtml, servedHtml);
        Assert.Equal(2, a.PageLoads);
        Assert.Equal(0, b.PageLoads);
        Assert.Throws<InvalidOperationException>(() => b.Renderer.Last);
        // Shared as they are: no antiforgery token in the payload, and no cookie set by either instance.
        var rendered = JsonNode.Parse(a.Renderer.Last)!;
        Assert.Null(rendered["antiforgery"]);
        Assert.Null(rendered["shared"]!["user"]);
        Assert.False(storedDocument.Headers.Contains("Set-Cookie"));
        Assert.False(servedDocument.Headers.Contains("Set-Cookie"));
        Assert.Equal(storedPayload.ToJsonString(), servedPayload.ToJsonString());
        Assert.Equal("a", (string?)servedPayload["props"]!["instance"]);
        Assert.Null(servedPayload["antiforgery"]);
        // The server time is added outside the cache: current on the instance that served it.
        Assert.True(servedNavigation.Headers.Contains(NetjesHeaders.ServerTime));
    }

    [Fact]
    public async Task RevalidatingOnOneInstance_EvictsThePageForBoth()
    {
        await using var a = await StartAsync("a");
        await using var b = await StartAsync("b");
        var visitor = new Visitor();
        await visitor.DocumentAsync(a);
        await visitor.NavigateAsync(a);
        Assert.Equal(2, a.PageLoads);

        var published = await visitor.CallAsync(b, "landing.publish");
        await visitor.DocumentAsync(a);
        var (_, afterAction) = await visitor.NavigateAsync(a);
        await b.App.Services.GetRequiredService<IPageCache>().RevalidateTagAsync("landing", Ct);
        var (_, afterTag) = await visitor.NavigateAsync(a);

        Assert.Equal(HttpStatusCode.OK, published);
        Assert.Equal(5, a.PageLoads);
        Assert.Equal(4, (int?)afterAction["props"]!["load"]);
        Assert.Equal(5, (int?)afterTag["props"]!["load"]);
        Assert.Equal(0, b.PageLoads);
    }

    [Fact]
    public async Task SignedInVisitors_AreNeitherServedTheSharedEntry_NorStoredForOthers()
    {
        await using var a = await StartAsync("a");
        await using var b = await StartAsync("b");
        await new Visitor().NavigateAsync(a);
        var signedIn = new Visitor();
        // Signed in on one instance; the shared key ring lets the other read the cookie.
        await signedIn.SendAsync(b, new HttpRequestMessage(HttpMethod.Get, "/test/sign-in"));

        var (_, own) = await signedIn.NavigateAsync(a);
        await signedIn.DocumentAsync(b);
        var ownDocument = b.Renderer.Last;
        var (_, anonymous) = await new Visitor().NavigateAsync(b);
        await new Visitor().DocumentAsync(a);

        // Each instance rendered for the signed-in visitor instead of serving the anonymous payload.
        Assert.Equal("Signed-in visitor", (string?)own["shared"]!["user"]!["name"]);
        Assert.Contains("Signed-in visitor", ownDocument, StringComparison.Ordinal);
        // Neither render was stored: the anonymous payload is the one stored first, and no document was stored, so the
        // anonymous document is rendered afresh, for nobody.
        Assert.Null(anonymous["shared"]!["user"]);
        Assert.DoesNotContain("Signed-in visitor", anonymous.ToJsonString(), StringComparison.Ordinal);
        Assert.Null(JsonNode.Parse(a.Renderer.Last)!["shared"]!["user"]);
        Assert.Equal(3, a.PageLoads);
        Assert.Equal(1, b.PageLoads);
    }

    [Fact]
    public async Task WithASharedSecret_EitherInstanceReusesTheLayoutTheVisitorHolds()
    {
        var secret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await using var a = await StartAsync("a", secret);
        await using var b = await StartAsync("b", secret);
        var visitor = new Visitor();
        var (_, login) = await visitor.NavigateAsync(a, "/login");
        var key = (string)Layout(login)["key"]!;

        var (_, onB) = await visitor.NavigateAsync(b, "/", key);
        var (_, onA) = await visitor.NavigateAsync(a, "/", key);

        Assert.True((bool?)Layout(onB)["reused"]);
        Assert.Equal(key, (string?)Layout(onB)["key"]);
        AssertReusesOnlyWhatWasSent(onB, key);
        AssertReusesOnlyWhatWasSent(onA, key);
    }

    /// <summary>
    /// Without a shared secret each instance signs its own keys, and one instance's key means nothing to the other:
    /// segments then load in full. The store is shared, so an entry that reuses a layout is keyed by the secret that
    /// signed its key as well: one instance never serves the other's entry to a visitor holding its own key.
    /// </summary>
    [Fact]
    public async Task WithoutASharedSecret_ASharedEntry_NeverReusesAKeyTheVisitorDidNotSend()
    {
        await using var a = await StartAsync("a");
        await using var b = await StartAsync("b");
        var onA = new Visitor();
        var onB = new Visitor();
        // Each holds the landing layout from its own instance (the login page is not cached, so each renders it).
        var keyFromA = (string)Layout((await onA.NavigateAsync(a, "/login")).Payload)["key"]!;
        var keyFromB = (string)Layout((await onB.NavigateAsync(b, "/login")).Payload)["key"]!;
        Assert.NotEqual(keyFromA, keyFromB);

        var (_, storedByB) = await onB.NavigateAsync(b, "/", keyFromB);
        var (_, servedByA) = await onA.NavigateAsync(a, "/", keyFromA);

        Assert.True((bool?)Layout(storedByB)["reused"]);
        AssertReusesOnlyWhatWasSent(storedByB, keyFromB);
        AssertReusesOnlyWhatWasSent(servedByA, keyFromA);
    }
}
