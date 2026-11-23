using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Json;
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
    private readonly ConcurrentQueue<Instrument> _instruments = new();

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
                _instruments.Enqueue(instrument);
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
    public async Task APagePackagesPage_TagsItsSpansWithThePackage_AndItsFileId()
    {
        const string help = TestPagePackage.Name;
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx", "app/telemetry-help/own/page.tsx"],
            prepareRoot: root => TestPagePackage.Install(root, ["page.tsx", "form/page.tsx", "loaded/page.tsx"]),
            map: a =>
            {
                var mount = a.MapPagePackage(TestPagePackage.Descriptor, "/telemetry-help");
                mount.MapPageLoader("/loaded", () => Page.Props(new { }));
                mount.MapPageAction("/form", () => Page.Redirect("/")).DisableAntiforgery();
            });

        await app.GetDocumentAsync("/telemetry-help");
        await app.GetDocumentAsync("/telemetry-help/loaded");
        await app.Client.PostAsync("/telemetry-help/form", new FormUrlEncodedContent([]), TestContext.Current.CancellationToken);
        await app.GetDocumentAsync("/telemetry-help/own");

        var render = Assert.Single(Spans("/telemetry-help"), s => s.OperationName == "netjes.render");
        Assert.Equal($"{help}/app/page.tsx", render.GetTagItem("netjes.page"));
        Assert.Equal(help, render.GetTagItem("netjes.package"));
        Assert.Equal(help, Assert.Single(Spans("/telemetry-help/loaded"), s => s.OperationName == "netjes.loader").GetTagItem("netjes.package"));
        Assert.Equal(help, Assert.Single(Spans("/telemetry-help/loaded"), s => s.OperationName == "netjes.render").GetTagItem("netjes.package"));
        Assert.Equal(help, Assert.Single(Spans("/telemetry-help/form"), s => s.OperationName == "netjes.action").GetTagItem("netjes.package"));
        // The app's own page under the mount is the app's, and the histograms stay as they are: one series per route.
        Assert.Null(Assert.Single(Spans("/telemetry-help/own"), s => s.OperationName == "netjes.render").GetTagItem("netjes.package"));
        Assert.DoesNotContain(_measurements, m => m.Tags.ContainsKey("netjes.package"));
    }

    [Fact]
    public async Task TheDurationHistograms_AdviseTheBucketsOfAspNetCoresRequestDuration()
    {
        await using var app = await TestApp.StartAsync(["app/telemetry-buckets/page.tsx"]);
        await app.GetDocumentAsync("/telemetry-buckets");

        // The boundaries ASP.NET Core advises for http.server.request.duration, in seconds, as the SDK's default
        // boundaries are for milliseconds.
        double[] expected = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10];
        foreach (var name in new[] { "netjes.render.duration", "netjes.sidecar.render.duration" })
        {
            var histogram = Assert.IsType<Histogram<double>>(Assert.Single(_instruments.Distinct(), i => i.Name == name));
            Assert.Equal(expected, histogram.Advice?.HistogramBucketBoundaries);
        }
    }

    [Fact]
    public async Task APageRequest_TagsItsSpansAndRenderDuration_WithTheRouteTemplate()
    {
        await using var app = await TestApp.StartAsync(
            ["app/telemetry-template/[id]/page.tsx"],
            map: a => a.MapPageLoader("/telemetry-template/[id]", (string id) => Page.Props(new { id })));

        await app.GetDocumentAsync("/telemetry-template/1");

        // ASP.NET Core's http.route notation, beside netjes.route in page-file syntax.
        var spans = Spans("/telemetry-template/[id]").ToList();
        Assert.Equal("/telemetry-template/{id}", Assert.Single(spans, s => s.OperationName == "netjes.loader").GetTagItem("http.route"));
        Assert.Equal("/telemetry-template/{id}", Assert.Single(spans, s => s.OperationName == "netjes.render").GetTagItem("http.route"));
        var duration = Assert.Single(_measurements, m => m.Instrument == "netjes.render.duration" && (string?)m.Tags["netjes.route"] == "/telemetry-template/[id]");
        Assert.Equal("/telemetry-template/{id}", duration.Tags["http.route"]);
    }

    [Theory]
    [InlineData("/telemetry-catch/[...slug]", "app/telemetry-catch/[...slug]/page.tsx", "/telemetry-catch/{**slug:minlength(1)}")]
    [InlineData("/telemetry-optional/[[...slug]]", "app/telemetry-optional/[[...slug]]/page.tsx", "/telemetry-optional/{**slug}")]
    [InlineData("/telemetry-group", "app/(telemetry)/telemetry-group/page.tsx", "/telemetry-group")]
    public async Task TheRouteTemplate_IsTheOneNetjesMapsThePageAt(string pattern, string file, string template)
    {
        await using var app = await TestApp.StartAsync([file]);
        var url = pattern.Replace("[[...slug]]", "a/b").Replace("[...slug]", "a/b");

        await app.GetDocumentAsync(url);

        Assert.Equal(template, Assert.Single(Spans(pattern), s => s.OperationName == "netjes.render").GetTagItem("http.route"));
    }

    [Fact]
    public async Task APageAction_CarriesTheRouteTemplate()
    {
        await using var app = await TestApp.StartAsync(
            ["app/telemetry-act/[id]/page.tsx"],
            map: a => a.MapPageAction("/telemetry-act/[id]", (string id) => Page.Redirect("/")).DisableAntiforgery());

        await app.Client.PostAsync("/telemetry-act/7", new FormUrlEncodedContent([]), TestContext.Current.CancellationToken);

        Assert.Equal("/telemetry-act/{id}", Assert.Single(Spans("/telemetry-act/[id]"), s => s.OperationName == "netjes.action").GetTagItem("http.route"));
    }

    [Fact]
    public async Task AServerAction_RecordsASpanWithItsName()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapAction("telemetry.greet", () => ServerAction.Ok("hi")).DisableAntiforgery());

        var request = new HttpRequestMessage(HttpMethod.Post, "/_netjes/actions/telemetry.greet") { Content = JsonContent.Create(new { }) };
        request.Headers.Add(NetjesHeaders.Data, "1");
        var response = await app.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
        var span = Assert.Single(_activities, s => s.OperationName == "netjes.server_action" && (string?)s.GetTagItem("netjes.server_action.name") == "telemetry.greet");
        Assert.Equal("netjes.server_action telemetry.greet", span.DisplayName);
        Assert.Null(span.GetTagItem("netjes.package"));
    }

    [Fact]
    public async Task AServerActionThatThrows_MarksItsSpanAsAnError()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapAction("telemetry.broken", Func<ServerActionResult> () => throw new InvalidOperationException("db down")).DisableAntiforgery());

        var request = new HttpRequestMessage(HttpMethod.Post, "/_netjes/actions/telemetry.broken") { Content = JsonContent.Create(new { }) };
        request.Headers.Add(NetjesHeaders.Data, "1");
        await app.Client.SendAsync(request, TestContext.Current.CancellationToken);

        var span = Assert.Single(_activities, s => s.OperationName == "netjes.server_action" && (string?)s.GetTagItem("netjes.server_action.name") == "telemetry.broken");
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("db down", span.StatusDescription);
    }

    [Fact]
    public async Task ALayoutLoader_RecordsASpanBeforeThePagesLoader()
    {
        await using var app = await TestApp.StartAsync(
            ["app/telemetry-layout/layout.tsx", "app/telemetry-layout/[id]/page.tsx"],
            map: a =>
            {
                a.MapLayoutLoader("/telemetry-layout", () => Page.Props(new { Menu = "m" }));
                a.MapPageLoader("/telemetry-layout/[id]", (string id) => Page.Props(new { id }));
            });

        await app.GetDocumentAsync("/telemetry-layout/1");

        var layout = Assert.Single(_activities, s => s.OperationName == "netjes.layout_loader" && (string?)s.GetTagItem("netjes.route") == "/telemetry-layout");
        Assert.Equal("netjes.layout_loader /telemetry-layout", layout.DisplayName);
        Assert.Equal("/telemetry-layout", layout.GetTagItem("http.route"));
        Assert.Equal("app/telemetry-layout/layout.tsx", layout.GetTagItem("netjes.layout"));
        Assert.Null(layout.GetTagItem("netjes.package"));
        // Inside the page's render, which loads the layouts' props.
        var render = Assert.Single(Spans("/telemetry-layout/[id]"), s => s.OperationName == "netjes.render");
        Assert.Equal(render.TraceId, layout.TraceId);
    }

    [Fact]
    public async Task ALayoutLoaderThatThrows_MarksItsSpanAsAnError()
    {
        await using var app = await TestApp.StartAsync(
            ["app/telemetry-layout-broken/layout.tsx", "app/telemetry-layout-broken/page.tsx"],
            map: a => a.MapLayoutLoader("/telemetry-layout-broken", Func<PageResult> () => throw new InvalidOperationException("db down")));

        await app.GetDataAsync("/telemetry-layout-broken");

        var layout = Assert.Single(_activities, s => s.OperationName == "netjes.layout_loader" && (string?)s.GetTagItem("netjes.route") == "/telemetry-layout-broken");
        Assert.Equal(ActivityStatusCode.Error, layout.Status);
    }

    [Fact]
    public async Task APagePackagesLayoutLoader_IsTaggedWithThePackage()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            prepareRoot: root => TestPagePackage.Install(root, ["layout.tsx", "page.tsx"]),
            map: a => a.MapPagePackage(TestPagePackage.Descriptor, "/telemetry-help-layout")
                .MapLayoutLoader("/", () => Page.Props(new { })));

        await app.GetDocumentAsync("/telemetry-help-layout");

        var layout = Assert.Single(_activities, s => s.OperationName == "netjes.layout_loader" && (string?)s.GetTagItem("netjes.route") == "/telemetry-help-layout");
        Assert.Equal(TestPagePackage.Name, layout.GetTagItem("netjes.package"));
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
