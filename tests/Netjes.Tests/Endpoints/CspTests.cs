using System.Security.Cryptography;
using System.Text;
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
            configureApp: a => a.UseOutputCache(),
            environment: "Testing",
            mode: NetjesMode.Development);

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
            },
            environment: "Testing",
            mode: NetjesMode.Development);

        var first = await app.GetDocumentAsync("/");
        var second = await app.GetDocumentAsync("/");

        Assert.NotEqual(NonceIn(first), NonceIn(second));
        Assert.Equal(2, loads);
        Assert.Single(app.Logs, l => l.Level == Microsoft.Extensions.Logging.LogLevel.Warning && l.Message.Contains("rendered each time instead", StringComparison.Ordinal));
    }

    /// <summary>A hash-based policy, as output-cached pages need: no <c>{nonce}</c> anywhere.</summary>
    private static readonly string HashPolicy = $"script-src 'self' {NetjesContentSecurityPolicy.ScriptHashes}; object-src 'none'";

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
            configureApp: a => a.UseOutputCache(),
            environment: "Testing",
            mode: NetjesMode.Development);

        await app.GetDocumentAsync("/");
        var cached = await app.GetDocumentAsync("/");

        Assert.Equal(1, loads);
        Assert.Equal($"{HashPolicy}; frame-ancestors 'self'", cached.Headers.GetValues("Content-Security-Policy").Single());
        Assert.DoesNotContain(app.Logs, l => l.Message.Contains("hash-based", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/missing")]
    public async Task ScriptHashes_AreTheHashesOfTheInlineScriptsTheClientRenderShellCarries(string url)
    {
        // The document .NET writes itself while no sidecar is ready, for a page and for the not-found page;
        // packages/netjes/test/csp.test.ts hashes the scripts of the documents the sidecar renders.
        await using var app = await ClientRenderFallbackTests.StartAsync(configure: o => o.ContentSecurityPolicy = HashPolicy);

        var html = await (await app.GetDocumentAsync(url)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("No Node sidecar was ready", html);
        var hashes = InlineScripts(html).Select(HashSource).Distinct().Order(StringComparer.Ordinal);
        Assert.Equal(NetjesContentSecurityPolicy.ScriptHashes.Split(' ').Order(StringComparer.Ordinal), hashes);
    }

    /// <summary>
    /// The text of each inline script in a document that a browser runs, and that a policy's script-src governs: every
    /// script element without <c>src</c>, except the <c>application/json</c> payload, which is data.
    /// </summary>
    private static IEnumerable<string> InlineScripts(string html) =>
        ScriptRegex().Matches(html)
            .Where(m => !m.Groups["attributes"].Value.Contains(" src=", StringComparison.Ordinal)
                && !m.Groups["attributes"].Value.Contains(" type=\"application/json\"", StringComparison.Ordinal))
            .Select(m => m.Groups["text"].Value);

    /// <summary>The source that allows a script by its text, as a browser hashes it: SHA-256 of its UTF-8 bytes.</summary>
    private static string HashSource(string script) => $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script)))}'";

    [Fact]
    public async Task PagesThatAreNotOutputCached_DoNotWarn()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configure: o => o.ContentSecurityPolicy = Policy,
            services: s => s.AddOutputCache(),
            configureApp: a => a.UseOutputCache(),
            environment: "Testing",
            mode: NetjesMode.Development);

        await app.GetDocumentAsync("/");

        Assert.DoesNotContain(app.Logs, l => l.Message.Contains("hash-based", StringComparison.Ordinal));
    }

    /// <summary>
    /// The policy with the word <c>ScriptHashes</c> replaced by <see cref="NetjesContentSecurityPolicy.ScriptHashes"/>,
    /// and <c>SCRIPTHASHES</c> by the same hashes with their prefix in upper case (<c>'SHA256-…'</c>).
    /// </summary>
    private static string WithScriptHashes(string policy) =>
        policy
            .Replace("ScriptHashes", NetjesContentSecurityPolicy.ScriptHashes, StringComparison.Ordinal)
            .Replace("SCRIPTHASHES", NetjesContentSecurityPolicy.ScriptHashes.Replace("'sha256-", "'SHA256-", StringComparison.Ordinal), StringComparison.Ordinal);

    /// <summary>
    /// An app whose documents go out under <paramref name="policy"/> (<see cref="WithScriptHashes"/>): a client build in
    /// Production, whose module script is <c>/assets/client-entry-AAA.js</c>, or in Development the Vite dev server at
    /// <c>http://localhost:5173</c>. With <paramref name="inItsOwnHeader"/>, the app writes the policy itself, and
    /// <c>{nonce}</c> stands for <see cref="NetjesHttpContextExtensions.GetCspNonce"/>. <paramref name="configure"/>
    /// changes the options after that.
    /// </summary>
    private static Task<TestApp> StartWithPolicyAsync(
        string policy, string environment = "Development", bool inItsOwnHeader = false, Action<NetjesOptions>? configure = null)
    {
        var written = WithScriptHashes(policy);
        return TestApp.StartAsync(
            environment == "Production" ? [] : ["app/page.tsx"],
            configure: o =>
            {
                o.ContentSecurityPolicy = inItsOwnHeader ? null : written;
                configure?.Invoke(o);
            },
            configureApp: a =>
            {
                if (inItsOwnHeader)
                {
                    a.Use((http, next) =>
                    {
                        http.Response.Headers.ContentSecurityPolicy = written.Replace("{nonce}", http.GetCspNonce(), StringComparison.Ordinal);
                        return next(http);
                    });
                }
            },
            environment: environment,
            prepareRoot: environment == "Production" ? ModeTests.WriteProductionBuild : null);
    }

    /// <summary>The renderer's warnings, which in these apps are all about the policies that go out on documents.</summary>
    private static List<string> PolicyWarnings(TestApp app) =>
        [.. app.Logs
            .Where(l => l.Category == typeof(Netjes.Rendering.PageRenderer).FullName && l.Level == Microsoft.Extensions.Logging.LogLevel.Warning)
            .Select(l => l.Message)];

    [Theory]
    [InlineData("script-src 'self'")]
    [InlineData("default-src 'self'")]
    [InlineData("SCRIPT-SRC 'self'")]
    // A hash turns 'unsafe-inline' off.
    [InlineData("script-src 'self' 'unsafe-inline' 'sha256-AAAA'")]
    // Script elements follow script-src-elem.
    [InlineData("script-src 'unsafe-inline'; script-src-elem 'self'")]
    // Two policies in one header, and browsers enforce both.
    [InlineData("object-src 'none', script-src 'self'")]
    public async Task APolicyWithoutANonce_ThatRefusesTheBootstrap_IsWarnedAboutOnce(string policy)
    {
        await using var app = await StartWithPolicyAsync(policy, "Production");

        await app.GetDocumentAsync("/");
        await app.GetDocumentAsync("/");

        var warning = Assert.Single(PolicyWarnings(app));
        Assert.Contains("NetjesContentSecurityPolicy.ScriptHashes", warning);
    }

    [Theory]
    [InlineData("script-src ScriptHashes", "/assets/client-entry-AAA.js", "'self'")]
    // Next to 'strict-dynamic' browsers ignore 'self', and the scripts carry no nonce.
    [InlineData("script-src 'self' 'strict-dynamic' ScriptHashes", "'strict-dynamic'", "nonce")]
    [InlineData("script-src 'strict-dynamic' 'unsafe-inline' https: ScriptHashes", "'strict-dynamic'", "nonce")]
    public async Task APolicyThatRefusesTheDocumentsModuleScripts_IsWarnedAbout(string policy, string named, string advice)
    {
        await using var app = await StartWithPolicyAsync(policy, "Production");

        await app.GetDocumentAsync("/");

        var warning = Assert.Single(PolicyWarnings(app));
        Assert.Contains(named, warning);
        Assert.Contains(advice, warning);
    }

    [Theory]
    [InlineData("script-src 'self' ScriptHashes; object-src 'none'")]
    [InlineData("script-src 'self' 'unsafe-inline'")]
    [InlineData("object-src 'none'; base-uri 'self'")]
    [InlineData("default-src 'self' 'unsafe-inline'")]
    [InlineData("script-src 'self' SCRIPTHASHES")]
    [InlineData("script-src https: ScriptHashes")]
    [InlineData("script-src * ScriptHashes")]
    // Behind a proxy the request's host is not always the one the browser sees: a host source may name the app's own.
    [InlineData("script-src https://www.example.com ScriptHashes")]
    public async Task PoliciesThatAllowNetjesScripts_AreNotWarnedAbout(string policy)
    {
        await using var app = await StartWithPolicyAsync(policy, "Production");

        await app.GetDocumentAsync("/");

        Assert.Empty(PolicyWarnings(app));
    }

    [Fact]
    public async Task TheAppsOwnPolicyHeader_IsCheckedToo()
    {
        await using var app = await StartWithPolicyAsync("script-src 'self'", "Production", inItsOwnHeader: true);

        await app.GetDocumentAsync("/");

        Assert.Contains("NetjesContentSecurityPolicy.ScriptHashes", Assert.Single(PolicyWarnings(app)));
    }

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Production", true)]
    [InlineData("Development", false)]
    [InlineData("Development", true)]
    public async Task NonceBasedPolicies_GetNoScriptWarnings(string environment, bool inItsOwnHeader)
    {
        // Netjes puts the nonce on its scripts, the dev server's included, and the modules they load inherit it.
        await using var app = await StartWithPolicyAsync("script-src 'nonce-{nonce}' 'strict-dynamic'; object-src 'none'", environment, inItsOwnHeader);

        await app.GetDocumentAsync("/");
        await app.GetDocumentAsync("/");

        Assert.Empty(PolicyWarnings(app));
    }

    [Fact]
    public async Task InDevelopment_APolicyWithoutANonceThatRefusesTheViteDevServer_IsWarnedAbout()
    {
        await using var app = await StartWithPolicyAsync("script-src 'self' ScriptHashes; object-src 'none'");

        await app.GetDocumentAsync("/");
        await app.GetDocumentAsync("/");

        var warning = Assert.Single(PolicyWarnings(app));
        Assert.Contains("http://localhost:5173", warning);
        Assert.Contains("outside Development", warning);
    }

    [Theory]
    [InlineData("script-src 'self' http://localhost:5173 ScriptHashes")]
    [InlineData("script-src 'self' localhost:5173 ScriptHashes")]
    [InlineData("script-src 'self' http://localhost:* ScriptHashes")]
    [InlineData("script-src * ScriptHashes")]
    [InlineData("script-src http: ScriptHashes")]
    [InlineData("object-src 'none'; base-uri 'self'")]
    public async Task InDevelopment_APolicyThatAllowsTheViteDevServer_IsNotWarnedAbout(string policy)
    {
        await using var app = await StartWithPolicyAsync(policy);

        await app.GetDocumentAsync("/");

        Assert.Empty(PolicyWarnings(app));
    }

    [Fact]
    public async Task InDevelopment_TheWarnings_NameTheDevServerOnThePortItWasGiven()
    {
        await using var app = await StartWithPolicyAsync("script-src 'self' ScriptHashes; connect-src 'self'", configure: o => o.ViteDevPort = 5174);

        await app.GetDocumentAsync("/");

        var warnings = PolicyWarnings(app);
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("Vite dev server at http://localhost:5174,", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("HMR WebSocket at ws://localhost:5174,", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InDevelopment_APolicyThatAllowsTheDevServerOnThePortItWasGiven_IsNotWarnedAbout()
    {
        await using var app = await StartWithPolicyAsync(
            "script-src 'self' http://localhost:5174 ScriptHashes; connect-src 'self' ws://localhost:5174", configure: o => o.ViteDevPort = 5174);

        await app.GetDocumentAsync("/");

        Assert.Empty(PolicyWarnings(app));
    }

    [Fact]
    public async Task InDevelopment_TheWarnings_NameTheDevServerWhereTheSidecarReportsIt()
    {
        // At http://127.0.0.1:5199, as the sidecar reports the dev server when NETJES_VITE_ORIGIN puts it there.
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configure: o => o.ContentSecurityPolicy = WithScriptHashes("script-src 'self' ScriptHashes; connect-src 'self'"),
            sidecar: "vite-origin-sidecar.mjs");

        await app.GetDocumentAsync("/");

        var warnings = PolicyWarnings(app);
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("Vite dev server at http://127.0.0.1:5199,", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("HMR WebSocket at ws://127.0.0.1:5199,", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("script-src 'nonce-{nonce}' 'strict-dynamic'; connect-src 'self'", false)]
    [InlineData("script-src 'nonce-{nonce}' 'strict-dynamic'; connect-src 'self'", true)]
    [InlineData("default-src 'self'; script-src 'nonce-{nonce}'", false)]
    // An http source matches https, but not ws.
    [InlineData("script-src 'nonce-{nonce}'; connect-src 'self' http://localhost:5173", false)]
    public async Task InDevelopment_AConnectSrcThatRefusesTheHmrSocket_IsWarnedAbout(string policy, bool inItsOwnHeader)
    {
        await using var app = await StartWithPolicyAsync(policy, inItsOwnHeader: inItsOwnHeader);

        // Each document has a nonce of its own in the policy, and the warning still comes once.
        await app.GetDocumentAsync("/");
        await app.GetDocumentAsync("/");

        var warning = Assert.Single(PolicyWarnings(app), w => w.Contains("ws://localhost:5173", StringComparison.Ordinal));
        Assert.Contains("server.hmr", warning);
    }

    [Theory]
    [InlineData("script-src 'nonce-{nonce}'; connect-src 'self' ws://localhost:5173")]
    [InlineData("script-src 'nonce-{nonce}'; connect-src ws:")]
    [InlineData("script-src 'nonce-{nonce}'; connect-src *")]
    [InlineData("script-src 'nonce-{nonce}'; connect-src ws://localhost:*")]
    [InlineData("script-src 'nonce-{nonce}'; CONNECT-SRC WS://LOCALHOST:5173")]
    [InlineData("script-src 'nonce-{nonce}'; object-src 'none'")]
    public async Task InDevelopment_AConnectSrcThatAllowsTheSocket_IsNotWarnedAbout(string policy)
    {
        await using var app = await StartWithPolicyAsync(policy);

        await app.GetDocumentAsync("/");

        Assert.Empty(PolicyWarnings(app));
    }

    [Fact]
    public async Task InProduction_NoDevServerChecksRun()
    {
        await using var app = await StartWithPolicyAsync("script-src 'self' ScriptHashes; connect-src 'self'; style-src 'self'", "Production");

        await app.GetDocumentAsync("/");

        Assert.Empty(PolicyWarnings(app));
    }

    [Theory]
    [InlineData("script-src 'self' http://localhost:5173 ScriptHashes; style-src 'self'")]
    [InlineData("default-src 'self' http://localhost:5173 ws://localhost:5173 ScriptHashes")]
    // A hash turns 'unsafe-inline' off.
    [InlineData("script-src 'self' http://localhost:5173 ScriptHashes; style-src 'self' 'unsafe-inline' 'sha256-AAAA'")]
    // The nonce goes on the styles too, but this style-src does not hold it.
    [InlineData("script-src 'nonce-{nonce}'; style-src 'self'")]
    public async Task InDevelopment_AStyleSrcThatRefusesInlineStyles_IsWarnedAbout(string policy)
    {
        await using var app = await StartWithPolicyAsync(policy);

        await app.GetDocumentAsync("/");
        await app.GetDocumentAsync("/");

        Assert.Contains("style-src", Assert.Single(PolicyWarnings(app)));
    }

    [Theory]
    [InlineData("script-src 'nonce-{nonce}'; style-src 'self' 'unsafe-inline'")]
    [InlineData("script-src 'nonce-{nonce}'; object-src 'none'")]
    [InlineData("script-src 'nonce-{nonce}'; style-src 'nonce-{nonce}'")]
    public async Task InDevelopment_StyleSourcesThatAllowTheDevStyles_AreNotWarnedAbout(string policy)
    {
        await using var app = await StartWithPolicyAsync(policy);

        await app.GetDocumentAsync("/");

        Assert.Empty(PolicyWarnings(app));
    }

    [Fact]
    public async Task APolicyThatChangesWithEveryRequest_IsCheckedOnlyUntilALimit()
    {
        // Such as a policy with the hash of an inline script whose text changes each time: Netjes keeps no more of them.
        var requests = 0;
        await using var app = await TestApp.StartAsync(
            [],
            configureApp: a => a.Use((http, next) =>
            {
                http.Response.Headers.ContentSecurityPolicy = $"script-src 'self'; report-uri /csp-reports/{Interlocked.Increment(ref requests)}";
                return next(http);
            }),
            environment: "Production",
            prepareRoot: ModeTests.WriteProductionBuild);

        for (var i = 0; i < 120; i++)
        {
            await app.GetDocumentAsync("/");
        }

        // One request after the other: each of the first 100 policies is checked, and warned about once, and no more.
        Assert.Equal(100, PolicyWarnings(app).Count);
    }

    [Fact]
    public async Task AStaticExport_IsNotChecked()
    {
        // The exported files go out under the static host's policy, not this one.
        int? exitCode = null;
        await using var app = await TestApp.StartAsync(
            [],
            environment: "Production",
            prepareRoot: ModeTests.WriteProductionBuild,
            configure: o =>
            {
                o.ContentSecurityPolicy = "script-src 'self'";
                o.Command = new NetjesCommandOptions { Name = "export", Output = "out" };
            },
            services: s =>
            {
                s.AddSingleton(new Netjes.TypeGen.CommandExit(code => exitCode = code));
                s.AddSingleton(sp => new Netjes.Export.ExportClient(
                    () => ((Microsoft.AspNetCore.TestHost.TestServer)sp.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()).CreateClient()));
            });

        Assert.Equal(0, exitCode);
        Assert.Empty(PolicyWarnings(app));
    }

    [GeneratedRegex("'nonce-([^']+)'")]
    private static partial Regex NonceRegex();

    [GeneratedRegex("<script(?<attributes>[^>]*)>(?<text>.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex ScriptRegex();
}
