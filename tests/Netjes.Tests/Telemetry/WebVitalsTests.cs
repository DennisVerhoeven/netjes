using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using Netjes.Tests.Endpoints;

namespace Netjes.Tests.Telemetry;

/// <summary>The optional <c>/_netjes/vitals</c> beacon: Core Web Vitals from browsers as .NET metrics.</summary>
public sealed class WebVitalsTests : IDisposable
{
    private readonly ConcurrentQueue<(string Instrument, double Value, Dictionary<string, object?> Tags)> _measurements = new();
    private readonly MeterListener _listener = new();

    public WebVitalsTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == NetjesTelemetry.Name && instrument.Name.StartsWith("netjes.web_vitals.", StringComparison.Ordinal))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            _measurements.Enqueue((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        _listener.Start();
    }

    private static Task<HttpResponseMessage> PostAsync(TestApp app, string json) =>
        app.Client.PostAsync("/_netjes/vitals", new StringContent(json, Encoding.UTF8, "text/plain"), TestContext.Current.CancellationToken);

    private static Task<TestApp> StartAsync(bool collect = true) =>
        TestApp.StartAsync(["app/vitals/[id]/page.tsx"], configure: o => o.CollectWebVitals = collect);

    [Fact]
    public async Task AReportedMetric_IsRecordedPerRouteAndRating()
    {
        await using var app = await StartAsync();

        var response = await PostAsync(app, """{"name":"LCP","value":1234.5,"rating":"good","route":"/vitals/[id]","navigationType":"navigate"}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var measurement = Assert.Single(_measurements, m => m.Instrument == "netjes.web_vitals.lcp" && (string?)m.Tags["netjes.route"] == "/vitals/[id]");
        Assert.Equal(1234.5, measurement.Value);
        Assert.Equal("good", measurement.Tags["netjes.web_vitals.rating"]);
    }

    [Fact]
    public async Task RoutesTheAppDoesNotHave_AreRecordedAsUnknown_SoTagsStayBounded()
    {
        await using var app = await StartAsync();

        await PostAsync(app, """{"name":"CLS","value":0.12,"rating":"needs-improvement","route":"/made/up/42"}""");

        Assert.Contains(_measurements, m => m.Instrument == "netjes.web_vitals.cls" && (string?)m.Tags["netjes.route"] == "unknown" && m.Value == 0.12);
    }

    [Theory]
    [InlineData("""{"name":"FID","value":1,"rating":"good","route":"/"}""")]
    [InlineData("""{"name":"LCP","value":-1,"rating":"good","route":"/"}""")]
    [InlineData("""{"name":"LCP","value":1e12,"rating":"good","route":"/"}""")]
    [InlineData("""{"name":"LCP","value":1,"rating":"great","route":"/"}""")]
    [InlineData("""not json""")]
    public async Task InvalidReports_AreRejected(string json)
    {
        await using var app = await StartAsync();

        var response = await PostAsync(app, json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OversizedReports_AreRejected()
    {
        await using var app = await StartAsync();

        var response = await PostAsync(app, $$"""{"name":"LCP","value":1,"rating":"good","route":"{{new string('a', 5000)}}"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ByDefault_ThereIsNoEndpoint()
    {
        await using var app = await StartAsync(collect: false);

        var response = await PostAsync(app, """{"name":"LCP","value":1,"rating":"good","route":"/"}""");

        Assert.NotEqual(HttpStatusCode.NoContent, response.StatusCode);
        Assert.DoesNotContain(_measurements, m => m.Instrument == "netjes.web_vitals.lcp" && m.Value == 1);
    }

    [Fact]
    public async Task TheDocumentTellsTheClientWhetherToReport()
    {
        await using var app = await StartAsync();

        await app.GetDocumentAsync("/vitals/1");

        Assert.Equal("/_netjes/vitals", (string?)app.Renderer.LastPayload["document"]!["vitalsEndpoint"]);
    }

    public void Dispose() => _listener.Dispose();
}
