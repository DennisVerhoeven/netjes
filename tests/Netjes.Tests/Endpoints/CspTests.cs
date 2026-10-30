using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Netjes.Tests.Endpoints;

/// <summary>A per-request nonce for a Content-Security-Policy that only allows the scripts Netjes (and the app) render.</summary>
public partial class CspTests
{
    private const string Policy = "script-src 'nonce-{nonce}' 'strict-dynamic'; object-src 'none'";

    private static string NonceIn(HttpResponseMessage response) =>
        NonceRegex().Match(response.Headers.GetValues("Content-Security-Policy").Single()).Groups[1].Value;

    [Fact]
    public async Task Documents_GetThePolicyWithAFreshNonce_AndTheRendererGetsTheSameNonce()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"], configure: o => o.ContentSecurityPolicy = Policy);

        var first = await app.GetDocumentAsync("/");
        var firstPayload = app.Renderer.LastPayload;
        var second = await app.GetDocumentAsync("/");

        // Plus same-origin framing (the security headers), since the policy has no frame-ancestors of its own.
        Assert.Matches(
            "^script-src 'nonce-[A-Za-z0-9+/=]{24}' 'strict-dynamic'; object-src 'none'; frame-ancestors 'self'$",
            first.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(NonceIn(first), (string?)firstPayload["document"]!["nonce"]);
        Assert.NotEqual(NonceIn(first), NonceIn(second));
    }

    [Fact]
    public async Task ErrorAndNotFoundDocuments_GetItToo()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"], configure: o => o.ContentSecurityPolicy = Policy);

        var response = await app.GetDocumentAsync("/missing");

        Assert.Equal(NonceIn(response), (string?)app.Renderer.LastPayload["document"]!["nonce"]);
    }

    [Fact]
    public async Task DataResponses_NeedNoPolicy()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"], configure: o => o.ContentSecurityPolicy = Policy);

        var response = await app.GetDataAsync("/");

        Assert.False(response.Headers.Contains("Content-Security-Policy"));
    }

    [Fact]
    public async Task WithoutAPolicy_ThereIsNoNonce()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"]);

        var response = await app.GetDocumentAsync("/");

        Assert.False(response.Headers.Contains("Content-Security-Policy"));
        Assert.Null(app.Renderer.LastPayload["document"]!["nonce"]);
    }

    [Fact]
    public async Task AnAppThatSetsItsOwnPolicy_UsesGetCspNonce_AndNetjesAppliesTheSameNonce()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configureApp: a => a.Use((http, next) =>
            {
                http.Response.Headers.ContentSecurityPolicy = $"script-src 'nonce-{http.GetCspNonce()}'";
                return next(http);
            }));

        var response = await app.GetDocumentAsync("/");

        Assert.Equal(NonceIn(response), (string?)app.Renderer.LastPayload["document"]!["nonce"]);
    }

    [Fact]
    public async Task GetCspNonce_IsStableWithinARequest()
    {
        var context = new DefaultHttpContext();

        Assert.Equal(context.GetCspNonce(), context.GetCspNonce());
        Assert.NotEqual(context.GetCspNonce(), new DefaultHttpContext().GetCspNonce());
    }

    [Fact]
    public async Task OutputCachedPages_AreNotStoredWithANonce_AndTheAppIsWarnedOnce()
    {
        var loads = 0;
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configure: o => o.ContentSecurityPolicy = Policy,
            map: a => a.MapPageLoader("/", () => Page.Props(new { Load = Interlocked.Increment(ref loads) })).CachePage(TimeSpan.FromMinutes(5)),
            services: s => s.AddOutputCache(),
            configureApp: a => a.UseOutputCache());

        var first = await app.GetDocumentAsync("/");
        var second = await app.GetDocumentAsync("/");

        // A cached copy would hand every visitor the same nonce, which then stops meaning anything.
        Assert.NotEqual(NonceIn(first), NonceIn(second));
        Assert.Equal(2, loads);
        Assert.Single(app.Logs, l => l.Level == Microsoft.Extensions.Logging.LogLevel.Warning && l.Message.Contains("hash-based", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OutputCachedPages_AreNotStoredWithTheNonceOfAPolicyTheAppWrites()
    {
        var loads = 0;
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props(new { Load = Interlocked.Increment(ref loads) })).CachePage(TimeSpan.FromMinutes(5)),
            services: s => s.AddOutputCache(),
            configureApp: a =>
            {
                a.UseOutputCache();
                a.Use((http, next) =>
                {
                    http.Response.Headers.ContentSecurityPolicy = $"script-src 'nonce-{http.GetCspNonce()}'";
                    return next(http);
                });
            });

        var first = await app.GetDocumentAsync("/");
        var second = await app.GetDocumentAsync("/");

        Assert.NotEqual(NonceIn(first), NonceIn(second));
        Assert.Equal(2, loads);
        Assert.Single(app.Logs, l => l.Level == Microsoft.Extensions.Logging.LogLevel.Warning && l.Message.Contains("rendered each time instead", StringComparison.Ordinal));
    }

    /// <summary>A hash-based policy, as output-cached pages need: no <c>{nonce}</c> anywhere.</summary>
    private const string HashPolicy = "script-src 'self' 'sha256-FSjVV+FzmXP8KKrLKLdvst/5N/bEWSkde01XzpGKRgs='; object-src 'none'";

    [Fact]
    public async Task APolicyWithoutTheNoncePlaceholder_GetsNoNonce()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"], configure: o => o.ContentSecurityPolicy = HashPolicy);

        var response = await app.GetDocumentAsync("/");

        Assert.Equal($"{HashPolicy}; frame-ancestors 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Null(app.Renderer.LastPayload["document"]!["nonce"]);
    }

    [Fact]
    public async Task OutputCachedPages_UnderAPolicyWithoutTheNoncePlaceholder_AreStored()
    {
        var loads = 0;
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configure: o => o.ContentSecurityPolicy = HashPolicy,
            map: a => a.MapPageLoader("/", () => Page.Props(new { Load = Interlocked.Increment(ref loads) })).CachePage(TimeSpan.FromMinutes(5)),
            services: s => s.AddOutputCache(),
            configureApp: a => a.UseOutputCache());

        await app.GetDocumentAsync("/");
        var cached = await app.GetDocumentAsync("/");

        Assert.Equal(1, loads);
        Assert.Equal($"{HashPolicy}; frame-ancestors 'self'", cached.Headers.GetValues("Content-Security-Policy").Single());
        Assert.DoesNotContain(app.Logs, l => l.Message.Contains("hash-based", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PagesThatAreNotOutputCached_DoNotWarn()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configure: o => o.ContentSecurityPolicy = Policy,
            services: s => s.AddOutputCache(),
            configureApp: a => a.UseOutputCache());

        await app.GetDocumentAsync("/");

        Assert.DoesNotContain(app.Logs, l => l.Message.Contains("hash-based", StringComparison.Ordinal));
    }

    [GeneratedRegex("'nonce-([^']+)'")]
    private static partial Regex NonceRegex();
}
