using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Netjes.Tests.Endpoints;

/// <summary>A <c>[lang]</c> segment backed by ASP.NET Core request localization, like Next.js's i18n routing.</summary>
public class LocalizationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<TestApp> StartAsync() =>
        TestApp.StartAsync(
            ["app/[lang]/about/page.tsx", "app/[lang]/page.tsx"],
            services: s => s.AddNetjesLocalization(o => o.SupportedCultures = ["en", "nl"]),
            configureApp: a => a.UseNetjesLocalization(),
            map: a =>
            {
                a.MapLocaleRedirect();
                a.MapPageLoader("/[lang]/about", () => Page.Props(new { Culture = CultureInfo.CurrentUICulture.Name, Date = 1.5.ToString(CultureInfo.CurrentCulture) }));
            });

    [Fact]
    public async Task TheLangSegment_SetsTheRequestCulture_TheLocaleAndTheHtmlLang()
    {
        await using var app = await StartAsync();

        var payload = await app.GetDataJsonAsync("/nl/about");
        await app.GetDocumentAsync("/nl/about");

        Assert.Equal("nl", (string?)payload["props"]!["culture"]);
        Assert.Equal("1,5", (string?)payload["props"]!["date"]);
        Assert.Equal("nl", (string?)payload["locale"]);
        Assert.Equal("nl", (string?)app.Renderer.LastPayload["document"]!["lang"]);
    }

    [Fact]
    public async Task AnUnsupportedLocale_IsNotFound()
    {
        await using var app = await StartAsync();

        var response = await app.GetDataAsync("/xx/about");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnUnsupportedLocale_IsNotFound_OnEndpointsThatAreNoPage()
    {
        // Request localization would otherwise look the raw name up as a culture, which the runtime caches for good.
        await using var app = await TestApp.StartAsync(
            ["app/[lang]/page.tsx"],
            services: s => s.AddNetjesLocalization(o => o.SupportedCultures = ["en", "nl"]),
            configureApp: a => a.UseNetjesLocalization(),
            map: a => a.MapGet("/{lang}/api/ping", () => CultureInfo.CurrentUICulture.Name));

        var unsupported = await app.Client.GetAsync("/xx-YY/api/ping", Ct);
        var supported = await app.Client.GetAsync("/nl/api/ping", Ct);

        Assert.Equal(HttpStatusCode.NotFound, unsupported.StatusCode);
        Assert.Equal("nl", await supported.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task AnUnsupportedLocale_RendersTheNotFoundPageInTheDefaultCulture_NotTheServers()
    {
        await using var app = await TestApp.StartAsync(
            ["app/[lang]/about/page.tsx"],
            services: s =>
            {
                s.AddNetjesLocalization(o => o.SupportedCultures = ["nl", "en"]);
                // The test's culture reaches the request, standing in for the server process's own.
                s.Configure<TestServerOptions>(o => o.PreserveExecutionContext = true);
            },
            configureApp: a => a.UseNetjesLocalization());
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("de-DE");

        var response = await app.GetDocumentAsync("/xx/about");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("nl", (string?)app.Renderer.LastPayload["locale"]);
        Assert.Equal("nl", (string?)app.Renderer.LastPayload["document"]!["lang"]);
    }

    [Theory]
    [InlineData("/EN/about", "en")]
    [InlineData("/NL-nl/about", "nl-NL")]
    public async Task ALocaleInAnotherCase_ReachesParamsAndLoadersAsConfigured(string url, string configured)
    {
        await using var app = await TestApp.StartAsync(
            ["app/[lang]/about/page.tsx"],
            services: s => s.AddNetjesLocalization(o => o.SupportedCultures = ["en", "nl-NL"]),
            configureApp: a => a.UseNetjesLocalization(),
            map: a => a.MapPageLoader("/[lang]/about", (string lang) => Page.Props(new { Lang = lang })));

        var payload = await app.GetDataJsonAsync(url);

        Assert.Equal(configured, (string?)payload["params"]!["lang"]);
        Assert.Equal(configured, (string?)payload["props"]!["lang"]);
        Assert.Equal(configured, (string?)payload["locale"]);
    }

    [Fact]
    public async Task ALocaleInAnotherCase_ReachesEndpointsThatAreNoPageAsConfigured()
    {
        await using var app = await TestApp.StartAsync(
            ["app/[lang]/page.tsx"],
            services: s => s.AddNetjesLocalization(o => o.SupportedCultures = ["en", "nl"]),
            configureApp: a => a.UseNetjesLocalization(),
            map: a => a.MapGet("/{lang}/api/echo", (string lang) => lang));

        var response = await app.Client.GetAsync("/NL/api/echo", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nl", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public void ParentCultureFallback_IsOff()
    {
        // Falling back creates a culture for each unmatched name to find its parent; an app that adds an
        // Accept-Language or cookie provider would otherwise hand that lookup every name a client sends.
        using var services = new ServiceCollection().AddNetjesLocalization(o => o.SupportedCultures = ["en", "nl"]).BuildServiceProvider();

        var options = services.GetRequiredService<IOptions<RequestLocalizationOptions>>().Value;

        Assert.False(options.FallBackToParentCultures);
        Assert.False(options.FallBackToParentUICultures);
    }

    [Theory]
    [InlineData("nl-NL,nl;q=0.9,en;q=0.8", "/nl")]
    [InlineData("de-DE,de;q=0.9", "/en")]
    [InlineData(null, "/en")]
    public async Task TheRoot_RedirectsToTheBestSupportedLocale(string? acceptLanguage, string location)
    {
        await using var app = await StartAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        if (acceptLanguage is not null)
        {
            request.Headers.Add("Accept-Language", acceptLanguage);
        }

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal(location, response.Headers.Location?.ToString());
    }

    [Theory]
    [InlineData("Accept-Language", "nl-NL,nl;q=0.9")]
    [InlineData("Cookie", ".AspNetCore.Culture=c%3Dnl%7Cuic%3Dnl")]
    public async Task PagesOutsideTheLangSegment_UseTheDefaultCulture_NotOneFromTheRequest(string header, string value)
    {
        // Their URL does not name a locale, so a cached copy must not carry the first visitor's.
        await using var app = await TestApp.StartAsync(
            ["app/[lang]/page.tsx", "app/about/page.tsx"],
            services: s => s.AddNetjesLocalization(o => o.SupportedCultures = ["en", "nl"]),
            configureApp: a => a.UseNetjesLocalization(),
            map: a => a.MapPageLoader("/about", () => Page.Props(new { Culture = CultureInfo.CurrentUICulture.Name })));
        var request = new HttpRequestMessage(HttpMethod.Get, "/about?culture=nl");
        request.Headers.Add(NetjesHeaders.Data, "1");
        request.Headers.Add(header, value);

        var payload = System.Text.Json.Nodes.JsonNode.Parse(await (await app.Client.SendAsync(request, Ct)).Content.ReadAsStringAsync(Ct))!;

        Assert.Equal("en", (string?)payload["props"]!["culture"]);
    }

    [Fact]
    public async Task WithoutLocalization_ThereIsNoLocale()
    {
        await using var app = await TestApp.StartAsync(["app/about/page.tsx"]);

        var payload = await app.GetDataJsonAsync("/about");

        Assert.Null(payload["locale"]);
    }
}
