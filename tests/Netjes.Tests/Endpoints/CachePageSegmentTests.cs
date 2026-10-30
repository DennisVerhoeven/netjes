using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Netjes.Tests.Endpoints;

/// <summary>
/// How a cached page's entries are keyed for client navigations: by which of its layout segments the client holds,
/// never by the text of headers anyone can make up (the cache's size limit does not count key text), and, for an entry
/// that reuses a segment, by the secret that signed its key, so instances on one store never serve each other's.
/// </summary>
public class CachePageSegmentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Files = ["app/layout.tsx", "app/[team]/layout.tsx", "app/[team]/page.tsx"];

    /// <summary>The cached page under the layout, and an uncached one that hands out the layout's key.</summary>
    private static readonly string[] InstanceFiles = [.. Files, "app/[team]/settings/page.tsx"];

    private static async Task<TestApp> StartAsync(Func<int> load, ConcurrentQueue<string> keys) =>
        await TestApp.StartAsync(
            Files,
            map: a =>
            {
                a.MapLayoutLoader("/[team]", (string team) => Page.Props(new { Team = team }));
                a.MapPageLoader("/[team]", () => Page.Props(new { Load = load() })).CachePage(TimeSpan.FromMinutes(5));
            },
            services: s =>
            {
                s.AddOutputCache();
                RecordingStore.Install(s, keys);
            },
            configureApp: a => a.UseOutputCache());

    private static async Task<JsonNode> DataAsync(TestApp app, string url, string? segments = null, string data = "1")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(NetjesHeaders.Data, data);
        if (segments is not null) request.Headers.Add(NetjesHeaders.Segments, segments);
        var response = await app.Client.SendAsync(request, Ct);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
    }

    /// <summary>A client navigation with these headers as given: names in any casing, a name more than once.</summary>
    private static async Task<JsonNode> DataWithHeadersAsync(TestApp app, string url, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (name, value) in headers)
        {
            Assert.True(request.Headers.TryAddWithoutValidation(name, value));
        }

        var response = await app.Client.SendAsync(request, Ct);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
    }

    [Fact]
    public async Task MadeUpSegmentsHeaders_ShareOneEntry_WhoseKeyLeavesTheirTextOut()
    {
        var loads = 0;
        var keys = new ConcurrentQueue<string>();
        await using var app = await StartAsync(() => Interlocked.Increment(ref loads), keys);

        for (var i = 0; i < 5; i++)
        {
            await DataAsync(app, "/core", segments: new string('x', 2000) + i);
        }

        Assert.Equal(1, loads);
        Assert.NotEmpty(keys);
        Assert.All(keys, key => Assert.DoesNotContain("xxxx", key, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnyDataHeaderValue_SharesTheDataEntry_AndDocumentsKeepTheirOwn()
    {
        var loads = 0;
        await using var app = await StartAsync(() => Interlocked.Increment(ref loads), new ConcurrentQueue<string>());

        var first = await DataAsync(app, "/core");
        var madeUp = await DataAsync(app, "/core", data: new string('y', 2000));
        var document = await app.GetDocumentAsync("/core");
        var again = await DataAsync(app, "/core");

        Assert.Equal(2, loads);
        Assert.Equal(1, (int?)first["props"]!["load"]);
        Assert.Equal(1, (int?)madeUp["props"]!["load"]);
        Assert.Equal(1, (int?)again["props"]!["load"]);
        Assert.Equal("text/html", document.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ClientsHoldingTheLayout_GetTheEntryThatReusesIt_AndOthersTheFullOne()
    {
        var loads = 0;
        await using var app = await StartAsync(() => Interlocked.Increment(ref loads), new ConcurrentQueue<string>());
        var full = await DataAsync(app, "/core");
        var key = (string)full["segments"]![1]!["key"]!;

        var holding = await DataAsync(app, "/core", segments: key);
        var holdingAgain = await DataAsync(app, "/core", segments: $"made-up, {key}");
        var notHolding = await DataAsync(app, "/core", segments: "made-up");
        var otherTeam = await DataAsync(app, "/infra", segments: key);

        Assert.Equal("core", (string?)full["segments"]![1]!["props"]!["team"]);
        Assert.True((bool?)holding["segments"]![1]!["reused"]);
        Assert.Null(holding["segments"]![1]!["props"]);
        Assert.Equal(key, (string?)holding["segments"]![1]!["key"]);
        Assert.Equal(holding.ToJsonString(), holdingAgain.ToJsonString());
        Assert.Equal(full.ToJsonString(), notHolding.ToJsonString());
        Assert.Equal("infra", (string?)otherTeam["segments"]![1]!["props"]!["team"]);
        Assert.Equal(3, loads);
    }

    [Fact]
    public async Task ARenderThatReusedSegmentsItsKeyDoesNotSay_IsNotStored()
    {
        await using var app = await TestApp.StartAsync(
            Files,
            map: a =>
            {
                a.MapLayoutLoader("/[team]", (string team) => Page.Props(new { Team = team }));
                // Not a page endpoint: the cache cannot tell which layouts the page it renders has.
                a.MapGet("/elsewhere/{team}", () => Page.Render("/[team]")).CachePage(TimeSpan.FromMinutes(5));
            },
            services: s => s.AddOutputCache(),
            configureApp: a => a.UseOutputCache());
        var key = (string)(await DataAsync(app, "/core"))["segments"]![1]!["key"]!;

        var holding = await DataAsync(app, "/elsewhere/core", segments: key);
        var notHolding = await DataAsync(app, "/elsewhere/core");

        Assert.True((bool?)holding["segments"]![1]!["reused"]);
        Assert.Null(notHolding["segments"]![1]!["reused"]);
        Assert.Equal("core", (string?)notHolding["segments"]![1]!["props"]?["team"]);
    }

    private static string NewSecret() => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    /// <summary>ASP.NET Core's in-memory store, out of a container of its own so apps can share it (as Redis).</summary>
    private static IOutputCacheStore SharedStore(ConcurrentQueue<string> keys) =>
        new RecordingStore(new ServiceCollection().AddOptions().AddOutputCache().BuildServiceProvider().GetRequiredService<IOutputCacheStore>(), keys);

    /// <summary>An instance of an app on a shared output-cache store; null leaves its segment key secret random.</summary>
    private static Task<TestApp> InstanceAsync(IOutputCacheStore store, string? secret, Func<int> load) =>
        TestApp.StartAsync(
            InstanceFiles,
            map: a =>
            {
                a.MapLayoutLoader("/[team]", (string team) => Page.Props(new { Team = team }));
                a.MapPageLoader("/[team]", () => Page.Props(new { Load = load() })).CachePage(TimeSpan.FromMinutes(5));
            },
            configure: o => o.SegmentKeySecret = secret,
            services: s =>
            {
                s.AddOutputCache();
                s.Replace(ServiceDescriptor.Singleton(store));
            },
            configureApp: a => a.UseOutputCache());

    /// <summary>The team layout's key, from the uncached page under it: nothing is stored on the way.</summary>
    private static async Task<string> LayoutKeyAsync(TestApp app, string team = "core") =>
        (string)Layout(await DataAsync(app, $"/{team}/settings"))["key"]!;

    private static JsonNode Layout(JsonNode payload) => payload["segments"]![1]!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithDifferentSecrets_AnEntryThatReusesALayout_IsNeverServedByTheOtherInstance(bool configured)
    {
        var store = SharedStore(new ConcurrentQueue<string>());
        var (aLoads, bLoads) = (0, 0);
        // Unset, each instance makes its own secret; set, they differ (a secret rotated while the store kept entries).
        await using var a = await InstanceAsync(store, configured ? NewSecret() : null, () => Interlocked.Increment(ref aLoads));
        await using var b = await InstanceAsync(store, configured ? NewSecret() : null, () => Interlocked.Increment(ref bLoads));
        var keyFromA = await LayoutKeyAsync(a);
        var keyFromB = await LayoutKeyAsync(b);

        var storedByB = await DataAsync(b, "/core", segments: keyFromB);
        var onA = await DataAsync(a, "/core", segments: keyFromA);

        Assert.NotEqual(keyFromA, keyFromB);
        Assert.True((bool?)Layout(storedByB)["reused"]);
        Assert.Equal(keyFromB, (string?)Layout(storedByB)["key"]);
        Assert.True((bool?)Layout(onA)["reused"]);
        Assert.Equal(keyFromA, (string?)Layout(onA)["key"]);
        Assert.Equal(1, aLoads);
        Assert.Equal(1, bLoads);
    }

    [Fact]
    public async Task WithASharedSecret_EitherInstanceServesTheEntryThatReusesTheLayout()
    {
        var store = SharedStore(new ConcurrentQueue<string>());
        var secret = NewSecret();
        var (aLoads, bLoads) = (0, 0);
        await using var a = await InstanceAsync(store, secret, () => Interlocked.Increment(ref aLoads));
        await using var b = await InstanceAsync(store, secret, () => Interlocked.Increment(ref bLoads));
        var key = await LayoutKeyAsync(a);

        var storedByB = await DataAsync(b, "/core", segments: key);
        var servedByA = await DataAsync(a, "/core", segments: key);

        Assert.Equal(key, await LayoutKeyAsync(b));
        Assert.True((bool?)Layout(servedByA)["reused"]);
        Assert.Equal(key, (string?)Layout(servedByA)["key"]);
        Assert.Equal(storedByB.ToJsonString(), servedByA.ToJsonString());
        Assert.Equal(0, aLoads);
        Assert.Equal(1, bLoads);
    }

    /// <summary>
    /// Whatever a visitor sends that this instance did not sign for the layout (nothing, a made-up key, the other
    /// instance's key, its key for another team, its own key re-cased or re-encoded) gets the full payload, whoever
    /// stored it, and never the other instance's payload that reuses the layout. Documents reuse nothing, keys or not.
    /// </summary>
    [Fact]
    public async Task KeysTheInstanceDidNotSign_GetTheSharedFullEntry_WhoeverStoredIt()
    {
        var store = SharedStore(new ConcurrentQueue<string>());
        var (aLoads, bLoads) = (0, 0);
        await using var a = await InstanceAsync(store, null, () => Interlocked.Increment(ref aLoads));
        await using var b = await InstanceAsync(store, null, () => Interlocked.Increment(ref bLoads));
        var keyFromA = await LayoutKeyAsync(a);
        var keyFromB = await LayoutKeyAsync(b);
        var otherTeamFromA = await LayoutKeyAsync(a, "infra");
        // B stores both payloads, the one that reuses its key and the full one, and the document.
        var reusedByB = await DataAsync(b, "/core", segments: keyFromB);
        var full = await DataAsync(b, "/core");
        await b.GetDocumentAsync("/core");
        string?[] notSignedByA =
        [
            null,
            "",
            "made-up",
            new string('A', keyFromA.Length),
            keyFromB,
            otherTeamFromA,
            keyFromA.ToUpperInvariant(),
            keyFromA.ToLowerInvariant(),
            keyFromA[..^1],
            keyFromA + "=",
            $"\"{keyFromA}\"",
            $"{keyFromA};made-up",
            // The same bytes in standard base64, and every character percent-encoded.
            Convert.ToBase64String(Base64Url.DecodeFromChars(keyFromA)),
            string.Concat(keyFromA.Select(c => $"%{(int)c:X2}")),
        ];

        // A re-cased key that happens to be the same (one without letters) is no variant.
        foreach (var segments in notSignedByA.Where(s => s != keyFromA))
        {
            var served = await DataAsync(a, "/core", segments: segments);
            Assert.Equal(full.ToJsonString(), served.ToJsonString());
        }

        var document = new HttpRequestMessage(HttpMethod.Get, "/core");
        document.Headers.Add(NetjesHeaders.Segments, keyFromA);
        var servedDocument = await a.Client.SendAsync(document, Ct);

        Assert.True((bool?)Layout(reusedByB)["reused"]);
        Assert.Null(Layout(full)["reused"]);
        Assert.Equal("core", (string?)Layout(full)["props"]!["team"]);
        Assert.Equal("text/html", servedDocument.Content.Headers.ContentType?.MediaType);
        Assert.Empty(a.Renderer.Payloads);
        Assert.Equal(0, aLoads);
        Assert.Equal(3, bLoads);
    }

    /// <summary>
    /// This instance's own key, however the request carries it (header names in any casing, a header line per key,
    /// spaces and commas, next to keys it did not sign), gets this instance's payload that reuses the layout, never
    /// the other instance's.
    /// </summary>
    [Fact]
    public async Task TheInstancesOwnKey_HoweverTheHeadersCarryIt_GetsItsOwnEntryThatReusesTheLayout()
    {
        var store = SharedStore(new ConcurrentQueue<string>());
        var (aLoads, bLoads) = (0, 0);
        await using var a = await InstanceAsync(store, null, () => Interlocked.Increment(ref aLoads));
        await using var b = await InstanceAsync(store, null, () => Interlocked.Increment(ref bLoads));
        var keyFromA = await LayoutKeyAsync(a);
        var keyFromB = await LayoutKeyAsync(b);
        await DataAsync(b, "/core", segments: keyFromB);

        var own = await DataAsync(a, "/core", segments: keyFromA);
        (string, string)[][] carried =
        [
            [("x-netjes-data", "1"), ("x-netjes-segments", keyFromA)],
            [("X-NETJES-DATA", "1"), ("X-NETJES-SEGMENTS", keyFromA)],
            [(NetjesHeaders.Data, "1"), (NetjesHeaders.Segments, "made-up"), (NetjesHeaders.Segments, keyFromA)],
            [(NetjesHeaders.Data, "1"), (NetjesHeaders.Segments, $"  made-up ,{keyFromA} ")],
            [(NetjesHeaders.Data, "1"), (NetjesHeaders.Segments, $"{keyFromB},{keyFromA}")],
            [(NetjesHeaders.Data, "1"), (NetjesHeaders.Segments, $"{keyFromA} {keyFromA}")],
        ];
        var served = new List<JsonNode>();
        foreach (var headers in carried)
        {
            served.Add(await DataWithHeadersAsync(a, "/core", headers));
        }

        Assert.True((bool?)Layout(own)["reused"]);
        Assert.Equal(keyFromA, (string?)Layout(own)["key"]);
        Assert.All(served, payload => Assert.Equal(own.ToJsonString(), payload.ToJsonString()));
        Assert.Equal(1, aLoads);
        Assert.Equal(1, bLoads);
    }

    /// <summary>
    /// The cache keys of entries with and without a reused layout hold neither the secret, in any of its usual
    /// forms, nor the visitor's key.
    /// </summary>
    [Fact]
    public async Task TheCacheKeys_HoldNeitherTheSecretNorTheVisitorsKey()
    {
        var keys = new ConcurrentQueue<string>();
        var secret = NewSecret();
        await using var app = await InstanceAsync(SharedStore(keys), secret, () => 0);
        var key = await LayoutKeyAsync(app);

        var reused = await DataAsync(app, "/core", segments: key);
        await DataAsync(app, "/core");

        var bytes = Encoding.UTF8.GetBytes(secret);
        string[] secretForms = [secret, Convert.ToHexString(bytes), Convert.ToBase64String(bytes), Base64Url.EncodeToString(bytes)];
        Assert.True((bool?)Layout(reused)["reused"]);
        Assert.NotEmpty(keys);
        Assert.All(keys, cacheKey =>
        {
            Assert.DoesNotContain(key, cacheKey, StringComparison.Ordinal);
            Assert.All(secretForms, form => Assert.DoesNotContain(form, cacheKey, StringComparison.OrdinalIgnoreCase));
        });
    }

    /// <summary>Records the keys the output cache looks up and stores under.</summary>
    private sealed class RecordingStore(IOutputCacheStore inner, ConcurrentQueue<string> keys) : IOutputCacheStore
    {
        public static void Install(IServiceCollection services, ConcurrentQueue<string> keys)
        {
            var original = services.Single(d => d.ServiceType == typeof(IOutputCacheStore));
            services.Remove(original);
            services.AddSingleton<IOutputCacheStore>(sp => new RecordingStore((IOutputCacheStore)original.ImplementationFactory!(sp), keys));
        }

        public ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken) => inner.EvictByTagAsync(tag, cancellationToken);

        public ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
        {
            keys.Enqueue(key);
            return inner.GetAsync(key, cancellationToken);
        }

        public ValueTask SetAsync(string key, byte[] value, string[]? tags, TimeSpan validFor, CancellationToken cancellationToken)
        {
            keys.Enqueue(key);
            return inner.SetAsync(key, value, tags, validFor, cancellationToken);
        }
    }
}
