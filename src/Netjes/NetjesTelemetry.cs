using System.Diagnostics;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Netjes.Routing;

namespace Netjes;

/// <summary>
/// Netjes's traces and metrics. With OpenTelemetry: <c>.WithTracing(t =&gt; t.AddSource(NetjesTelemetry.Name))</c> and
/// <c>.WithMetrics(m =&gt; m.AddMeter(NetjesTelemetry.Name))</c>.
/// </summary>
public static class NetjesTelemetry
{
    /// <summary>The ActivitySource and Meter name.</summary>
    public const string Name = "Netjes";

    internal static readonly ActivitySource Source = new(Name, typeof(NetjesTelemetry).Assembly.GetName().Version?.ToString());

    internal static readonly Meter Meter = new(Name, typeof(NetjesTelemetry).Assembly.GetName().Version?.ToString());

    /// <summary>
    /// The bucket boundaries, in seconds, that the duration histograms advise: the ones ASP.NET Core advises for
    /// <c>http.server.request.duration</c>, 5 ms to 10 s. OpenTelemetry's default boundaries are for milliseconds, so
    /// without advice nearly every render of a page falls in the first bucket.
    /// </summary>
    internal static readonly double[] SecondsBuckets = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10];

    /// <summary>
    /// The bucket boundaries, in milliseconds, of the web vitals that are times: the vitals' own thresholds
    /// (good up to, poor from: INP 200 and 500 ms, TTFB 800 and 1800, FCP 1800 and 3000, LCP 2500 and 4000) are among
    /// them, so the share of good, mid and poor reports can be read from the buckets.
    /// </summary>
    internal static readonly double[] MillisecondsBuckets = [50, 100, 200, 300, 500, 800, 1000, 1500, 1800, 2500, 3000, 4000, 6000, 10000];

    /// <summary>The bucket boundaries of Cumulative Layout Shift, which has no unit: good up to 0.1, poor from 0.25.</summary>
    internal static readonly double[] LayoutShiftBuckets = [0.01, 0.025, 0.05, 0.1, 0.15, 0.25, 0.5, 1, 2.5];

    internal static InstrumentAdvice<double> Advise(double[] boundaries) => new() { HistogramBucketBoundaries = boundaries };

    /// <summary>Building the payload and, for documents, server rendering; tagged with the route and response kind.</summary>
    internal static readonly Histogram<double> RenderDuration =
        Meter.CreateHistogram<double>("netjes.render.duration", "s", "Time to answer a page request, including server rendering for documents.", tags: null, Advise(SecondsBuckets));

    /// <summary>The round trip to the Node sidecar for one server render.</summary>
    internal static readonly Histogram<double> SidecarDuration =
        Meter.CreateHistogram<double>("netjes.sidecar.render.duration", "s", "Time the Node sidecar took to render a document.", tags: null, Advise(SecondsBuckets));

    /// <summary>Documents sent for the browser to render because no Node sidecar was ready; tagged with the route.</summary>
    internal static readonly Counter<long> SidecarFallbacks =
        Meter.CreateCounter<long>("netjes.sidecar.fallbacks", "{document}", "Documents sent for the browser to render because no Node sidecar was ready.");

    private static readonly ConcurrentDictionary<string, string> Templates = new(StringComparer.Ordinal);

    /// <summary>
    /// The route template (<c>/blog/{slug}</c>, ASP.NET Core's <c>http.route</c>) of a route pattern in page-file syntax
    /// (<c>/blog/[slug]</c>, Netjes's <c>netjes.route</c>). Patterns come from the app's route table, so there are as
    /// many as it has routes.
    /// </summary>
    internal static string TemplateOf(string pattern) => Templates.GetOrAdd(pattern, RouteTable.TemplateOf);

    /// <summary>The tags of a route on a metric: <c>netjes.route</c>, and the <c>http.route</c> that goes with it.</summary>
    internal static TagList RouteTags(string? route)
    {
        var tags = new TagList { { "netjes.route", route } };
        if (route is not null)
        {
            tags.Add("http.route", TemplateOf(route));
        }

        return tags;
    }

    internal static Activity? StartActivity(string operation, string? route, ActivityKind kind = ActivityKind.Internal)
    {
        var activity = Source.StartActivity(operation, kind);
        if (activity is not null)
        {
            activity.DisplayName = route is null ? operation : $"{operation} {route}";
            activity.SetTag("netjes.route", route);
            if (route is not null)
            {
                activity.SetTag("http.route", TemplateOf(route));
            }
        }

        return activity;
    }

    /// <summary>A <c>netjes.server_action</c> span, named for the action (<c>netjes.server_action.name</c>).</summary>
    internal static Activity? StartServerAction(string name)
    {
        var activity = Source.StartActivity("netjes.server_action");
        if (activity is not null)
        {
            activity.DisplayName = $"netjes.server_action {name}";
            activity.SetTag("netjes.server_action.name", name);
        }

        return activity;
    }

    /// <summary>
    /// A <c>netjes.layout_loader</c> span for the loader of the layout at <paramref name="route"/> (its URL, as for a page),
    /// with the layout file's id (<c>netjes.layout</c>).
    /// </summary>
    internal static Activity? StartLayoutLoader(string route, string? layout)
    {
        var activity = StartActivity("netjes.layout_loader", route);
        activity?.SetTag("netjes.layout", layout);
        return activity;
    }

    /// <summary>
    /// Runs the rest of an endpoint filter chain inside the span <paramref name="start"/> opens, which an exception
    /// marks as an error. The tracing filter of page loaders, page actions, server actions and layout loaders.
    /// </summary>
    internal static async ValueTask<object?> TraceAsync(Func<Activity?> start, EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        using var activity = start();
        try
        {
            return await next(context);
        }
        catch (Exception ex)
        {
            activity.RecordException(ex);
            throw;
        }
    }

    /// <summary>
    /// Times one page response: a <c>netjes.render</c> span and the <c>netjes.render.duration</c> histogram. The span
    /// carries the page's file id, and the page package it is of; the histogram carries neither.
    /// </summary>
    internal static RenderScope StartRender(Microsoft.AspNetCore.Http.HttpContext http, string? route, string? page, string? package, bool isData) =>
        new(http, route, page, package, isData ? "data" : "document");

    /// <summary>Tags a span with the page package whose page it is about (<c>netjes.package</c>); nothing for the app's own.</summary>
    internal static Activity? WithPackage(this Activity? activity, string? package)
    {
        if (package is not null)
        {
            activity?.SetTag("netjes.package", package);
        }

        return activity;
    }

    internal sealed class RenderScope : IDisposable
    {
        private readonly Microsoft.AspNetCore.Http.HttpContext _http;
        private readonly string? _route;
        private readonly string _response;
        private readonly Activity? _activity;
        private readonly long _started = Stopwatch.GetTimestamp();

        internal RenderScope(Microsoft.AspNetCore.Http.HttpContext http, string? route, string? page, string? package, string response)
        {
            _http = http;
            _route = route;
            _response = response;
            _activity = StartActivity("netjes.render", route).WithPackage(package);
            _activity?.SetTag("netjes.page", page);
            _activity?.SetTag("netjes.response", response);
        }

        public void Dispose()
        {
            var status = _http.Response.StatusCode;
            _activity?.SetTag("http.response.status_code", status);
            _activity?.Dispose();
            RenderDuration.Record(
                Stopwatch.GetElapsedTime(_started).TotalSeconds,
                RouteTags(_route).With("netjes.response", _response).With("http.response.status_code", status));
        }
    }

    private static TagList With(this TagList tags, string key, object? value)
    {
        tags.Add(key, value);
        return tags;
    }

    internal static void RecordException(this Activity? activity, Exception exception)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity.AddException(exception);
    }
}
