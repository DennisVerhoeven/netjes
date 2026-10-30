using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Builder;
using Netjes.Tests.Endpoints;

namespace Netjes.Tests.Telemetry;

/// <summary>Spans and metrics under the "Netjes" ActivitySource and Meter, for OpenTelemetry to export.</summary>
public sealed class TelemetryTests : IDisposable
{
    private readonly ConcurrentQueue<Activity> _activities = new();
    private readonly ConcurrentQueue<(string Instrument, double Value, Dictionary<string, object?> Tags)> _measurements = new();
    private readonly ActivityListener _activityListener;
    private readonly MeterListener _meterListener = new();

    public TelemetryTests()
    {
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == NetjesTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _activities.Enqueue,
        };
        ActivitySource.AddActivityListener(_activityListener);
        _meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == NetjesTelemetry.Name)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            _measurements.Enqueue((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        _meterListener.Start();
    }

    // Other test classes run in parallel; only look at this class's routes.
    private IEnumerable<Activity> Spans(string route) =>
        _activities.Where(a => (string?)a.GetTagItem("netjes.route") == route);

    [Fact]
    public async Task APageRequest_RecordsLoaderAndRenderSpans_AndARenderDuration()
    {
        await using var app = await TestApp.StartAsync(
            ["app/telemetry/[id]/page.tsx"],
            map: a => a.MapPageLoader("/telemetry/[id]", (string id) => Page.Props(new { id })));

        await app.GetDocumentAsync("/telemetry/1");

        var spans = Spans("/telemetry/[id]").ToList();
        var loader = Assert.Single(spans, s => s.OperationName == "netjes.loader");
        var render = Assert.Single(spans, s => s.OperationName == "netjes.render");
        Assert.Equal("netjes.loader /telemetry/[id]", loader.DisplayName);
        Assert.Equal("app/telemetry/[id]/page.tsx", render.GetTagItem("netjes.page"));
        Assert.Equal("document", render.GetTagItem("netjes.response"));
        Assert.Equal(200, render.GetTagItem("http.response.status_code"));
        var duration = Assert.Single(_measurements, m => m.Instrument == "netjes.render.duration" && (string?)m.Tags["netjes.route"] == "/telemetry/[id]");
        Assert.Equal("document", duration.Tags["netjes.response"]);
        Assert.True(duration.Value >= 0);
    }

    [Fact]
    public async Task APageAction_RecordsAnActionSpan()
    {
        await using var app = await TestApp.StartAsync(
            ["app/telemetry-form/page.tsx"],
            map: a => a.MapPageAction("/telemetry-form", () => Page.Redirect("/")).DisableAntiforgery());

        await app.Client.PostAsync("/telemetry-form", new FormUrlEncodedContent([]), TestContext.Current.CancellationToken);

        Assert.Single(Spans("/telemetry-form"), s => s.OperationName == "netjes.action");
    }

    [Fact]
    public async Task ALoaderThatThrows_MarksItsSpanAsAnError()
    {
        await using var app = await TestApp.StartAsync(
            ["app/telemetry-broken/page.tsx"],
            map: a => a.MapPageLoader("/telemetry-broken", Func<PageResult> () => throw new InvalidOperationException("db down")));

        await app.GetDataAsync("/telemetry-broken");

        var loader = Assert.Single(Spans("/telemetry-broken"), s => s.OperationName == "netjes.loader");
        Assert.Equal(ActivityStatusCode.Error, loader.Status);
        Assert.Equal("db down", loader.StatusDescription);
    }

    [Fact]
    public async Task SidecarRenders_AreSpans_AndCarryTheTraceToNode()
    {
        var options = new NetjesOptions
        {
            Mode = NetjesMode.Production,
            SidecarScript = Path.Combine(AppContext.BaseDirectory, "Sidecar", "fake-sidecar.mjs"),
            StartupTimeout = TimeSpan.FromSeconds(20),
        };
        var environment = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { ContentRootPath = AppContext.BaseDirectory };
        await using var sidecar = new Netjes.Sidecar.NodeSidecar(
            new NetjesContext(Microsoft.Extensions.Options.Options.Create(options), environment),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Netjes.Sidecar.NodeSidecar>.Instance);
        await sidecar.StartAsync(TestContext.Current.CancellationToken);

        var html = await sidecar.RenderAsync("""{"page":"traceparent"}""", TestContext.Current.CancellationToken);

        // The header is W3C traceparent in the span's trace (HttpClient may add a span of its own below it).
        var span = Assert.Single(_activities, a => a.OperationName == "netjes.sidecar.render" && html.Contains(a.TraceId.ToHexString()));
        Assert.Equal(ActivityKind.Client, span.Kind);
        Assert.Matches($"^<html>00-{span.TraceId.ToHexString()}-[0-9a-f]{{16}}-01</html>$", html);
        Assert.Contains(_measurements, m => m.Instrument == "netjes.sidecar.render.duration");
    }

    public void Dispose()
    {
        _activityListener.Dispose();
        _meterListener.Dispose();
    }
}
