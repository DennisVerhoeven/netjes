using System.Diagnostics;
using System.Diagnostics.Metrics;

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

    /// <summary>Building the payload and, for documents, server rendering; tagged with the route and response kind.</summary>
    internal static readonly Histogram<double> RenderDuration =
        Meter.CreateHistogram<double>("netjes.render.duration", "s", "Time to answer a page request, including server rendering for documents.");

    /// <summary>The round trip to the Node sidecar for one server render.</summary>
    internal static readonly Histogram<double> SidecarDuration =
        Meter.CreateHistogram<double>("netjes.sidecar.render.duration", "s", "Time the Node sidecar took to render a document.");

    /// <summary>Documents sent for the browser to render because no Node sidecar was ready; tagged with the route.</summary>
    internal static readonly Counter<long> SidecarFallbacks =
        Meter.CreateCounter<long>("netjes.sidecar.fallbacks", "{document}", "Documents sent for the browser to render because no Node sidecar was ready.");

    internal static Activity? StartActivity(string operation, string? route, ActivityKind kind = ActivityKind.Internal)
    {
        var activity = Source.StartActivity(operation, kind);
        if (activity is not null)
        {
            activity.DisplayName = route is null ? operation : $"{operation} {route}";
            activity.SetTag("netjes.route", route);
        }

        return activity;
    }

    /// <summary>Times one page response: a <c>netjes.render</c> span and the <c>netjes.render.duration</c> histogram.</summary>
    internal static RenderScope StartRender(Microsoft.AspNetCore.Http.HttpContext http, string? route, string? page, bool isData) =>
        new(http, route, page, isData ? "data" : "document");

    internal sealed class RenderScope : IDisposable
    {
        private readonly Microsoft.AspNetCore.Http.HttpContext _http;
        private readonly string? _route;
        private readonly string _response;
        private readonly Activity? _activity;
        private readonly long _started = Stopwatch.GetTimestamp();

        internal RenderScope(Microsoft.AspNetCore.Http.HttpContext http, string? route, string? page, string response)
        {
            _http = http;
            _route = route;
            _response = response;
            _activity = StartActivity("netjes.render", route);
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
                new KeyValuePair<string, object?>("netjes.route", _route),
                new KeyValuePair<string, object?>("netjes.response", _response),
                new KeyValuePair<string, object?>("http.response.status_code", status));
        }
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
