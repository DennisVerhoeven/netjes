using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Netjes.Routing;

namespace Netjes;

/// <summary>
/// <c>POST /_netjes/vitals</c>: Core Web Vitals reported by <c>&lt;WebVitals /&gt;</c> (from <c>netjes/vitals</c>),
/// recorded as <c>netjes.web_vitals.*</c> histograms on the Netjes meter. Mapped by <c>MapPages()</c> when
/// <see cref="NetjesOptions.CollectWebVitals"/> is on. Anyone can post, so reports are validated and tags bounded.
/// </summary>
internal static class WebVitalsEndpoint
{
    public const string Path = "/_netjes/vitals";

    private const int MaxBodyBytes = 1024;

    private static readonly Dictionary<string, (Histogram<double> Histogram, double Max)> Metrics = new(StringComparer.Ordinal)
    {
        ["LCP"] = (NetjesTelemetry.Meter.CreateHistogram<double>("netjes.web_vitals.lcp", "ms", "Largest Contentful Paint"), 120_000),
        ["INP"] = (NetjesTelemetry.Meter.CreateHistogram<double>("netjes.web_vitals.inp", "ms", "Interaction to Next Paint"), 120_000),
        ["CLS"] = (NetjesTelemetry.Meter.CreateHistogram<double>("netjes.web_vitals.cls", null, "Cumulative Layout Shift"), 100),
        ["FCP"] = (NetjesTelemetry.Meter.CreateHistogram<double>("netjes.web_vitals.fcp", "ms", "First Contentful Paint"), 120_000),
        ["TTFB"] = (NetjesTelemetry.Meter.CreateHistogram<double>("netjes.web_vitals.ttfb", "ms", "Time to First Byte"), 120_000),
    };

    private static readonly HashSet<string> Ratings = ["good", "needs-improvement", "poor"];

    private sealed record Report(string? Name, double? Value, string? Rating, string? Route);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints
            .MapPost(Path, HandleAsync)
            .DisableAntiforgery()
            .WithDisplayName("Netjes web vitals")
            .ExcludeFromDescription();

    private static async Task<IResult> HandleAsync(HttpContext http, IRouteTableProvider routes)
    {
        // sendBeacon posts text/plain (no preflight); read the body ourselves, capped.
        var buffer = new byte[MaxBodyBytes + 1];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = await http.Request.Body.ReadAsync(buffer.AsMemory(length), http.RequestAborted)) > 0)
        {
            length += read;
        }

        if (length > MaxBodyBytes)
        {
            return Results.BadRequest();
        }

        Report? report;
        try
        {
            report = JsonSerializer.Deserialize<Report>(buffer.AsSpan(0, length), Json);
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }

        if (report is not { Name: { } name, Value: { } value, Rating: { } rating }
            || !Metrics.TryGetValue(name, out var metric)
            || !double.IsFinite(value) || value < 0 || value > metric.Max
            || !Ratings.Contains(rating))
        {
            return Results.BadRequest();
        }

        // Only route patterns the app has: anything else would let a client invent unlimited tag values.
        var route = report.Route is { } pattern && routes.Current.Find(pattern) is not null ? pattern : "unknown";
        metric.Histogram.Record(
            value,
            new KeyValuePair<string, object?>("netjes.route", route),
            new KeyValuePair<string, object?>("netjes.web_vitals.rating", rating));
        return Results.NoContent();
    }
}
