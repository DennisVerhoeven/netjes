namespace Blog.Features.Streaming;

/// <summary>
/// A page that streams: it renders at once, and a slow report (Page.Defer) fills in when it is ready. The page is
/// interactive before then, because the client hydrates the shell while the rest is still streaming.
/// </summary>
public static class StreamingDemo
{
    public sealed record SlowReport(int Items, string GeneratedBy);

    public sealed record StreamingProps(string Title, Deferred<SlowReport> Report);

    public static void MapStreamingDemo(this IEndpointRouteBuilder app)
    {
        app.MapPageLoader("/streaming", (HttpContext http) =>
            Page.Props(new StreamingProps("Streaming", Page.Defer(SlowReportAsync(http.RequestAborted)))));
    }

    private static async Task<SlowReport> SlowReportAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(2500), cancellationToken);
        return new SlowReport(42, "a deliberately slow C# task");
    }
}
