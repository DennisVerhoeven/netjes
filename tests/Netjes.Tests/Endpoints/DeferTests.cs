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

    [Fact(Timeout = 10_000)]
    public async Task DataRequest_StreamsThePayloadFirstThenEachValueAsItCompletes()
    {
        // The slow value is held until the fast one has been read off the stream, so the order follows from the
        // streaming, not from two timers racing on a busy machine. The timeout ends the test if the stream ever waits
        // for every value before writing.
        var ct = TestContext.Current.CancellationToken;
        var slow = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await TestApp.StartAsync(
            ["app/posts/[id]/page.tsx"],
            map: a => a.MapPageLoader("/posts/[id]", (string id) => Page.Props(new
            {
                Title = $"Post {id}",
                Comments = Page.Defer(slow.Task),
                Likes = Page.Defer(After(10, 3)),
            })));

        var request = new HttpRequestMessage(HttpMethod.Get, "/posts/1");
        request.Headers.Add(NetjesHeaders.Data, "1");
        using var response = await app.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        var payload = JsonNode.Parse((await reader.ReadLineAsync(ct))!)!;
        var first = JsonNode.Parse((await reader.ReadLineAsync(ct))!)!;
        slow.SetResult(["slow"]);
        var second = JsonNode.Parse((await reader.ReadLineAsync(ct))!)!;

        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        var props = payload["props"]!;
        Assert.Equal("Post 1", (string?)props["title"]);
        Assert.Equal("""{"$netjes":"deferred","id":"d0"}""", props["comments"]!.ToJsonString());
        Assert.Equal("""{"$netjes":"deferred","id":"d1"}""", props["likes"]!.ToJsonString());
        Assert.Equal("""{"id":"d1","value":3}""", first.ToJsonString());
        Assert.Equal("""{"id":"d0","value":["slow"]}""", second.ToJsonString());
        Assert.Null(await reader.ReadLineAsync(ct));
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
