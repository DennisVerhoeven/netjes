using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Netjes.Rendering;

namespace Netjes.Tests.Endpoints;

/// <summary>
/// However a page is output-cached (<c>CachePage</c>, ASP.NET Core's <c>CacheOutput</c> in any form, an
/// <c>[OutputCache]</c> attribute, a base policy), its entries are keyed by Netjes's rules: a document and a client
/// payload at one URL never share an entry, and a payload is keyed by which of the page's layouts the client holds.
/// </summary>
public class PageOutputCacheKeyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public enum Policy
    {
        CachePage,
        CacheOutput,
        NamedPolicy,
        PolicyBuilder,
        Attribute,
        BasePolicy,
    }

    public static TheoryData<Policy> Policies => [.. Enum.GetValues<Policy>()];

    private static readonly string[] Files = ["app/layout.tsx", "app/[team]/layout.tsx", "app/[team]/page.tsx"];

    /// <summary>The team page under <paramref name="policy"/>, below a layout with a loader.</summary>
    private static Task<TestApp> StartAsync(
        Policy policy, Func<int> load, Action<IServiceCollection>? services = null, Action<WebApplication>? map = null) =>
        TestApp.StartAsync(
            Files,
            map: a =>
            {
                map?.Invoke(a);
                a.MapLayoutLoader("/[team]", (string team) => Page.Props(new { Team = team }));
                var page = policy == Policy.Attribute
                    ? a.MapPageLoader("/[team]", [OutputCache(Duration = 300)] () => Page.Props(new { Load = load() }))
                    : a.MapPageLoader("/[team]", () => Page.Props(new { Load = load() }));
                CacheUnder(policy, page);
            },
            services: s =>
            {
                AddOutputCache(s, policy);
                services?.Invoke(s);
            },
            configureApp: a => a.UseOutputCache());

    /// <summary>Caches <paramref name="page"/> under <paramref name="policy"/>; an <c>[OutputCache]</c> attribute is the loader's own.</summary>
    private static void CacheUnder(Policy policy, RouteHandlerBuilder page) =>
        _ = policy switch
        {
            Policy.CachePage => page.CachePage(TimeSpan.FromMinutes(5)),
            Policy.CacheOutput => page.CacheOutput(),
            Policy.NamedPolicy => page.CacheOutput("pages"),
            Policy.PolicyBuilder => page.CacheOutput(p => p.Expire(TimeSpan.FromMinutes(5))),
            _ => page,
        };

    /// <summary>
    /// The team page with <paramref name="loader"/> under <paramref name="policy"/>. For <see cref="Policy.Attribute"/>,
    /// the endpoint gets the metadata an <c>[OutputCache]</c> attribute on the loader gives it.
    /// </summary>
    private static Task<TestApp> StartWithLoaderAsync(
        Policy policy, Delegate loader, Action<IServiceCollection>? services = null, Action<WebApplication>? map = null) =>
        TestApp.StartAsync(
            Files,
            map: a =>
            {
                map?.Invoke(a);
                a.MapLayoutLoader("/[team]", (string team) => Page.Props(new { Team = team }));
                var page = a.MapPageLoader("/[team]", loader);
                CacheUnder(policy, policy == Policy.Attribute ? page.WithMetadata(new OutputCacheAttribute { Duration = 300 }) : page);
            },
            services: s =>
            {
                AddOutputCache(s, policy);
                services?.Invoke(s);
            },
            configureApp: a => a.UseOutputCache());

    private static void AddOutputCache(IServiceCollection services, Policy policy) =>
        services.AddOutputCache(o =>
        {
            o.AddPolicy("pages", b => b.Expire(TimeSpan.FromMinutes(5)));
            if (policy == Policy.BasePolicy)
            {
                o.AddBasePolicy(b => b.Expire(TimeSpan.FromMinutes(5)));
            }
        });

    /// <summary>
    /// A browser: it sends back the cookies it was given. One that already has the antiforgery cookie gets pages that
    /// set none, which the output cache stores. A fresh one has no cookies.
    /// </summary>
    private sealed record Visitor(TestApp App, string? Cookie, string? TokenField = null, string? Token = null)
    {
        public static Visitor Fresh(TestApp app) => new(app, null);

        public static async Task<Visitor> ReturningAsync(TestApp app)
        {
            var response = await app.Client.GetAsync("/_netjes/antiforgery", Ct);
            var token = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
            return new Visitor(
                app,
                string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])),
                (string)token["formFieldName"]!,
                (string)token["requestToken"]!);
        }

        /// <summary>A form post without JavaScript to a server action.</summary>
        public Task<HttpResponseMessage> PostFormAsync(string action, params (string Name, string Value)[] fields)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/_netjes/actions/{action}")
            {
                Content = new FormUrlEncodedContent([.. fields.Select(f => KeyValuePair.Create(f.Name, f.Value)), KeyValuePair.Create(TokenField!, Token!)]),
            };
            request.Headers.Add("Cookie", Cookie);
            return App.Client.SendAsync(request, Ct);
        }

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, params (string Name, string Value)[] headers)
        {
            var request = new HttpRequestMessage(method, url);
            if (Cookie is not null)
            {
                request.Headers.Add("Cookie", Cookie);
            }

            foreach (var (name, value) in headers)
            {
                Assert.True(request.Headers.TryAddWithoutValidation(name, value));
            }

            return await App.Client.SendAsync(request, Ct);
        }

        public async Task<JsonNode> DataAsync(string url, params (string Name, string Value)[] headers) =>
            await JsonAsync(await SendAsync(HttpMethod.Get, url, [(NetjesHeaders.Data, "1"), .. headers]));

        public Task<HttpResponseMessage> DocumentAsync(string url, params (string Name, string Value)[] headers) =>
            SendAsync(HttpMethod.Get, url, headers);
    }

    private static async Task<JsonNode> JsonAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
    }

    private static async Task AssertDocumentAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("<!DOCTYPE html>", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    private static JsonNode Layout(JsonNode payload) => payload["segments"]![1]!;

    /// <summary>
    /// A returning visitor's client navigations are stored; a fresh visitor's document at the same URL must still be a
    /// document, and their navigation must not get the payload that leaves out the layout they do not hold.
    /// </summary>
    [Theory]
    [MemberData(nameof(Policies))]
    public async Task UnderAnyPolicy_AReturningVisitorsPayloads_AreNeverAnotherVisitorsDocumentOrLayout(Policy policy)
    {
        var loads = 0;
        await using var app = await StartAsync(policy, () => Interlocked.Increment(ref loads));
        var visitor = await Visitor.ReturningAsync(app);
        var key = (string)Layout(await visitor.DataAsync("/core?from=elsewhere"))["key"]!;

        var reusing = await visitor.DataAsync("/core", (NetjesHeaders.Segments, key));
        var fresh = Visitor.Fresh(app);
        var document = await fresh.DocumentAsync("/core");
        var full = await fresh.DataAsync("/core");

        Assert.True((bool?)Layout(reusing)["reused"]);
        await AssertDocumentAsync(document);
        Assert.Null(Layout(full)["reused"]);
        Assert.Equal("core", (string?)Layout(full)["props"]!["team"]);
    }

    [Theory]
    [MemberData(nameof(Policies))]
    public async Task UnderAnyPolicy_ADocumentAndAPayloadAtOneUrl_KeepTheirOwnEntries(Policy policy)
    {
        var loads = 0;
        await using var app = await StartAsync(policy, () => Interlocked.Increment(ref loads));
        var visitor = await Visitor.ReturningAsync(app);

        // A payload stored first, then a document at its URL; and the other way round.
        var payload = await visitor.DataAsync("/core");
        var document = await visitor.DocumentAsync("/core");
        await visitor.DocumentAsync("/infra");
        var infraPayload = await visitor.DataAsync("/infra");

        await AssertDocumentAsync(document);
        Assert.Equal("core", (string?)Layout(payload)["props"]!["team"]);
        Assert.Equal("infra", (string?)Layout(infraPayload)["props"]!["team"]);
        // Each was stored: asked again, nothing renders.
        Assert.Equal(payload.ToJsonString(), (await visitor.DataAsync("/core")).ToJsonString());
        Assert.Equal(infraPayload.ToJsonString(), (await visitor.DataAsync("/infra")).ToJsonString());
        await AssertDocumentAsync(await visitor.DocumentAsync("/core"));
        await AssertDocumentAsync(await visitor.DocumentAsync("/infra"));
        Assert.Equal(4, loads);
    }

    [Theory]
    [MemberData(nameof(Policies))]
    public async Task UnderAnyPolicy_APayloadThatReusesALayout_IsServedOnlyToClientsHoldingIt(Policy policy)
    {
        var loads = 0;
        await using var app = await StartAsync(policy, () => Interlocked.Increment(ref loads));
        var visitor = await Visitor.ReturningAsync(app);
        // The layout's key, from another entry of the page (the query string is part of the key).
        var key = (string)Layout(await visitor.DataAsync("/core?from=elsewhere"))["key"]!;

        var holding = await visitor.DataAsync("/core", (NetjesHeaders.Segments, key));
        var notHolding = await visitor.DataAsync("/core");
        var madeUp = await visitor.DataAsync("/core", (NetjesHeaders.Segments, "made-up"));
        var holdingAgain = await visitor.DataAsync("/core", (NetjesHeaders.Segments, key));
        var document = await visitor.DocumentAsync("/core", (NetjesHeaders.Segments, key));

        Assert.True((bool?)Layout(holding)["reused"]);
        Assert.Null(Layout(holding)["props"]);
        Assert.Equal(holding.ToJsonString(), holdingAgain.ToJsonString());
        Assert.Null(Layout(notHolding)["reused"]);
        Assert.Equal("core", (string?)Layout(notHolding)["props"]!["team"]);
        Assert.Equal(notHolding.ToJsonString(), madeUp.ToJsonString());
        await AssertDocumentAsync(document);
        Assert.Equal(4, loads);
    }

    /// <summary>Header names are case-insensitive: any casing of the data header is a payload request, keyed as one.</summary>
    [Theory]
    [MemberData(nameof(Policies))]
    public async Task UnderAnyPolicy_TheDataHeaderInAnyCasing_IsKeyedAsAPayload(Policy policy)
    {
        var loads = 0;
        await using var app = await StartAsync(policy, () => Interlocked.Increment(ref loads));
        var visitor = await Visitor.ReturningAsync(app);

        var lower = await JsonAsync(await visitor.SendAsync(HttpMethod.Get, "/core", ("x-netjes-data", "1")));
        var document = await visitor.DocumentAsync("/core");
        var upper = await JsonAsync(await visitor.SendAsync(HttpMethod.Get, "/core", ("X-NETJES-DATA", "yes")));

        await AssertDocumentAsync(document);
        Assert.Equal(lower.ToJsonString(), upper.ToJsonString());
        Assert.Equal(2, loads);
    }

    /// <summary>Only the header makes a payload request: a query parameter of that name is just part of the URL.</summary>
    [Theory]
    [MemberData(nameof(Policies))]
    public async Task UnderAnyPolicy_TheDataHeaderAsAQueryParameter_IsADocumentRequest(Policy policy)
    {
        var loads = 0;
        await using var app = await StartAsync(policy, () => Interlocked.Increment(ref loads));
        var visitor = await Visitor.ReturningAsync(app);
        const string url = "/core?X-Netjes-Data=1&x-netjes-data=1&X-Netjes-Segments=made-up";

        var payload = await visitor.DataAsync(url);
        var document = await visitor.DocumentAsync(url);
        var documentAgain = await visitor.DocumentAsync(url);

        Assert.Equal("core", (string?)Layout(payload)["props"]!["team"]);
        await AssertDocumentAsync(document);
        await AssertDocumentAsync(documentAgain);
        Assert.Equal(2, loads);
    }

    /// <summary>HEAD has entries of its own, keyed by the same rules: a HEAD for a document gets a document's headers.</summary>
    [Theory]
    [MemberData(nameof(Policies))]
    public async Task UnderAnyPolicy_HeadRequests_KeepDocumentsAndPayloadsApart(Policy policy)
    {
        var loads = 0;
        await using var app = await StartAsync(policy, () => Interlocked.Increment(ref loads));
        var visitor = await Visitor.ReturningAsync(app);

        var headPayload = await visitor.SendAsync(HttpMethod.Head, "/core", (NetjesHeaders.Data, "1"));
        var headDocument = await visitor.SendAsync(HttpMethod.Head, "/core");
        var document = await visitor.DocumentAsync("/core");
        var headPayloadAgain = await visitor.SendAsync(HttpMethod.Head, "/core", ("x-netjes-data", "1"));
        var headDocumentAgain = await visitor.SendAsync(HttpMethod.Head, "/core");

        Assert.Equal("application/json", headPayload.Content.Headers.ContentType?.MediaType);
        Assert.Equal("text/html", headDocument.Content.Headers.ContentType?.MediaType);
        await AssertDocumentAsync(document);
        Assert.Equal("application/json", headPayloadAgain.Content.Headers.ContentType?.MediaType);
        Assert.Equal("text/html", headDocumentAgain.Content.Headers.ContentType?.MediaType);
        Assert.Equal(3, loads);
    }

    /// <summary>
    /// The page rendered in place of a failed form post (without JavaScript) shows that visitor's errors and input: it
    /// is never answered from the cache, and never stored.
    /// </summary>
    [Theory]
    [MemberData(nameof(Policies))]
    public async Task UnderAnyPolicy_APageRenderedForAFailedFormPost_IsNotTheStoredPage(Policy policy)
    {
        var loads = 0;
        await using var app = await StartAsync(
            policy,
            () => Interlocked.Increment(ref loads),
            map: a => a.MapAction("sign", ([Microsoft.AspNetCore.Mvc.FromForm] string? name) => ServerAction.Invalid("name", "required")));
        var visitor = await Visitor.ReturningAsync(app);
        await AssertDocumentAsync(await visitor.DocumentAsync("/core"));

        var failed = await visitor.PostFormAsync("sign", ("name", ""), ("__netjes_return", "/core"));
        var failedPayload = app.Renderer.LastPayload;
        var again = await visitor.DocumentAsync("/core");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);
        Assert.Equal("required", (string?)failedPayload["actionState"]!["errors"]!["name"]![0]);
        await AssertDocumentAsync(again);
        Assert.Equal(2, loads);
    }

    /// <summary>
    /// A request token belongs to the visitor's antiforgery cookie: a response the output cache may store carries none,
    /// in the payload or in the document, so no stored page hands one visitor's token to another. Pages without one
    /// fetch a token before a post, as CachePage pages always have.
    /// </summary>
    [Theory]
    [MemberData(nameof(Policies))]
    public async Task UnderAnyPolicy_AStoredPage_CarriesNoVisitorsAntiforgeryToken(Policy policy)
    {
        var loads = 0;
        await using var app = await StartAsync(policy, () => Interlocked.Increment(ref loads));
        // The document embeds its payload, as the real renderer does.
        app.Renderer.OnRender = json => $"<!DOCTYPE html><html><body><script type=\"application/json\">{json}</script></body></html>";
        var visitor = await Visitor.ReturningAsync(app);

        var stored = await visitor.DataAsync("/core");
        var servedToAnother = await Visitor.Fresh(app).DataAsync("/core");
        await visitor.DocumentAsync("/core");
        var documentForAnother = await Visitor.Fresh(app).DocumentAsync("/core");

        Assert.Null(stored["antiforgery"]);
        Assert.Null(servedToAnother["antiforgery"]);
        Assert.DoesNotContain("requestToken", await documentForAnother.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.False(documentForAnother.Headers.Contains("Set-Cookie"));
        Assert.Equal(2, loads);
    }

    /// <summary>
    /// What the output cache will not store is one visitor's page, and keeps its token, so its forms work without
    /// JavaScript: a request with an Authorization header, and the page rendered for a failed form post.
    /// </summary>
    [Theory]
    [InlineData(Policy.CacheOutput)]
    [InlineData(Policy.NamedPolicy)]
    [InlineData(Policy.PolicyBuilder)]
    [InlineData(Policy.Attribute)]
    [InlineData(Policy.BasePolicy)]
    public async Task UnderAPolicyOtherThanCachePage_APageTheCacheWillNotStore_KeepsItsAntiforgeryToken(Policy policy)
    {
        await using var app = await StartAsync(
            policy,
            () => 0,
            map: a => a.MapAction("sign", ([Microsoft.AspNetCore.Mvc.FromForm] string? name) => ServerAction.Invalid("name", "required")));
        var visitor = await Visitor.ReturningAsync(app);

        var authorized = await visitor.DataAsync("/core", ("Authorization", "Bearer someone"));
        await visitor.PostFormAsync("sign", ("name", ""), ("__netjes_return", "/core"));
        var failedPost = app.Renderer.LastPayload;

        Assert.NotNull((string?)authorized["antiforgery"]!["requestToken"]);
        Assert.NotNull(failedPost["actionState"]);
        Assert.NotNull((string?)failedPost["antiforgery"]!["requestToken"]);
    }

    /// <summary>A minimal API that renders a page: an endpoint the page rules key only under CachePage.</summary>
    private static Task<TestApp> StartElsewhereAsync(Policy policy, Func<int> load) =>
        TestApp.StartAsync(
            Files,
            map: a =>
            {
                a.MapLayoutLoader("/[team]", (string team) => Page.Props(new { Team = team }));
                var elsewhere = a.MapGet("/elsewhere/{team}", () => Page.Render("/[team]", new { Load = load() }));
                a.MapGet("/moved", () => Page.Redirect("/core")).CacheOutput();
                a.MapGet("/gone", () => Page.NotFound()).CacheOutput();
                _ = policy switch
                {
                    Policy.CachePage => elsewhere.CachePage(TimeSpan.FromMinutes(5)),
                    Policy.CacheOutput => elsewhere.CacheOutput(),
                    _ => elsewhere,
                };
            },
            services: s => s.AddOutputCache(o =>
            {
                if (policy == Policy.BasePolicy)
                {
                    o.AddBasePolicy(b => b.Expire(TimeSpan.FromMinutes(5)));
                }
            }),
            configureApp: a => a.UseOutputCache());

    /// <summary>
    /// Nothing would keep such an endpoint's documents and payloads apart, so under output caching other than
    /// CachePage the pages it renders (redirects included) are not stored, and the app is told once.
    /// </summary>
    [Theory]
    [InlineData(Policy.CacheOutput)]
    [InlineData(Policy.BasePolicy)]
    public async Task APageRenderedByAnEndpointThatIsNoPage_IsNotStored_UnlessCachePageCachesIt(Policy policy)
    {
        var loads = 0;
        await using var app = await StartElsewhereAsync(policy, () => Interlocked.Increment(ref loads));
        var visitor = await Visitor.ReturningAsync(app);

        var payload = await visitor.DataAsync("/elsewhere/core");
        var document = await visitor.DocumentAsync("/elsewhere/core");
        var payloadAgain = await visitor.DataAsync("/elsewhere/core");
        var redirect = await visitor.DataAsync("/moved");
        var movedDocument = await visitor.DocumentAsync("/moved");

        Assert.Equal(1, (int?)payload["props"]!["load"]);
        await AssertDocumentAsync(document);
        Assert.Equal(3, (int?)payloadAgain["props"]!["load"]);
        Assert.Equal("redirect", (string?)redirect["kind"]);
        Assert.Equal(HttpStatusCode.TemporaryRedirect, movedDocument.StatusCode);
        var warning = Assert.Single(app.Logs, l => l.Message.Contains("is not stored", StringComparison.Ordinal) && l.Message.Contains("/elsewhere/{team}", StringComparison.Ordinal));
        Assert.Contains("CachePage", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>The output cache stores only a 200, so a page such an endpoint renders with another status is no cause to warn.</summary>
    [Theory]
    [InlineData(Policy.CacheOutput)]
    [InlineData(Policy.BasePolicy)]
    public async Task ANotFoundPageFromAnEndpointThatIsNoPage_IsNoCauseForAWarning(Policy policy)
    {
        await using var app = await StartElsewhereAsync(policy, () => 0);
        var visitor = await Visitor.ReturningAsync(app);

        var document = await visitor.DocumentAsync("/gone");
        var payload = await visitor.SendAsync(HttpMethod.Get, "/gone", (NetjesHeaders.Data, "1"));

        Assert.Equal(HttpStatusCode.NotFound, document.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, payload.StatusCode);
        Assert.DoesNotContain(app.Logs, l => l.Message.Contains("is not stored", StringComparison.Ordinal));
    }

    /// <summary>
    /// Output caching placed before <c>UseNetjes</c> runs before routing, where no policy knows the endpoint: only base
    /// policies apply and nothing keys the page, so it is not stored, and the warning names the order, not CachePage.
    /// </summary>
    [Fact]
    public async Task APageOutputCachedBeforeRouting_IsNotStored_AndTheWarningNamesTheOrder()
    {
        var loads = 0;
        await using var app = await TestApp.StartAsync(
            Files,
            map: a =>
            {
                a.MapLayoutLoader("/[team]", (string team) => Page.Props(new { Team = team }));
                a.MapPageLoader("/[team]", () => Page.Props(new { Load = Interlocked.Increment(ref loads) })).CachePage(TimeSpan.FromMinutes(5));
            },
            services: s => s.AddOutputCache(o => o.AddBasePolicy(b => b.Expire(TimeSpan.FromMinutes(5)))),
            beforeNetjes: a => a.UseOutputCache());
        var visitor = await Visitor.ReturningAsync(app);

        await visitor.DataAsync("/core");
        var document = await visitor.DocumentAsync("/core");
        await visitor.DataAsync("/core");

        await AssertDocumentAsync(document);
        Assert.Equal(3, loads);
        var warning = Assert.Single(app.Logs, l => l.Message.Contains("is not stored", StringComparison.Ordinal));
        Assert.Contains("UseOutputCache() after UseNetjes()", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(".CachePage(duration)", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APageRenderedByAnEndpointThatIsNoPage_UnderCachePage_IsStoredByThePageRules()
    {
        var loads = 0;
        await using var app = await StartElsewhereAsync(Policy.CachePage, () => Interlocked.Increment(ref loads));
        var visitor = await Visitor.ReturningAsync(app);

        var payload = await visitor.DataAsync("/elsewhere/core");
        var document = await visitor.DocumentAsync("/elsewhere/core");
        var payloadAgain = await visitor.DataAsync("/elsewhere/core");

        await AssertDocumentAsync(document);
        Assert.Equal(payload.ToJsonString(), payloadAgain.ToJsonString());
        Assert.Equal(2, loads);
        Assert.DoesNotContain(app.Logs, l => l.Message.Contains("is not stored", StringComparison.Ordinal));
    }

    /// <summary>A client build: its version, and documents that name its own assets.</summary>
    private sealed class BuildAssets(string version) : IAssetResolver
    {
        public string Version => version;

        public DocumentAssets Resolve(IReadOnlyList<string> modules) => new([], [], [$"/assets/entry-{version}.js"]);
    }

    /// <summary>ASP.NET Core's in-memory store, out of a container of its own so instances can share it (as Redis).</summary>
    private static IOutputCacheStore SharedStore() =>
        new ServiceCollection().AddOptions().AddOutputCache().BuildServiceProvider().GetRequiredService<IOutputCacheStore>();

    /// <summary>An instance of the app on a shared store, serving the client build <paramref name="build"/>.</summary>
    private static Task<TestApp> InstanceAsync(Policy policy, IOutputCacheStore store, string build, Func<int> load) =>
        StartAsync(policy, load, services: s =>
        {
            s.Replace(ServiceDescriptor.Singleton(store));
            s.Replace(ServiceDescriptor.Singleton<IAssetResolver>(new BuildAssets(build)));
        });

    /// <summary>
    /// During a rolling deploy, instances on two builds share the store: each serves only its own build's documents
    /// (which name that build's assets) and payloads (another build's would make the client load the page again).
    /// </summary>
    [Theory]
    [MemberData(nameof(Policies))]
    public async Task UnderAnyPolicy_InstancesOnAnotherBuild_NeverServeEachOthersEntries(Policy policy)
    {
        var store = SharedStore();
        var (oldLoads, newLoads) = (0, 0);
        await using var old = await InstanceAsync(policy, store, "build-1", () => Interlocked.Increment(ref oldLoads));
        await using var current = await InstanceAsync(policy, store, "build-2", () => Interlocked.Increment(ref newLoads));
        var onOld = await Visitor.ReturningAsync(old);
        var onCurrent = await Visitor.ReturningAsync(current);

        var oldPayload = await onOld.DataAsync("/core");
        await onOld.DocumentAsync("/core");
        var currentPayload = await onCurrent.DataAsync("/core");
        var currentDocument = await onCurrent.DocumentAsync("/core");
        var oldPayloadAgain = await onOld.DataAsync("/core");

        Assert.Equal("build-1", (string?)oldPayload["version"]);
        Assert.Equal("build-2", (string?)currentPayload["version"]);
        await AssertDocumentAsync(currentDocument);
        Assert.Contains("/assets/entry-build-2.js", current.Renderer.LastPayload.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(oldPayload.ToJsonString(), oldPayloadAgain.ToJsonString());
        Assert.Equal((2, 2), (oldLoads, newLoads));
    }

    [Theory]
    [MemberData(nameof(Policies))]
    public async Task UnderAnyPolicy_InstancesOnOneBuild_ShareDocumentsAndFullPayloads(Policy policy)
    {
        var store = SharedStore();
        var (aLoads, bLoads) = (0, 0);
        await using var a = await InstanceAsync(policy, store, "build-1", () => Interlocked.Increment(ref aLoads));
        await using var b = await InstanceAsync(policy, store, "build-1", () => Interlocked.Increment(ref bLoads));
        var onA = await Visitor.ReturningAsync(a);
        var onB = await Visitor.ReturningAsync(b);

        var stored = await onA.DataAsync("/core");
        await onA.DocumentAsync("/core");
        var served = await onB.DataAsync("/core");
        var document = await onB.DocumentAsync("/core");

        Assert.Equal(stored.ToJsonString(), served.ToJsonString());
        await AssertDocumentAsync(document);
        Assert.Equal((2, 0), (aLoads, bLoads));
    }

    /// <summary>
    /// A page repeats its URL, query string included, in its payload (the client shows it in the address bar), and its
    /// loaders may bind any of it. ASP.NET Core's key folds the case of query names and sorts repeated values, so each
    /// spelling of a query string gets its own entry.
    /// </summary>
    [Theory]
    [MemberData(nameof(Policies))]
    public async Task UnderAnyPolicy_EachQueryStringAsSent_GetsItsOwnEntry(Policy policy)
    {
        var loads = 0;
        await using var app = await StartAsync(policy, () => Interlocked.Increment(ref loads));
        var visitor = await Visitor.ReturningAsync(app);
        string[] urls = ["/core?tag=a&tag=b", "/core?tag=b&tag=a", "/core?Tag=a&tag=b", "/core?tag=a&tag=b&"];

        var payloads = new List<JsonNode>();
        foreach (var url in urls)
        {
            payloads.Add(await visitor.DataAsync(url));
        }

        Assert.Equal(urls, payloads.Select(p => (string?)p["url"]));
        Assert.Equal((string?)payloads[0]["url"], (string?)(await visitor.DataAsync(urls[0]))["url"]);
        Assert.Equal(4, loads);
    }

    /// <summary>A policy that keys on some query names only would serve one visitor's query string to another.</summary>
    [Fact]
    public async Task APolicyThatVariesByOneQueryName_StillKeysPagesOnTheWholeQueryString()
    {
        await using var app = await TestApp.StartAsync(
            Files,
            map: a => a.MapPageLoader("/[team]", (string? page) => Page.Props(new { Page = page }))
                .CacheOutput(p => p.SetVaryByQuery("page")),
            services: s => s.AddOutputCache(),
            configureApp: a => a.UseOutputCache());
        var first = await (await Visitor.ReturningAsync(app)).DataAsync("/core?page=2&email=first%40example.com");

        var second = await (await Visitor.ReturningAsync(app)).DataAsync("/core?page=2&email=second%40example.com");

        Assert.Equal("/core?page=2&email=first%40example.com", (string?)first["url"]);
        Assert.Equal("/core?page=2&email=second%40example.com", (string?)second["url"]);
        Assert.Equal("2", (string?)second["props"]!["page"]);
    }

    /// <summary>Conventions on <c>MapPages()</c> cache the pages without a loader, keyed by the same rules.</summary>
    [Theory]
    [InlineData(Policy.CachePage)]
    [InlineData(Policy.CacheOutput)]
    [InlineData(Policy.PolicyBuilder)]
    public async Task UnderAnyPolicyOnMapPages_PagesWithoutALoader_KeepDocumentsAndPayloadsApart(Policy policy)
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/about/page.tsx"],
            pages: pages => _ = policy switch
            {
                Policy.CachePage => pages.CachePage(TimeSpan.FromMinutes(5)),
                Policy.CacheOutput => pages.CacheOutput(),
                _ => pages.CacheOutput(p => p.Expire(TimeSpan.FromMinutes(5))),
            },
            services: s => s.AddOutputCache(),
            configureApp: a => a.UseOutputCache());
        var visitor = await Visitor.ReturningAsync(app);

        var payload = await visitor.DataAsync("/about");
        var document = await visitor.DocumentAsync("/about");
        var payloadAgain = await visitor.DataAsync("/about");
        var documentAgain = await visitor.DocumentAsync("/about");

        await AssertDocumentAsync(document);
        await AssertDocumentAsync(documentAgain);
        // Stored: the second payload is the first, render time included.
        Assert.Equal(payload.ToJsonString(), payloadAgain.ToJsonString());
        Assert.Single(app.Renderer.Payloads);
    }

    /// <summary>Every page entry is tagged with its path, so <c>RevalidatePath</c> evicts its documents and payloads.</summary>
    [Theory]
    [MemberData(nameof(Policies))]
    public async Task UnderAnyPolicy_RevalidatePath_EvictsThePage(Policy policy)
    {
        var loads = 0;
        await using var app = await StartAsync(policy, () => Interlocked.Increment(ref loads));
        var visitor = await Visitor.ReturningAsync(app);
        await visitor.DataAsync("/core");
        await visitor.DocumentAsync("/core");
        await visitor.DataAsync("/infra");

        await app.App.Services.GetRequiredService<IPageCache>().RevalidatePathAsync("/core", Ct);
        var payload = await visitor.DataAsync("/core");
        await visitor.DocumentAsync("/core");
        await visitor.DataAsync("/infra");

        Assert.Equal(4, (int?)payload["props"]!["load"]);
        Assert.Equal(5, loads);
    }

    private static void MapDraftModeOn(WebApplication app) =>
        app.MapGet("/api/draft/on", (IDraftMode draft) =>
        {
            draft.Enable();
            return Results.Ok();
        });

    /// <summary>An editor: a returning visitor who turned draft mode on (<see cref="MapDraftModeOn"/>).</summary>
    private static async Task<Visitor> EditorAsync(TestApp app)
    {
        var visitor = await Visitor.ReturningAsync(app);
        var on = await app.Client.GetAsync("/api/draft/on", Ct);
        var draft = on.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(DraftMode.CookieName + "=", StringComparison.Ordinal));
        return visitor with { Cookie = $"{visitor.Cookie}; {draft.Split(';')[0]}" };
    }

    /// <summary>A store that counts the entries written to it.</summary>
    private sealed class CountingStore(IOutputCacheStore store) : IOutputCacheStore
    {
        private int _writes;

        public int Writes => Volatile.Read(ref _writes);

        public ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken) => store.EvictByTagAsync(tag, cancellationToken);

        public ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken) => store.GetAsync(key, cancellationToken);

        public ValueTask SetAsync(string key, byte[] value, string[]? tags, TimeSpan validFor, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _writes);
            return store.SetAsync(key, value, tags, validFor, cancellationToken);
        }
    }

    /// <summary>
    /// What answers a draft request is one editor's preview: neither the page nor the redirect a loader returns for a
    /// client navigation is stored, so no other request, another editor's included, is served it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Policies))]
    public async Task UnderAnyPolicy_NothingAnsweringADraftRequest_IsStored(Policy policy)
    {
        var store = new CountingStore(SharedStore());
        var loads = 0;
        await using var app = await StartWithLoaderAsync(
            policy,
            PageResult<NoProps> (string team, IDraftMode draft) =>
            {
                Interlocked.Increment(ref loads);
                return team == "moved" && draft.IsEnabled ? Page.Redirect("/core") : Page.Props();
            },
            services: s => s.Replace(ServiceDescriptor.Singleton<IOutputCacheStore>(store)),
            map: MapDraftModeOn);
        var editor = await EditorAsync(app);
        var before = store.Writes;

        var redirect = await editor.DataAsync("/moved");
        var redirectAgain = await editor.DataAsync("/moved");
        var preview = await editor.DataAsync("/core");
        await AssertDocumentAsync(await editor.DocumentAsync("/core"));
        var draftWrites = store.Writes - before;
        // Outside draft mode, the same page is stored.
        var published = await Visitor.ReturningAsync(app);
        await published.DataAsync("/moved");
        await published.DataAsync("/moved");

        Assert.Equal("redirect", (string?)redirect["kind"]);
        Assert.Equal("redirect", (string?)redirectAgain["kind"]);
        Assert.True((bool?)preview["draftMode"]);
        Assert.Equal(0, draftWrites);
        Assert.Equal(1, store.Writes - before);
        Assert.Equal(5, loads);
    }

    /// <summary>Holds each render until <paramref name="renders"/> renders have started, or for 15 seconds.</summary>
    private sealed class Rendezvous(int renders)
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        /// <summary>Whether every render was there at once.</summary>
        public async Task<bool> ArriveAsync()
        {
            if (Interlocked.Increment(ref _arrived) == renders)
            {
                _all.TrySetResult();
            }

            return await Task.WhenAny(_all.Task, Task.Delay(TimeSpan.FromSeconds(15), Ct)) == _all.Task;
        }
    }

    /// <summary>
    /// The output cache holds back the requests for a key while one of them renders, to serve them what it stores.
    /// Draft requests share no key, so editors previewing a page at the same time do not wait for each other.
    /// </summary>
    [Theory]
    [MemberData(nameof(Policies))]
    public async Task UnderAnyPolicy_EditorsPreviewingAPageAtOnce_DoNotWaitForEachOther(Policy policy)
    {
        var together = new Rendezvous(2);
        await using var app = await StartWithLoaderAsync(
            policy, async () => Page.Props(new { Together = await together.ArriveAsync() }), map: MapDraftModeOn);
        var editor = await EditorAsync(app);

        var previews = await Task.WhenAll(editor.DataAsync("/core"), editor.DataAsync("/core"));

        Assert.All(previews, preview => Assert.True((bool?)preview["props"]!["together"]));
    }

    /// <summary>Nor do pages rendered for failed form posts share a key: visitors whose posts fail at once do not wait.</summary>
    [Theory]
    [MemberData(nameof(Policies))]
    public async Task UnderAnyPolicy_FormPostsFailingAtOnce_DoNotWaitForEachOther(Policy policy)
    {
        var together = new Rendezvous(2);
        await using var app = await StartWithLoaderAsync(
            policy,
            async () => Page.Props(new { Together = await together.ArriveAsync() }),
            map: a => a.MapAction("sign", ([Microsoft.AspNetCore.Mvc.FromForm] string? name) => ServerAction.Invalid("name", "required")));
        var (first, second) = (await Visitor.ReturningAsync(app), await Visitor.ReturningAsync(app));

        var failed = await Task.WhenAll(
            first.PostFormAsync("sign", ("name", ""), ("__netjes_return", "/core")),
            second.PostFormAsync("sign", ("name", ""), ("__netjes_return", "/core")));

        Assert.All(failed, response => Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode));
        Assert.Equal(2, app.Renderer.Payloads.Count);
        Assert.All(app.Renderer.Payloads, payload => Assert.True((bool?)JsonNode.Parse(payload)!["props"]!["together"]));
    }
}
