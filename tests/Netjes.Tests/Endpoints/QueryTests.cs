using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Netjes.Tests.Endpoints;

public class QueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task WithQuery_SeedsTheQueryWithKeyDataAndServerTime()
    {
        await using var app = await TestApp.StartAsync(
            ["app/blog/[slug]/page.tsx"],
            map: a => a.MapPageLoader("/blog/[slug]", (string slug) =>
                Page.Props(new { Title = slug }).WithQuery(["likes", slug], new { Slug = slug, Likes = 3 })),
            services: s => s.AddSingleton<TimeProvider>(new FixedTimeProvider(Now)));

        var payload = await app.GetDataJsonAsync("/blog/hello");

        var query = Assert.Single(payload["queries"]!.AsArray())!;
        Assert.Equal(["likes", "hello"], query["key"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(3, (int?)query["data"]!["likes"]);
        Assert.Equal(Now.ToUnixTimeMilliseconds(), (long?)query["updatedAt"]);
    }

    [Fact]
    public async Task WithQuery_CalledTwiceAndCombinedWithStatus_KeepsEveryQuery()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () =>
                Page.Props().WithQuery(["a"], 1).WithStatus(422).WithQuery(["b", 2], new[] { "x" })));

        var response = await app.GetDataAsync("/");
        var payload = await app.GetDataJsonAsync("/");

        Assert.Equal(422, (int)response.StatusCode);
        Assert.Equal(["[\"a\"]", "[\"b\",2]"], payload["queries"]!.AsArray().Select(q => q!["key"]!.ToJsonString()));
    }

    [Fact]
    public async Task Payload_WithoutQueries_OmitsTheField()
    {
        await using var app = await TestApp.StartAsync(["app/about/page.tsx"]);

        var payload = await app.GetDataJsonAsync("/about");

        Assert.Null(payload["queries"]);
    }

    [Fact]
    public async Task WithQuery_OnDocumentRequests_ReachesTheServerRenderer()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props().WithQuery(["greeting"], "hi")));

        await app.GetDocumentAsync("/");

        Assert.Equal("hi", (string?)app.Renderer.LastPayload["queries"]![0]!["data"]);
    }

    [Fact]
    public async Task Payload_CarriesTheServerTimeItWasRenderedAt()
    {
        await using var app = await TestApp.StartAsync(
            ["app/about/page.tsx"], services: s => s.AddSingleton<TimeProvider>(new FixedTimeProvider(Now)));

        var payload = await app.GetDataJsonAsync("/about");

        Assert.Equal(Now.ToUnixTimeMilliseconds(), (long?)payload["renderedAt"]);
    }

    [Fact]
    public async Task DataResponses_CarryTheCurrentServerTime_EvenWhenServedFromTheOutputCache()
    {
        var time = new FixedTimeProvider(Now);
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props().WithQuery(["a"], 1)).CachePage(TimeSpan.FromMinutes(5)),
            services: s => s.AddSingleton<TimeProvider>(time).AddOutputCache(),
            configureApp: a => a.UseOutputCache());

        await app.GetDataAsync("/");
        time.Advance(TimeSpan.FromMinutes(2));
        var cached = await app.GetDataAsync("/");
        var payload = JsonNode.Parse(await cached.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;

        Assert.Equal(Now.ToUnixTimeMilliseconds(), (long?)payload["renderedAt"]);
        Assert.Equal(Now.AddMinutes(2).ToUnixTimeMilliseconds().ToString(), cached.Headers.GetValues(NetjesHeaders.ServerTime).Single());
    }

    [Fact]
    public void WithQuery_EmptyKey_Throws()
    {
        Assert.Throws<ArgumentException>(() => Page.Props().WithQuery([], 1));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
