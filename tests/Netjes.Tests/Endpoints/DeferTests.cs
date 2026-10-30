using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;

namespace Netjes.Tests.Endpoints;

/// <summary>Page.Defer: loader values that are still being computed when the page starts rendering.</summary>
public class DeferTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<T> After<T>(int milliseconds, T value)
    {
        await Task.Delay(milliseconds);
        return value;
    }

    private static async Task<List<JsonNode>> LinesAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        return [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!)];
    }

    [Fact]
    public async Task DataRequest_StreamsThePayloadFirstThenEachValueAsItCompletes()
    {
        await using var app = await TestApp.StartAsync(
            ["app/posts/[id]/page.tsx"],
            map: a => a.MapPageLoader("/posts/[id]", (string id) => Page.Props(new
            {
                Title = $"Post {id}",
                Comments = Page.Defer(After(150, new[] { "slow" })),
                Likes = Page.Defer(After(10, 3)),
            })));

        var response = await app.GetDataAsync("/posts/1");
        var lines = await LinesAsync(response);

        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(3, lines.Count);
        var props = lines[0]["props"]!;
        Assert.Equal("Post 1", (string?)props["title"]);
        Assert.Equal("""{"$netjes":"deferred","id":"d0"}""", props["comments"]!.ToJsonString());
        Assert.Equal("""{"$netjes":"deferred","id":"d1"}""", props["likes"]!.ToJsonString());
        Assert.Equal("""{"id":"d1","value":3}""", lines[1].ToJsonString());
        Assert.Equal("""{"id":"d0","value":["slow"]}""", lines[2].ToJsonString());
    }

    [Fact]
    public async Task DataRequest_AFailedValue_IsReportedForItsBoundary()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props(new
            {
                Broken = Page.Defer(Task.FromException<int>(new InvalidOperationException("comments down"))),
            })));

        var lines = await LinesAsync(await app.GetDataAsync("/"));

        Assert.Equal("d0", (string?)lines[1]["id"]);
        Assert.Equal("comments down", (string?)lines[1]["error"]);
    }

    [Fact]
    public async Task DataRequest_FailedValueInProduction_HidesTheMessage()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configure: o => o.IncludeErrorDetails = false,
            map: a => a.MapPageLoader("/", () => Page.Props(new { Broken = Page.Defer(Task.FromException<int>(new InvalidOperationException("secret"))) })));

        var lines = await LinesAsync(await app.GetDataAsync("/"));

        Assert.Equal("Something went wrong", (string?)lines[1]["error"]);
    }

    [Fact]
    public async Task DataRequest_WithoutDeferredValues_IsPlainJson()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"]);

        var response = await app.GetDataAsync("/");

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task DocumentRequest_HandsEachValueToTheRendererAsItCompletes()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props(new { Likes = Page.Defer(After(30, 3)), Name = Page.Defer(Task.FromResult("Ada")) })));
        // A render that is still streaming (its Suspense boundaries waiting) while the values complete.
        app.Renderer.OnStream = _ => ["<html>", "<body>", "</body></html>"];

        await app.GetDocumentAsync("/");

        var options = Assert.Single(app.Renderer.StreamOptions);
        Assert.False(string.IsNullOrEmpty(options.RenderId));
        Assert.Equal("""{"$netjes":"deferred","id":"d0"}""", app.Renderer.LastPayload["props"]!["likes"]!.ToJsonString());
        Assert.Equal(
            [(options.RenderId!, "d0", "3", null), (options.RenderId!, "d1", "\"Ada\"", null)],
            app.Renderer.Resolved.OrderBy(r => r.Id).Select(r => (r.RenderId, r.Id, r.Json, r.Error)));
    }

    [Fact]
    public async Task DocumentRequest_WithoutDeferredValues_HasNoRenderId()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"]);

        await app.GetDocumentAsync("/");

        Assert.Null(Assert.Single(app.Renderer.StreamOptions).RenderId);
    }
}
