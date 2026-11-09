using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Netjes.Rendering;

namespace Netjes.Tests.Endpoints;

/// <summary>
/// Regression tests across the app router: layout loaders and their boundaries, signed segment keys, output caching
/// under a base policy, deferred values, forms without JavaScript and what they hand back, and the origin check on actions.
/// </summary>
public class AppRouterRegressionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class NobodyHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }

    private static void Nobody(IServiceCollection services) =>
        services.AddAuthorization().AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, NobodyHandler>("Test", null);

    private sealed class AdaHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "ada")], "Test")), "Test")));
    }

    /// <summary>Signed in as Ada, who has no roles.</summary>
    private static void Ada(IServiceCollection services) =>
        services.AddAuthorization().AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, AdaHandler>("Test", null);

    private sealed record Token(string Cookie, string Field, string Value);

    private static async Task<Token> TokenAsync(TestApp app)
    {
        var page = await app.GetDataAsync("/");
        var antiforgery = JsonNode.Parse(await page.Content.ReadAsStringAsync(Ct))!["antiforgery"]!;
        return new Token(
            string.Join("; ", page.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])),
            (string)antiforgery["formFieldName"]!,
            (string)antiforgery["requestToken"]!);
    }

    /// <summary>From the token endpoint, as the client gets one for a page that carries none (a page the output cache stores).</summary>
    private static async Task<Token> FetchedTokenAsync(TestApp app)
    {
        var response = await app.Client.GetAsync("/_netjes/antiforgery", Ct);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        return new Token(
            string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])),
            (string)json["formFieldName"]!,
            (string)json["requestToken"]!);
    }

    private static HttpRequestMessage NoJsPost(string action, Token token, params (string, string)[] fields)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/_netjes/actions/{action}")
        {
            Content = new FormUrlEncodedContent([.. fields.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)), new(token.Field, token.Value)]),
        };
        request.Headers.Add("Cookie", token.Cookie);
        return request;
    }

    [Fact]
    public async Task NoJsValidationFailure_WithARootLayoutLoader_RendersThePageWith422()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx", "app/guestbook/page.tsx"],
            map: a =>
            {
                a.MapLayoutLoader("/", () => Page.Props(new { Site = "x" }));
                a.MapAction("sign", ([FromForm] string? name) => ServerAction.Invalid("name", "required"));
            });
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(NoJsPost("sign", token, ("name", ""), ("__netjes_return", "/guestbook")), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("app/guestbook/page.tsx", (string?)app.Renderer.LastPayload["page"]);
        Assert.Equal("x", (string?)app.Renderer.LastPayload["segments"]![0]!["props"]!["site"]);
    }

    [Fact]
    public async Task Empty401_WithARootLayoutLoader_RendersTheUnauthorizedPageInsideTheRootLayout()
    {
        // The page's own loader refuses the visitor; the root layout's, like a file page at the root, lets them in.
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/error.tsx", "app/unauthorized.tsx", "app/admin/page.tsx"],
            map: a =>
            {
                a.MapLayoutLoader("/", () => Page.Props(new { Site = "x" }));
                a.MapPageLoader("/admin", () => Page.Props()).RequireAuthorization();
            },
            services: Nobody,
            configureApp: a => a.UseAuthorization());

        var response = await app.GetDocumentAsync("/admin");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("app/unauthorized.tsx", (string?)app.Renderer.LastPayload["page"]);
        Assert.Equal("x", (string?)app.Renderer.LastPayload["segments"]![0]!["props"]!["site"]);
    }

    [Fact]
    public async Task DeniedUser_NeverRunsTheProtectedSectionsLayoutLoader_AndGetsItsBoundaryWithoutItsLayout()
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/error.tsx", "app/admin/layout.tsx", "app/admin/unauthorized.tsx", "app/admin/page.tsx"],
            map: a => a.MapLayoutLoader("/admin", () =>
            {
                calls++;
                return Page.Props(new { Secret = "s3cret" });
            }).RequireAuthorization(),
            services: Nobody,
            configureApp: a => a.UseAuthorization());

        var response = await app.GetDataAsync("/admin");
        var body = await response.Content.ReadAsStringAsync(Ct);
        var payload = JsonNode.Parse(body)!;

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, calls);
        Assert.DoesNotContain("s3cret", body);
        // The section's own unauthorized page, inside the layouts above the section (its own loader cannot run).
        Assert.Equal("app/admin/unauthorized.tsx", (string?)payload["page"]);
        Assert.Equal(["app"], payload["segments"]!.AsArray().Select(s => (string?)s!["id"]));
    }

    [Fact]
    public async Task SignedInUserWithoutTheRole_GetsTheForbiddenPageInsideTheLayoutsTheyMayUse()
    {
        var adminCalls = 0;
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/error.tsx", "app/forbidden.tsx", "app/admin/layout.tsx", "app/admin/page.tsx"],
            map: a =>
            {
                a.MapLayoutLoader("/", () => Page.Props(new { Site = "x" })).RequireAuthorization();
                a.MapLayoutLoader("/admin", () =>
                {
                    adminCalls++;
                    return Page.Props(new { Secret = "s3cret" });
                }).RequireAuthorization(p => p.RequireRole("Admin"));
            },
            services: Ada,
            configureApp: a => a.UseAuthorization());

        var response = await app.GetDocumentAsync("/admin");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, adminCalls);
        Assert.Equal("app/forbidden.tsx", (string?)app.Renderer.LastPayload["page"]);
        var segments = app.Renderer.LastPayload["segments"]!.AsArray();
        Assert.Equal(["app"], segments.Select(s => (string?)s!["id"]));
        Assert.Equal("x", (string?)segments[0]!["props"]!["site"]);
    }

    [Fact]
    public async Task AnonymousVisitor_OfAProtectedRoot_GetsTheRootUnauthorizedPageWithoutLayouts()
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/error.tsx", "app/unauthorized.tsx", "app/page.tsx"],
            map: a => a.MapLayoutLoader("/", () =>
            {
                calls++;
                return Page.Props(new { Site = "x" });
            }).RequireAuthorization(),
            services: Nobody,
            configureApp: a => a.UseAuthorization());

        var response = await app.GetDocumentAsync("/");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, calls);
        Assert.Equal("app/unauthorized.tsx", (string?)app.Renderer.LastPayload["page"]);
        Assert.Empty(app.Renderer.LastPayload["segments"]!.AsArray());
    }

    private const string Help = TestPagePackage.Name;

    /// <summary>
    /// The layout loader that guards an unknown URL below a page package's mount (the fallback endpoint carries no
    /// layout loader's authorization): the package's own, mapped through the mount, the app's above the mount, or the
    /// app's root layout's; or the mount's conventions, which guard the package's files where its own layout loader
    /// has no authorization of its own.
    /// </summary>
    public static TheoryData<string, bool> MountGuards => new()
    {
        { "the mount's", false },
        { "the mount's", true },
        { "the mount's conventions", false },
        { "the mount's conventions", true },
        { "the app's above the mount", false },
        { "the app's above the mount", true },
        { "the app's root", false },
        { "the app's root", true },
    };

    [Theory]
    [MemberData(nameof(MountGuards))]
    public async Task UnknownUrlBelowAProtectedLayoutOnAMountsChain_NeverRunsItsLoaderForAVisitorItRefuses(string guard, bool signedIn)
    {
        var calls = 0;
        var secret = () =>
        {
            calls++;
            return Page.Props(new { Secret = "s3cret" });
        };

        var onTheMount = guard == "the mount's";
        var atTheRoot = guard == "the app's root";
        var prefix = guard == "the app's above the mount" ? "/members/help" : "/help";
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/not-found.tsx", "app/page.tsx", "app/members/layout.tsx", "app/members/not-found.tsx", "app/members/page.tsx"],
            prepareRoot: root => TestPagePackage.Install(root, ["layout.tsx", "not-found.tsx", "page.tsx"]),
            map: a =>
            {
                if (guard == "the app's above the mount")
                {
                    a.MapLayoutLoader("/members", secret).RequireAuthorization();
                }

                if (atTheRoot)
                {
                    a.MapLayoutLoader("/", secret).RequireAuthorization();
                }

                var mount = a.MapPagePackage(TestPagePackage.Descriptor, prefix);
                if (onTheMount)
                {
                    mount.MapLayoutLoader("/", secret).RequireAuthorization();
                }

                if (guard == "the mount's conventions")
                {
                    mount.RequireAuthorization().MapLayoutLoader("/", secret);
                }
            },
            services: signedIn ? Ada : Nobody,
            configureApp: a => a.UseAuthorization());

        foreach (var url in new[] { $"{prefix}/nope", $"{prefix}/nope/deeper", prefix.ToUpperInvariant() + "/Nope" })
        {
            var response = await app.GetDataAsync(url);
            var body = await response.Content.ReadAsStringAsync(Ct);
            var payload = JsonNode.Parse(body)!;
            var document = await app.GetDocumentAsync(url);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, document.StatusCode);
            var ids = payload["segments"]!.AsArray().Select(s => (string?)s!["id"]).ToList();
            if (signedIn)
            {
                Assert.Equal($"{Help}/app/not-found.tsx", (string?)payload["page"]);
                Assert.Equal(prefix == "/help" ? ["app", "app/help"] : ["app", "app/members", "app/members/help"], ids);
                Assert.Contains("s3cret", body);
            }
            else
            {
                // The nearest not-found above the guarded segment, inside the layouts above it: the guarded segment's own
                // not-found would render in its place, and the mount's below it. Above the root there is no layout.
                Assert.Equal("app/not-found.tsx", (string?)payload["page"]);
                Assert.Equal(atTheRoot ? [] : ["app"], ids);
                Assert.DoesNotContain("s3cret", body);
                Assert.Equal("app/not-found.tsx", (string?)app.Renderer.LastPayload["page"]);
            }
        }

        Assert.Equal(signedIn ? 6 : 0, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APageItsMountProtects_NeverRunsThePackagesLayoutLoaderForAVisitorTheMountRefuses(bool signedIn)
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/not-found.tsx", "app/error.tsx", "app/page.tsx"],
            prepareRoot: root => TestPagePackage.Install(root, ["layout.tsx", "not-found.tsx", "error.tsx", "page.tsx"]),
            map: a => a.MapPagePackage(TestPagePackage.Descriptor, "/help").RequireAuthorization().MapLayoutLoader("/", () =>
            {
                calls++;
                return Page.Props(new { Secret = "s3cret" });
            }),
            services: signedIn ? Ada : Nobody,
            configureApp: a => a.UseAuthorization());

        var response = await app.GetDataAsync("/help");
        var body = await response.Content.ReadAsStringAsync(Ct);
        var ids = JsonNode.Parse(body)!["segments"]!.AsArray().Select(s => (string?)s!["id"]).ToList();

        Assert.Equal(signedIn ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, response.StatusCode);
        if (signedIn)
        {
            Assert.Equal(["app", "app/help"], ids);
            Assert.Contains("s3cret", body);
        }
        else
        {
            // The package's segment stops the page: its boundary renders in its place, without its layout.
            Assert.Equal(["app"], ids);
            Assert.DoesNotContain("s3cret", body);
        }

        Assert.Equal(signedIn ? 1 : 0, calls);
    }

    /// <summary>
    /// The ways to ask for an unknown URL outside every page package's mount, which the not-found fallback answers: a
    /// document, a client navigation (one that claims to hold the root segment too, with the key a visitor without a
    /// user would get for it), a <c>HEAD</c>, a deeper URL, and other spellings of the URL.
    /// </summary>
    public static TheoryData<string, string> UnknownUrls => new()
    {
        { "/nope", "document" },
        { "/nope", "data" },
        { "/nope", "data, holding the root segment" },
        { "/nope", "head" },
        { "/nope/deeper", "data" },
        { "/NOPE", "document" },
        { "/%6eope", "document" },
        { "//nope", "document" },
    };

    /// <summary>A request for <paramref name="url"/> as <see cref="UnknownUrls"/> names it: its status and the payload the page got.</summary>
    private static async Task<(HttpStatusCode Status, string Payload)> RequestAsync(TestApp app, string url, string request)
    {
        // From the origin, so //nope stays a path rather than naming a host.
        var message = new HttpRequestMessage(
            request == "head" ? HttpMethod.Head : HttpMethod.Get, new Uri(app.Client.BaseAddress!.GetLeftPart(UriPartial.Authority) + url));
        if (request.StartsWith("data", StringComparison.Ordinal))
        {
            message.Headers.Add(NetjesHeaders.Data, "1");
        }

        if (request is "data, holding the root segment" or "data, holding the mount's segments")
        {
            var table = app.App.Services.GetRequiredService<Netjes.Routing.IRouteTableProvider>().Current;
            var version = app.App.Services.GetRequiredService<IAssetResolver>().Version;
            var keys = app.App.Services.GetRequiredService<Netjes.Routing.LayoutLoaderRegistry>();
            var held = request == "data, holding the root segment" ? table.RootSegments : table.SegmentsAt("help");
            message.Headers.Add(NetjesHeaders.Segments, string.Join(',', held.Select(s => keys.KeyFor(s, new Microsoft.AspNetCore.Http.DefaultHttpContext(), version))));
        }

        var before = app.Renderer.Payloads.Count;
        var response = await app.Client.SendAsync(message, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, message.Headers.Contains(NetjesHeaders.Data) ? body : app.Renderer.Payloads[before]);
    }

    [Theory]
    [MemberData(nameof(UnknownUrls))]
    public async Task UnknownUrl_NeverRunsAProtectedRootLayoutLoaderForAVisitorItRefuses(string url, string request)
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/not-found.tsx", "app/page.tsx"],
            map: a => a.MapLayoutLoader("/", () =>
            {
                calls++;
                return Page.Props(new { Secret = "s3cret" });
            }).RequireAuthorization(),
            services: Nobody,
            configureApp: a => a.UseAuthorization());

        var (status, body) = await RequestAsync(app, url, request);
        var payload = JsonNode.Parse(body)!;

        // Still the 404 an unknown URL gets, without a challenge: the root's not-found, outside the root layout.
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal(0, calls);
        Assert.Equal("app/not-found.tsx", (string?)payload["page"]);
        Assert.Empty(payload["segments"]!.AsArray());
        Assert.DoesNotContain("s3cret", body);
    }

    [Theory]
    [InlineData("document")]
    [InlineData("data")]
    public async Task UnknownUrl_RunsAProtectedRootLayoutLoaderForAVisitorItAuthorizes(string request)
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/not-found.tsx", "app/page.tsx"],
            map: a => a.MapLayoutLoader("/", () =>
            {
                calls++;
                return Page.Props(new { Secret = "s3cret" });
            }).RequireAuthorization(),
            services: Ada,
            configureApp: a => a.UseAuthorization());

        var (status, body) = await RequestAsync(app, "/nope", request);
        var segments = JsonNode.Parse(body)!["segments"]!.AsArray();

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal(1, calls);
        Assert.Equal(["app"], segments.Select(s => (string?)s!["id"]));
        Assert.Equal("s3cret", (string?)segments[0]!["props"]!["secret"]);
    }

    [Theory]
    [InlineData("document", false)]
    [InlineData("data", false)]
    [InlineData("data, holding the root segment", false)]
    [InlineData("document", true)]
    [InlineData("data", true)]
    public async Task UnknownUrl_UnderMapPagesAuthorization_NeverRunsAnUnprotectedRootLayoutLoaderForAVisitorItRefuses(string request, bool signedIn)
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/not-found.tsx", "app/page.tsx"],
            map: a => a.MapLayoutLoader("/", () =>
            {
                calls++;
                return Page.Props(new { Secret = "s3cret" });
            }),
            services: signedIn ? Ada : Nobody,
            configureApp: a => a.UseAuthorization(),
            pages: p => p.RequireAuthorization());

        var (status, body) = await RequestAsync(app, "/nope", request);
        var ids = JsonNode.Parse(body)!["segments"]!.AsArray().Select(s => (string?)s!["id"]).ToList();

        // The fallback is one of MapPages()'s endpoints without carrying its conventions (an unknown URL would be
        // challenged), so their authorization guards the layout loaders on its chain.
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal(signedIn ? 1 : 0, calls);
        Assert.Equal(signedIn ? ["app"] : [], ids);
        Assert.Equal(signedIn, body.Contains("s3cret", StringComparison.Ordinal));
    }

    /// <summary>
    /// Who visits an unknown URL below a page package's mount whose own layout loader has no authorization, nobody or Ada
    /// (signed in, no roles), and whether the mount requires the Admin role. <c>MapPages()</c> requires a signed-in user.
    /// </summary>
    public static TheoryData<string, bool> MountVisitors => new()
    {
        { "nobody", false },
        { "Ada", false },
        { "nobody", true },
        { "Ada", true },
    };

    [Theory]
    [MemberData(nameof(MountVisitors))]
    public async Task UnknownUrlBelowAMount_UnderMapPagesAuthorization_NeverRunsThePackagesLayoutLoaderForAVisitorItRefuses(string visitor, bool adminMount)
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/not-found.tsx", "app/page.tsx"],
            prepareRoot: root => TestPagePackage.Install(root, ["layout.tsx", "not-found.tsx", "page.tsx"]),
            map: a =>
            {
                var mount = a.MapPagePackage(TestPagePackage.Descriptor, "/help");
                if (adminMount)
                {
                    mount.RequireAuthorization(p => p.RequireRole("Admin"));
                }

                mount.MapLayoutLoader("/", () =>
                {
                    calls++;
                    return Page.Props(new { Secret = "s3cret" });
                });
            },
            services: visitor == "Ada" ? Ada : Nobody,
            configureApp: a => a.UseAuthorization(),
            pages: p => p.RequireAuthorization());

        var requests = new[]
        {
            ("/help/nope", "data"), ("/help/nope", "document"), ("/help/nope", "head"), ("/HELP/nope", "data"),
            ("/help/nope/deeper", "data"), ("/help/nope", "data, holding the mount's segments"),
        };
        foreach (var (url, request) in requests)
        {
            var (status, body) = await RequestAsync(app, url, request);
            var payload = JsonNode.Parse(body)!;
            var ids = payload["segments"]!.AsArray().Select(s => (string?)s!["id"]).ToList();

            Assert.Equal(HttpStatusCode.NotFound, status);
            if (visitor == "Ada" && !adminMount)
            {
                Assert.Equal($"{Help}/app/not-found.tsx", (string?)payload["page"]);
                Assert.Equal(["app", "app/help"], ids);
            }
            else
            {
                // The app's not-found inside its root layout, which has no loader: MapPages()'s authorization stops the
                // chain at the first layout loader on it, the mount's at the first folder with the package's files.
                Assert.Equal("app/not-found.tsx", (string?)payload["page"]);
                Assert.Equal(["app"], ids);
                Assert.DoesNotContain("s3cret", body);
            }
        }

        // The keys a visitor without a user would hold are not Ada's, so her navigation runs the loader too.
        Assert.Equal(visitor == "Ada" && !adminMount ? requests.Length : 0, calls);
    }

    [Fact]
    public async Task AFailedRenderOfA404BelowAMount_UnderMapPagesAuthorization_NeverRunsThePackagesLayoutLoaderForTheErrorPage()
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/not-found.tsx", "app/error.tsx", "app/page.tsx"],
            prepareRoot: root => TestPagePackage.Install(root, ["layout.tsx", "not-found.tsx", "error.tsx", "page.tsx"]),
            map: a => a.MapPagePackage(TestPagePackage.Descriptor, "/help").MapLayoutLoader("/", () =>
            {
                calls++;
                return Page.Props(new { Secret = "s3cret" });
            }),
            services: Nobody,
            configureApp: a => a.UseAuthorization(),
            pages: p => p.RequireAuthorization(),
            renderer: r => r.OnRender = payload => payload.Contains("not-found.tsx", StringComparison.Ordinal)
                ? throw new InvalidOperationException("The not-found page failed to render")
                : "<!DOCTYPE html><html><body>rendered</body></html>");

        var response = await app.GetDocumentAsync("/help/nope");

        // The error page renders on the chain the 404 stopped, inside the layouts above the refused segment.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0, calls);
        Assert.Equal("app/error.tsx", (string?)app.Renderer.LastPayload["page"]);
        Assert.Equal(["app"], app.Renderer.LastPayload["segments"]!.AsArray().Select(s => (string?)s!["id"]));
    }

    /// <summary>
    /// The boundaries Netjes renders for an endpoint that serves no page, whose chain nothing has authorized, with the
    /// status each gets: the status pages for a server action's empty 401, for the fallback policy's on an unknown URL
    /// and for an empty 403 or 429 a middleware sets on one; the not-found and forbidden pages a minimal API asks for;
    /// and the error pages for an exception on an unknown URL, in a server action, and in the render of a 404.
    /// </summary>
    public static TheoryData<string, int, bool> NonPageBoundaries => new()
    {
        { "a server action's 401", 401, false },
        { "the fallback policy's 401", 401, false },
        { "a middleware's 403", 403, false },
        { "a middleware's 403", 403, true },
        { "a middleware's 429", 429, false },
        { "Page.NotFound() from a minimal API", 404, false },
        { "Page.NotFound() from a minimal API", 404, true },
        { "Page.Forbidden() from a minimal API", 403, false },
        { "an exception on an unknown URL", 500, false },
        { "an exception on an unknown URL", 500, true },
        { "an exception in a server action", 500, false },
        { "a failed render of an unknown URL's 404", 500, false },
        { "a failed render of an unknown URL's 404", 500, true },
    };

    [Theory]
    [MemberData(nameof(NonPageBoundaries))]
    public async Task ABoundaryForAnEndpointThatServesNoPage_NeverRunsAProtectedRootLayoutLoaderForAVisitorItRefuses(string path, int status, bool signedIn)
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/not-found.tsx", "app/error.tsx", "app/forbidden.tsx", "app/unauthorized.tsx", "app/page.tsx"],
            map: a =>
            {
                a.MapLayoutLoader("/", () =>
                {
                    calls++;
                    return Page.Props(new { Secret = "s3cret" });
                }).RequireAuthorization();
                a.MapAction("secure", () => ServerAction.Ok()).RequireAuthorization();
                a.MapAction("boom", Func<ServerActionResult> () => throw new InvalidOperationException("boom"));
                a.MapGet("/api/missing", () => Page.NotFound());
                a.MapGet("/api/refused", () => Page.Forbidden());
            },
            services: s =>
            {
                (signedIn ? (Action<IServiceCollection>)Ada : Nobody)(s);
                if (path == "the fallback policy's 401")
                {
                    s.AddAuthorization(o => o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
                }
            },
            configureApp: a =>
            {
                a.UseAuthorization();
                a.Use(async (http, next) =>
                {
                    switch (http.Request.Path.Value)
                    {
                        case "/blocked":
                            http.Response.StatusCode = path == "a middleware's 429" ? 429 : 403;
                            return;
                        case "/boom":
                            throw new InvalidOperationException("boom");
                    }

                    await next(http);
                });
            },
            renderer: r => r.OnRender = payload => path == "a failed render of an unknown URL's 404" && payload.Contains("app/not-found.tsx", StringComparison.Ordinal)
                ? throw new InvalidOperationException("The not-found page failed to render")
                : "<!DOCTYPE html><html><body>rendered</body></html>");

        var response = path switch
        {
            "a server action's 401" => await app.Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/_netjes/actions/secure") { Content = new FormUrlEncodedContent([]) }, Ct),
            "an exception in a server action" => await app.Client.SendAsync(NoJsPost("boom", await FetchedTokenAsync(app)), Ct),
            "a middleware's 403" or "a middleware's 429" => await app.GetDocumentAsync("/blocked"),
            "Page.NotFound() from a minimal API" => await app.GetDocumentAsync("/api/missing"),
            "Page.Forbidden() from a minimal API" => await app.GetDocumentAsync("/api/refused"),
            "an exception on an unknown URL" => await app.GetDocumentAsync("/boom"),
            _ => await app.GetDocumentAsync("/nope"),
        };
        var payload = app.Renderer.Payloads.Last();
        var ids = JsonNode.Parse(payload)!["segments"]!.AsArray().Select(s => (string?)s!["id"]).ToList();

        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal(signedIn ? 1 : 0, calls);
        Assert.Equal(signedIn ? ["app"] : [], ids);
        Assert.Equal(signedIn, payload.Contains("s3cret", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("the loader's", "document", false)]
    [InlineData("the loader's", "data", false)]
    [InlineData("the loader's", "data", true)]
    [InlineData("MapPages()'s", "data", false)]
    [InlineData("MapPages()'s", "data", true)]
    public async Task AnUnsupportedLocale_BeforeAuthorization_NeverRunsAProtectedLayoutLoaderOnThePagesChainForAVisitorItRefuses(
        string guard, string request, bool signedIn)
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(
            ["app/[lang]/layout.tsx", "app/[lang]/not-found.tsx", "app/[lang]/page.tsx", "app/[lang]/admin/layout.tsx", "app/[lang]/admin/not-found.tsx", "app/[lang]/admin/page.tsx"],
            map: a =>
            {
                var loader = a.MapLayoutLoader("/[lang]/admin", () =>
                {
                    calls++;
                    return Page.Props(new { Secret = "s3cret" });
                });
                if (guard == "the loader's")
                {
                    loader.RequireAuthorization();
                }
            },
            pages: p =>
            {
                if (guard == "MapPages()'s")
                {
                    p.RequireAuthorization();
                }
            },
            services: s =>
            {
                (signedIn ? (Action<IServiceCollection>)Ada : Nobody)(s);
                s.AddNetjesLocalization(o => o.SupportedCultures = ["en", "nl"]);
            },
            // Right after UseNetjes, as the routing guide puts it: its 404 comes before the authorization middleware.
            configureApp: a =>
            {
                a.UseNetjesLocalization();
                a.UseAuthorization();
            });

        var (status, body) = await RequestAsync(app, "/xx/admin", request);
        var payload = JsonNode.Parse(body)!;
        var ids = payload["segments"]!.AsArray().Select(s => (string?)s!["id"]).ToList();

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal(signedIn ? 1 : 0, calls);
        Assert.Equal(signedIn ? "app/[lang]/admin/not-found.tsx" : "app/[lang]/not-found.tsx", (string?)payload["page"]);
        Assert.Equal(signedIn ? ["app/[lang]", "app/[lang]/admin"] : ["app/[lang]"], ids);
        Assert.Equal(signedIn, body.Contains("s3cret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnsupportedLocale_ForAPageWhoseOwnLoaderRefusesTheVisitor_RendersInsideTheLayoutsTheyMayUse()
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(
            ["app/[lang]/layout.tsx", "app/[lang]/not-found.tsx", "app/[lang]/page.tsx", "app/[lang]/admin/page.tsx"],
            map: a =>
            {
                a.MapLayoutLoader("/[lang]", () =>
                {
                    calls++;
                    return Page.Props(new { Site = "public" });
                });
                a.MapPageLoader("/[lang]/admin", () => Page.Props()).RequireAuthorization();
            },
            services: s =>
            {
                Nobody(s);
                s.AddNetjesLocalization(o => o.SupportedCultures = ["en", "nl"]);
            },
            configureApp: a =>
            {
                a.UseNetjesLocalization();
                a.UseAuthorization();
            });

        var (status, body) = await RequestAsync(app, "/xx/admin", "data");

        // As on the page's 401 for the same visitor: its own loader's refusal says nothing about the layout above it.
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal(1, calls);
        Assert.Equal(["app/[lang]"], JsonNode.Parse(body)!["segments"]!.AsArray().Select(s => (string?)s!["id"]));
    }

    private sealed class ThrowingHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            throw new InvalidOperationException("The identity provider's metadata could not be fetched");
    }

    /// <summary>The way ASP.NET Core documents to hide a protected page: a refusal answered with an empty 404.</summary>
    private sealed class RefusalAs404 : Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler
    {
        private readonly Microsoft.AspNetCore.Authorization.Policy.AuthorizationMiddlewareResultHandler _default = new();

        public Task HandleAsync(
            Microsoft.AspNetCore.Http.RequestDelegate next, Microsoft.AspNetCore.Http.HttpContext context,
            Microsoft.AspNetCore.Authorization.AuthorizationPolicy policy, Microsoft.AspNetCore.Authorization.Policy.PolicyAuthorizationResult result)
        {
            if (result.Challenged || result.Forbidden)
            {
                context.Response.StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }

            return _default.HandleAsync(next, context, policy, result);
        }
    }

    /// <summary>
    /// The ways a page's own status or error page renders before the authorization middleware has let its visitor in,
    /// with the status each keeps: an empty 429 from a middleware ahead of it (a rate limiter), an exception thrown there,
    /// an authentication handler that throws (the guides put UseAuthentication() after UseNetjes()), and a refusal the
    /// app answers with an empty 404.
    /// </summary>
    public static TheoryData<string, string, bool> BeforeAuthorization => new()
    {
        { "a middleware's 429", "document", false },
        { "a middleware's 429", "data", false },
        { "a middleware's 429", "data", true },
        { "an exception in a middleware", "document", false },
        { "an exception in a middleware", "document", true },
        { "an authentication handler that throws", "document", false },
        { "a refusal answered with 404", "document", false },
        { "a refusal answered with 404", "data", false },
    };

    [Theory]
    [MemberData(nameof(BeforeAuthorization))]
    public async Task APagesBoundary_BeforeAuthorizationLetsTheVisitorIn_NeverRunsALayoutLoaderItRefusesThem(string how, string request, bool signedIn)
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/error.tsx", "app/not-found.tsx", "app/page.tsx",
             "app/admin/layout.tsx", "app/admin/error.tsx", "app/admin/not-found.tsx", "app/admin/page.tsx"],
            map: a => a.MapLayoutLoader("/admin", () =>
            {
                calls++;
                return Page.Props(new { Secret = "s3cret" });
            }).RequireAuthorization(),
            services: s =>
            {
                if (how == "an authentication handler that throws")
                {
                    s.AddAuthorization().AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, ThrowingHandler>("Test", null);
                }
                else
                {
                    (signedIn ? (Action<IServiceCollection>)Ada : Nobody)(s);
                }

                if (how == "a refusal answered with 404")
                {
                    s.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, RefusalAs404>();
                }
            },
            configureApp: a =>
            {
                a.Use(async (http, next) =>
                {
                    switch (how)
                    {
                        case "a middleware's 429":
                            http.Response.StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status429TooManyRequests;
                            return;
                        case "an exception in a middleware":
                            throw new InvalidOperationException("boom");
                    }

                    await next(http);
                });
                if (how == "an authentication handler that throws")
                {
                    a.UseAuthentication();
                }

                a.UseAuthorization();
            });

        var (status, body) = await RequestAsync(app, "/admin", request);
        var payload = JsonNode.Parse(body)!;
        var ids = payload["segments"]!.AsArray().Select(s => (string?)s!["id"]).ToList();

        Assert.Equal(how switch
        {
            "a middleware's 429" => HttpStatusCode.TooManyRequests,
            "a refusal answered with 404" => HttpStatusCode.NotFound,
            _ => HttpStatusCode.InternalServerError,
        }, status);
        Assert.Equal(signedIn ? 1 : 0, calls);
        Assert.Equal(signedIn, body.Contains("s3cret", StringComparison.Ordinal));
        if (signedIn)
        {
            Assert.Equal(["app", "app/admin"], ids);
        }
        else
        {
            // The nearest boundary above the refused section, inside the layouts above it.
            Assert.Equal(how == "a refusal answered with 404" ? "app/not-found.tsx" : "app/error.tsx", (string?)payload["page"]);
            Assert.Equal(["app"], ids);
        }
    }

    /// <summary>
    /// A GET over a raw socket that declares a body of <paramref name="declared"/> bytes and sends none, in HTTP/1.0 so the
    /// answer comes unchunked.
    /// </summary>
    private static async Task<(int Status, string Body)> GetDeclaringABodyAsync(TestApp app, string path, long declared, bool data)
    {
        var uri = app.Client.BaseAddress!;
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(uri.Host, uri.Port, Ct);
        await using var stream = tcp.GetStream();
        var head = $"GET {path} HTTP/1.0\r\nHost: {uri.Authority}\r\nContent-Length: {declared}\r\n"
            + (data ? $"{NetjesHeaders.Data}: 1\r\n" : "") + "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), Ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(Ct);
        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return (int.Parse(text[..split].Split("\r\n")[0].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture), text[(split + 4)..]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABodyOverTheLimit_OnAProtectedPage_GetsThe413WithoutRunningALayoutLoaderNothingAuthorized(bool data)
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/error.tsx", "app/unauthorized.tsx", "app/page.tsx"],
            map: a => a.MapLayoutLoader("/", () =>
            {
                calls++;
                return Page.Props(new { Secret = "s3cret" });
            }).RequireAuthorization(),
            services: Nobody,
            configureApp: a => a.UseAuthorization(),
            kestrel: true);

        // Above the server's 30 MB: Netjes answers 413 before the authorization middleware runs.
        var (status, body) = await GetDeclaringABodyAsync(app, "/", 40_000_000, data);

        Assert.Equal(413, status);
        Assert.Equal(0, calls);
        Assert.DoesNotContain("s3cret", body + string.Join("\n", app.Renderer.Payloads));
        Assert.Empty(JsonNode.Parse(data ? body : app.Renderer.Payloads.Last())!["segments"]!.AsArray());
    }

    [Fact]
    public async Task UnknownUrl_UnderMapPagesAuthorization_StillRendersTheLayoutsWithoutALoader()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/not-found.tsx", "app/page.tsx"],
            services: Nobody,
            configureApp: a => a.UseAuthorization(),
            pages: p => p.RequireAuthorization());

        var (status, body) = await RequestAsync(app, "/nope", "data");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal(["app"], JsonNode.Parse(body)!["segments"]!.AsArray().Select(s => (string?)s!["id"]));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/nope")]
    public async Task ALayoutLoaderThatAllowsAnonymousVisitors_UnderMapPagesAuthorization_RunsOnTheirNotFoundAsOnTheirPages(string url)
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/not-found.tsx", "app/page.tsx"],
            map: a => a.MapLayoutLoader("/", () =>
            {
                calls++;
                return Page.Props(new { Site = "public" });
            }).AllowAnonymous(),
            services: Nobody,
            configureApp: a => a.UseAuthorization(),
            pages: p => p.RequireAuthorization());

        var (status, body) = await RequestAsync(app, url, "data");

        // The loader's AllowAnonymous() reaches every page below it, as it would reach a page at the unknown URL.
        Assert.Equal(url == "/" ? HttpStatusCode.OK : HttpStatusCode.NotFound, status);
        Assert.Equal(1, calls);
        Assert.Equal(["app"], JsonNode.Parse(body)!["segments"]!.AsArray().Select(s => (string?)s!["id"]));
    }

    [Fact]
    public async Task UnknownUrlBelowAMountThatAllowsAnonymousVisitors_UnderMapPagesAuthorization_RendersThePackagesNotFoundInsideItsLayouts()
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/not-found.tsx", "app/page.tsx"],
            prepareRoot: root => TestPagePackage.Install(root, ["layout.tsx", "not-found.tsx", "page.tsx"]),
            map: a => a.MapPagePackage(TestPagePackage.Descriptor, "/help").AllowAnonymous().MapLayoutLoader("/", () =>
            {
                calls++;
                return Page.Props(new { Nav = "help-nav" });
            }),
            services: Nobody,
            configureApp: a => a.UseAuthorization(),
            pages: p => p.RequireAuthorization());

        // Every page of the package lets a visitor without a user in, and runs the loader for them; so does its 404.
        var (pageStatus, _) = await RequestAsync(app, "/help", "data");
        var (status, body) = await RequestAsync(app, "/help/nope", "data");
        var payload = JsonNode.Parse(body)!;

        Assert.Equal(HttpStatusCode.OK, pageStatus);
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal($"{Help}/app/not-found.tsx", (string?)payload["page"]);
        Assert.Equal(["app", "app/help"], payload["segments"]!.AsArray().Select(s => (string?)s!["id"]));
        Assert.Equal(2, calls);
    }

    /// <summary>
    /// Where a visitor without a user meets a root layout loader of no authorization of its own, in an app whose pages
    /// <c>MapPages().RequireAuthorization()</c> or a fallback policy protects: an unknown URL, a page's own 401, a server
    /// action's 401, a forbidden page a public minimal API asks for, and an empty 429 from a middleware ahead of
    /// authorization on a page.
    /// </summary>
    public static TheoryData<string, string, string> EveryPageProtected => new()
    {
        { "MapPages()'s", "/nope", "document" },
        { "MapPages()'s", "/admin", "document" },
        { "MapPages()'s", "/admin", "data" },
        { "MapPages()'s", "a server action", "document" },
        { "MapPages()'s", "a public minimal API's Page.Forbidden()", "document" },
        { "MapPages()'s", "a middleware's 429 on /admin", "data" },
        { "the fallback policy", "/nope", "data" },
        { "the fallback policy", "/admin", "document" },
        { "the fallback policy", "/admin", "data" },
        { "the fallback policy", "a server action", "document" },
        { "the fallback policy", "a public minimal API's Page.Forbidden()", "document" },
        { "the fallback policy", "a middleware's 429 on /admin", "data" },
    };

    [Theory]
    [MemberData(nameof(EveryPageProtected))]
    public async Task WhenEveryPageIsProtected_AVisitorWithoutAUser_NeverRunsTheRootLayoutLoader(string guard, string where, string request)
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/error.tsx", "app/not-found.tsx", "app/forbidden.tsx", "app/unauthorized.tsx", "app/page.tsx", "app/admin/page.tsx"],
            map: a =>
            {
                a.MapLayoutLoader("/", () =>
                {
                    calls++;
                    return Page.Props(new { Secret = "s3cret" });
                });
                a.MapAction("secure", () => ServerAction.Ok()).RequireAuthorization();
                a.MapGet("/api/refused", () => Page.Forbidden()).AllowAnonymous();
            },
            services: s =>
            {
                Nobody(s);
                if (guard == "the fallback policy")
                {
                    s.AddAuthorization(o => o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
                }
            },
            configureApp: a =>
            {
                a.Use(async (http, next) =>
                {
                    if (where == "a middleware's 429 on /admin")
                    {
                        http.Response.StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status429TooManyRequests;
                        return;
                    }

                    await next(http);
                });
                a.UseAuthorization();
            },
            pages: p =>
            {
                if (guard == "MapPages()'s")
                {
                    p.RequireAuthorization();
                }
            });

        HttpStatusCode status;
        string body;
        if (where == "a server action")
        {
            var response = await app.Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/_netjes/actions/secure") { Content = new FormUrlEncodedContent([]) }, Ct);
            (status, body) = (response.StatusCode, app.Renderer.Payloads.Last());
        }
        else
        {
            (status, body) = await RequestAsync(app, where switch
            {
                "a public minimal API's Page.Forbidden()" => "/api/refused",
                "a middleware's 429 on /admin" => "/admin",
                _ => where,
            }, request);
        }

        Assert.Equal(where switch
        {
            "/nope" => guard == "MapPages()'s" ? HttpStatusCode.NotFound : HttpStatusCode.Unauthorized,
            "a public minimal API's Page.Forbidden()" => HttpStatusCode.Forbidden,
            "a middleware's 429 on /admin" => HttpStatusCode.TooManyRequests,
            _ => HttpStatusCode.Unauthorized,
        }, status);
        Assert.Equal(0, calls);
        Assert.DoesNotContain("s3cret", body);
        // The root layout has a loader, so the page renders without it.
        Assert.Empty(JsonNode.Parse(body)!["segments"]!.AsArray());
    }

    /// <summary>
    /// A policy that names its authentication scheme, which the authorization middleware authenticates in place of the
    /// request's user: on the layout loader, on <c>MapPages()</c> or on a page package's mount, naming a scheme that
    /// signs nobody in while the default one signs Ada in, or the other way round.
    /// </summary>
    public static TheoryData<string, string> SchemePolicies => new()
    {
        { "the loader's", "refuses Ada" },
        { "the loader's", "signs Ada in" },
        { "MapPages()'s", "refuses Ada" },
        { "MapPages()'s", "signs Ada in" },
        { "the mount's", "refuses Ada" },
        { "the mount's", "signs Ada in" },
    };

    [Theory]
    [MemberData(nameof(SchemePolicies))]
    public async Task APolicyNamingItsScheme_IsJudgedOnThatScheme_OnAnUnknownUrlAsOnAPage(string guard, string scheme)
    {
        var calls = 0;
        var policy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder("Admin").RequireAuthenticatedUser().Build();
        var mounted = guard == "the mount's";
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/error.tsx", "app/not-found.tsx", "app/unauthorized.tsx", "app/page.tsx"],
            prepareRoot: mounted ? root => TestPagePackage.Install(root, ["layout.tsx", "not-found.tsx", "page.tsx"]) : null,
            map: a =>
            {
                var secret = () =>
                {
                    calls++;
                    return Page.Props(new { Secret = "s3cret" });
                };
                if (mounted)
                {
                    a.MapPagePackage(TestPagePackage.Descriptor, "/help").RequireAuthorization(policy).MapLayoutLoader("/", secret);
                }
                else
                {
                    var loader = a.MapLayoutLoader("/", secret);
                    if (guard == "the loader's")
                    {
                        loader.RequireAuthorization(policy);
                    }
                }
            },
            services: s =>
            {
                var site = scheme == "refuses Ada";
                var builder = s.AddAuthorization().AddAuthentication("Site");
                if (site)
                {
                    builder.AddScheme<AuthenticationSchemeOptions, AdaHandler>("Site", null).AddScheme<AuthenticationSchemeOptions, NobodyHandler>("Admin", null);
                }
                else
                {
                    builder.AddScheme<AuthenticationSchemeOptions, NobodyHandler>("Site", null).AddScheme<AuthenticationSchemeOptions, AdaHandler>("Admin", null);
                }
            },
            configureApp: a => a.UseAuthorization(),
            pages: p =>
            {
                if (guard == "MapPages()'s")
                {
                    p.RequireAuthorization(policy);
                }
            });

        var page = mounted ? "/help" : "/";
        var (pageStatus, _) = await RequestAsync(app, page, "data");
        var (status, body) = await RequestAsync(app, mounted ? "/help/nope" : "/nope", "data");

        // The page and the unknown URL agree: the middleware lets Ada in only through the scheme the policy names.
        var admitted = scheme == "signs Ada in";
        Assert.Equal(admitted ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, pageStatus);
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal(admitted ? 2 : 0, calls);
        Assert.Equal(admitted, body.Contains("s3cret", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("document")]
    [InlineData("data")]
    public async Task UnknownUrlUnderABasePath_NeverRunsAProtectedRootLayoutLoaderForAVisitorItRefuses(string request)
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/not-found.tsx", "app/page.tsx"],
            map: a => a.MapLayoutLoader("/", () =>
            {
                calls++;
                return Page.Props(new { Secret = "s3cret" });
            }).RequireAuthorization(),
            services: Nobody,
            configureApp: a => a.UseAuthorization(),
            pathBase: "/base");

        var (status, body) = await RequestAsync(app, "/base/nope", request);

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal(0, calls);
        Assert.Empty(JsonNode.Parse(body)!["segments"]!.AsArray());
        Assert.DoesNotContain("s3cret", body);
    }

    [Fact]
    public async Task ForgedSegmentKey_DoesNotSkipAGuardingLayoutLoader()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/error.tsx", "app/[team]/layout.tsx", "app/[team]/page.tsx", "app/[team]/settings/page.tsx"],
            map: a =>
            {
                a.MapLayoutLoader("/[team]", (string team) => team == "mine" ? Page.Props(new { Team = team }) : Page.Forbidden());
                a.MapPageLoader("/[team]/settings", (string team) => Page.Props(new { Billing = "billing of " + team }));
            });
        var version = (string)(await app.GetDataJsonAsync("/mine"))["version"]!;
        // What the key was before it was signed: anyone could compute it.
        var text = "app/[team]\n" + version + "\n\n\ntheirs";
        var forgedKey = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 16).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var request = new HttpRequestMessage(HttpMethod.Get, "/theirs/settings");
        request.Headers.Add(NetjesHeaders.Data, "1");
        request.Headers.Add(NetjesHeaders.Segments, forgedKey);

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("billing", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task SegmentKey_ForRouteValuesWithALineBreak_DoesNotSkipAGuardWhereTheLineBreakFallsElsewhere()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/error.tsx", "app/[team]/[project]/layout.tsx", "app/[team]/[project]/page.tsx"],
            map: a =>
            {
                // Team 'a' is someone else's; the projects of any other team are open.
                a.MapLayoutLoader("/[team]/[project]", (string team, string project) => team == "a" ? Page.Forbidden() : Page.Props(new { Team = team }));
                a.MapPageLoader("/[team]/[project]", (string team, string project) => Page.Props(new { Billing = "billing of " + team }));
            });
        // %0A decodes to a line break in the route value: team "a\nb", project "c".
        var key = (string)(await app.GetDataJsonAsync("/a%0Ab/c"))["segments"]![1]!["key"]!;
        var request = new HttpRequestMessage(HttpMethod.Get, "/a/b%0Ac");
        request.Headers.Add(NetjesHeaders.Data, "1");
        request.Headers.Add(NetjesHeaders.Segments, key);

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("billing", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task NoJsValidationFailure_UnderAnOutputCacheBasePolicy_IsNotAServerError()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx", "app/guestbook/page.tsx"],
            map: a => a.MapAction("sign", ([FromForm] string? name) => ServerAction.Invalid("name", "required")),
            services: s => s.AddOutputCache(o => o.AddBasePolicy(b => b.Expire(TimeSpan.FromMinutes(1)))),
            configureApp: a => a.UseOutputCache(),
            environment: "Testing",
            mode: NetjesMode.Development);
        var token = await FetchedTokenAsync(app);

        var response = await app.Client.SendAsync(NoJsPost("sign", token, ("name", ""), ("__netjes_return", "/guestbook")), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task TokenEndpoint_IsNeverServedFromAnOutputCacheBasePolicy()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            services: s => s.AddOutputCache(o => o.AddBasePolicy(b => b.Expire(TimeSpan.FromMinutes(1)))),
            configureApp: a => a.UseOutputCache(),
            environment: "Testing",
            mode: NetjesMode.Development);

        var first = await app.Client.GetAsync("/_netjes/antiforgery", Ct);
        var second = await app.Client.GetAsync("/_netjes/antiforgery", Ct);

        Assert.True(second.Headers.Contains("Set-Cookie"));
        Assert.Null(second.Headers.Age);
        Assert.NotEqual(await first.Content.ReadAsStringAsync(Ct), await second.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Document_DoesNotWaitForDeferredValuesThePageNeverRead()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props(new { Never = Page.Defer(new TaskCompletionSource<int>().Task) })));

        var response = await app.GetDocumentAsync("/").WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Document_IsNotHeldOpenByADeliveryToASidecarThatStoppedAnswering()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props(new { Late = Page.Defer(Task.Delay(50).ContinueWith(_ => 1)) })));
        // The page never reads the value, so the document is complete at once; the value's delivery then hangs, as it
        // would while the sidecar process restarts.
        app.Renderer.OnResolve = cancellationToken => Task.Delay(Timeout.Infinite, cancellationToken);
        app.Renderer.StreamHandler = async (_, _) =>
        {
            await Task.Delay(200, Ct);
            return new MemoryStream("<!DOCTYPE html><html><body>done</body></html>"u8.ToArray());
        };

        var response = await app.GetDocumentAsync("/").WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Document_IsNotBrokenByADeliveryThatFails_OnceItHasBeenSent()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props(new { Late = Page.Defer(Task.Delay(20).ContinueWith(_ => 1)) })));
        // A crashed sidecar process: the delivery fails outright.
        app.Renderer.OnResolve = _ => throw new HttpRequestException("Connection refused");
        app.Renderer.StreamHandler = async (_, _) =>
        {
            await Task.Delay(200, Ct);
            return new MemoryStream("<!DOCTYPE html><html><body>done</body></html>"u8.ToArray());
        };

        var response = await app.GetDocumentAsync("/").WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("done", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task DataStream_EndsAfterTheStreamTimeout_ReportingWhatNeverArrived()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configure: o => o.StreamTimeout = TimeSpan.FromMilliseconds(300),
            map: a => a.MapPageLoader("/", () => Page.Props(new { Never = Page.Defer(new TaskCompletionSource<int>().Task) })));

        var response = await app.GetDataAsync("/").WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var lines = (await response.Content.ReadAsStringAsync(Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.Equal("d0", (string?)JsonNode.Parse(lines[1])!["id"]);
        Assert.NotNull((string?)JsonNode.Parse(lines[1])!["error"]);
    }

    [Fact]
    public async Task CrawlerDocument_WithAValueThatNeverArrives_IsStillComplete()
    {
        // The deadlines run on the app's clock, which only this test moves: the value is reported as failed while the
        // delivery is still open, however late the thread pool runs what follows the first deadline.
        var time = new ManualTime();
        var limit = TimeSpan.FromMinutes(10);
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configure: o => o.StreamTimeout = limit,
            services: s => s.AddSingleton<TimeProvider>(time),
            map: a => a.MapPageLoader("/", () => Page.Props(new { Never = Page.Defer(new TaskCompletionSource<int>().Task) })));
        var rendering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Renderer.StreamHandler = async (_, options) =>
        {
            // Like the sidecar for a crawler: the document once every value has settled. The real one gives up at
            // StreamTimeout, after .NET has reported the value as failed (DeferredDeadlineTests); this one waits for the
            // report.
            rendering.TrySetResult();
            while (!app.Renderer.Resolved.Any(r => r.RenderId == options.RenderId && r.Id == "d0"))
            {
                await Task.Delay(5, Ct);
            }

            return new MemoryStream("<!DOCTYPE html><html><body>failed boundary</body></html>"u8.ToArray());
        };
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)");

        var sending = app.Client.SendAsync(request, Ct);
        await rendering.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        time.Advance(PageRenderer.DeferredDeadline(limit));
        var response = await sending.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("failed boundary", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("The value did not arrive in time", Assert.Single(app.Renderer.Resolved).Error);
    }

    /// <summary>A clock that moves only when <see cref="Advance"/> moves it, firing the timers that come due on the way.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;
        private readonly List<Timer> _timers = [];
        private TimeSpan _elapsed;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_timers)
            {
                return _start + _elapsed;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            List<Timer> due;
            lock (_timers)
            {
                _elapsed += by;
                due = [.. _timers.Where(t => t.DueAt <= _elapsed)];
                foreach (var timer in due)
                {
                    _timers.Remove(timer);
                    if (timer.Period > TimeSpan.Zero && timer.Period != Timeout.InfiniteTimeSpan)
                    {
                        timer.DueAt = _elapsed + timer.Period;
                        _timers.Add(timer);
                    }
                }
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private sealed class Timer(ManualTime clock, TimerCallback callback, object? state) : ITimer
        {
            public TimeSpan DueAt { get; set; }

            public TimeSpan Period { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._timers)
                {
                    clock._timers.Remove(this);
                    Period = period;
                    if (dueTime != Timeout.InfiniteTimeSpan)
                    {
                        DueAt = clock._elapsed + dueTime;
                        clock._timers.Add(this);
                    }
                }

                return true;
            }

            public void Fire() => callback(state);

            public void Dispose()
            {
                lock (clock._timers)
                {
                    clock._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    public sealed class Node
    {
        public Node? Next { get; set; }
    }

    [Fact]
    public async Task DeferredValueThatCannotBeSerialized_FailsAlone()
    {
        var cycle = new Node();
        cycle.Next = cycle;
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props(new
            {
                Broken = Page.Defer(Task.FromResult(cycle)),
                Fine = Page.Defer(Task.Delay(100).ContinueWith(_ => 42)),
            })));

        var response = await app.GetDataAsync("/").WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var lines = (await response.Content.ReadAsStringAsync(Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(l => JsonNode.Parse(l)!).ToList();

        Assert.Equal(["d0", "d1"], lines.Select(l => (string?)l["id"]));
        Assert.NotNull((string?)lines[0]["error"]);
        Assert.Equal(42, (int?)lines[1]["value"]);
        Assert.Contains(app.Logs, l => l.Level >= LogLevel.Warning && l.Message.Contains("d0"));
    }

    public sealed class DisposedProxy
    {
        // What a lazy-loading proxy does once Page.Defer(sp => ...) has disposed its DbContext.
        public int Count => throw new ObjectDisposedException("TaskBoardDb");
    }

    public sealed class Unwritable
    {
        public Type Kind { get; } = typeof(int);
    }

    public sealed class CancelledGetter
    {
        public int Count => throw new OperationCanceledException("the query was cancelled");
    }

    [Theory]
    [InlineData("getter")]
    [InlineData("type")]
    [InlineData("cancelled")]
    public async Task DeferredValueWhoseSerializationThrows_FailsAlone(string kind)
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props(new
            {
                Broken = Page.Defer(Task.FromResult<object>(kind switch
                {
                    "getter" => new DisposedProxy(),
                    "cancelled" => new CancelledGetter(),
                    _ => new Unwritable(),
                })),
                Fine = Page.Defer(Task.Delay(100).ContinueWith(_ => 42)),
            })));

        var response = await app.GetDataAsync("/").WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var lines = (await response.Content.ReadAsStringAsync(Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(l => JsonNode.Parse(l)!).ToList();

        Assert.Equal(["d0", "d1"], lines.Select(l => (string?)l["id"]));
        Assert.NotNull((string?)lines[0]["error"]);
        Assert.Equal(42, (int?)lines[1]["value"]);
    }

    [Fact]
    public async Task FailedDeferredValues_AreLogged()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props(new { Broken = Page.Defer(Task.FromException<int>(new InvalidOperationException("comments down"))) })));

        await (await app.GetDataAsync("/")).Content.ReadAsStringAsync(Ct);

        Assert.Contains(app.Logs, l => l.Level >= LogLevel.Warning && l.Message.Contains("comments down"));
    }

    public sealed class ScopedThing
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    [Fact]
    public async Task DeferWithServices_RunsInItsOwnScope()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", (ScopedThing requestThing) => Page.Props(new
            {
                Request = requestThing.Id,
                Deferred = Page.Defer(async services =>
                {
                    await Task.Yield();
                    return services.GetRequiredService<ScopedThing>().Id;
                }),
            })),
            services: s => s.AddScoped<ScopedThing>());

        var lines = (await (await app.GetDataAsync("/")).Content.ReadAsStringAsync(Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.NotEqual((string?)JsonNode.Parse(lines[0])!["props"]!["request"], (string?)JsonNode.Parse(lines[1])!["value"]);
    }

    [Theory]
    [InlineData("/\t/evil.example")]
    [InlineData("/\n/evil.example")]
    [InlineData("\t//evil.example")]
    public async Task NoJsReturnUrl_WithControlCharacters_GoesHome(string returnUrl)
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"], map: a => a.MapAction("sign", () => ServerAction.Ok()));
        var token = await TokenAsync(app);

        var response = await app.Client.SendAsync(NoJsPost("sign", token, ("__netjes_return", returnUrl)), Ct);

        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }

    // The forms below declare every field they post, so the private words alone decide what a failure echoes.
    public sealed class LoginForm
    {
        public string? Email { get; init; }
        public string? Password { get; init; }
        public string? ConfirmPassword { get; init; }
    }

    public sealed class PaymentForm
    {
        public string? Email { get; init; }
        public string? ShippingAddress { get; init; }
        public string? NewPwd { get; init; }
        [JsonPropertyName("pin_code")] public string? PinCode { get; init; }
        public string? Otp { get; init; }
        public string? CardNumber { get; init; }
        [JsonPropertyName("card-cvc")] public string? CardCvc { get; init; }
        public string? ApiToken { get; init; }
    }

    public sealed class LookAlikeForm
    {
        public string? Email { get; init; }
        public string? Password2 { get; init; }
        public string? Newpassword { get; init; }
        public string? PINCode { get; init; }
        public string? Pin2 { get; init; }
        public string? OTPValue { get; init; }
        public string? Cardnumber { get; init; }
        public string? Creditcard { get; init; }
        public string? Cardno { get; init; }
        public string? Cardcvc { get; init; }
        public string? Newpwd { get; init; }
        public string? Otpcode { get; init; }
        public string? Ibannumber { get; init; }
        public string? Ssnlast4 { get; init; }
        public string? X1Pin { get; init; }
        public string? Step2Otp { get; init; }
        [JsonPropertyName("cc-number")] public string? CcNumber { get; init; }
        [JsonPropertyName("cc-csc")] public string? CcCsc { get; init; }
        public string? Spinner { get; init; }
        public string? ShippingAddress { get; init; }
        public string? Account { get; init; }
    }

    [Fact]
    public async Task NoJsValidationFailure_DoesNotEchoPasswords()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx", "app/login/page.tsx"],
            map: a => a.MapAction("login", ([FromForm] LoginForm input) => ServerAction.Invalid("email", "unknown")));
        var token = await TokenAsync(app);

        await app.Client.SendAsync(
            NoJsPost("login", token, ("email", "ada@example.com"), ("password", "hunter2"), ("confirmPassword", "hunter2"), ("__netjes_return", "/login")),
            Ct);

        var values = app.Renderer.LastPayload["actionState"]!["values"]!;
        Assert.Equal("ada@example.com", (string?)values["email"]);
        Assert.Null(values["password"]);
        Assert.Null(values["confirmPassword"]);
    }

    [Fact]
    public async Task NoJsValidationFailure_DoesNotEchoSecretsByTheWordsInTheirNames()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx", "app/checkout/page.tsx"],
            map: a => a.MapAction("pay", ([FromForm] PaymentForm input) => ServerAction.Invalid("email", "unknown")));
        var token = await TokenAsync(app);

        await app.Client.SendAsync(
            NoJsPost("pay", token,
                ("email", "ada@example.com"), ("shippingAddress", "1 Main St"), ("newPwd", "x"), ("pin_code", "1234"), ("otp", "999"),
                ("cardNumber", "4242"), ("card-cvc", "123"), ("apiToken", "t"), ("__netjes_return", "/checkout")),
            Ct);

        var values = app.Renderer.LastPayload["actionState"]!["values"]!.AsObject();
        Assert.Equal(["email", "shippingAddress"], values.Select(v => v.Key));
    }

    [Fact]
    public async Task NoJsValidationFailure_HidesRunTogetherAndAcronymNames_TooButNotLookAlikes()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx", "app/checkout/page.tsx"],
            map: a => a.MapAction("pay", ([FromForm] LookAlikeForm input) => ServerAction.Invalid("email", "unknown")));
        var token = await TokenAsync(app);

        await app.Client.SendAsync(
            NoJsPost("pay", token,
                ("email", "a@b.c"), ("password2", "x"), ("newpassword", "x"), ("PINCode", "1"), ("pin2", "1"), ("OTPValue", "1"),
                ("cardnumber", "1"), ("creditcard", "1"), ("cardno", "1"), ("cardcvc", "1"), ("newpwd", "1"), ("NEWPWD", "1"),
                ("otpcode", "1"), ("ibannumber", "1"), ("ssnlast4", "1"), ("x1Pin", "1"), ("step2Otp", "1"), ("cc-number", "1"), ("cc-csc", "1"),
                ("spinner", "on"), ("shippingAddress", "1 Main St"), ("account", "acme"), ("__netjes_return", "/checkout")),
            Ct);

        // Hiding a field by mistake costs a retype; echoing a card number puts it in the page.
        var values = app.Renderer.LastPayload["actionState"]!["values"]!.AsObject();
        Assert.Equal(["email", "spinner", "shippingAddress", "account"], values.Select(v => v.Key));
    }

    public sealed class AccountForm
    {
        public string? Email { get; init; }
        public string? Pass { get; init; }
        public string? NewPw { get; init; }
        public string? ApiKey { get; init; }
        [JsonPropertyName("api_key")] public string? ApiKey2 { get; init; }
        public string? PrivateKey { get; init; }
        public string? Credential { get; init; }
        [JsonPropertyName("mfa_code")] public string? MfaCode { get; init; }
        public string? RecoveryCode { get; init; }
        public string? Bsn { get; init; }
        public string? PostalCode { get; init; }
    }

    [Fact]
    public async Task NoJsValidationFailure_HidesKeysCredentialsAndRecoveryCodes()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx", "app/account/page.tsx"],
            map: a => a.MapAction("account", ([FromForm] AccountForm input) => ServerAction.Invalid("email", "unknown")));
        var token = await TokenAsync(app);

        await app.Client.SendAsync(
            NoJsPost("account", token,
                ("email", "a@b.c"), ("pass", "x"), ("newPw", "x"), ("apiKey", "x"), ("api_key", "x"), ("privateKey", "x"), ("credential", "x"),
                ("mfa_code", "1"), ("recoveryCode", "1"), ("bsn", "1"), ("postalCode", "1234 AB"), ("__netjes_return", "/account")),
            Ct);

        var values = app.Renderer.LastPayload["actionState"]!["values"]!.AsObject();
        Assert.Equal(["email", "postalCode"], values.Select(v => v.Key));
    }

    [Fact]
    public async Task NoJsValidationFailure_EchoesWhatPrivateFormFieldsAllows()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx", "app/checkout/page.tsx"],
            configure: o => o.PrivateFormFields = ["iban"],
            map: a => a.MapAction("pay", ([FromForm] string email, [FromForm] string pin, [FromForm] string iban) => ServerAction.Invalid("email", "unknown")));
        var token = await TokenAsync(app);

        await app.Client.SendAsync(NoJsPost("pay", token, ("email", "a@b.c"), ("pin", "1"), ("iban", "NL00"), ("__netjes_return", "/checkout")), Ct);

        var values = app.Renderer.LastPayload["actionState"]!["values"]!.AsObject();
        Assert.Equal(["email", "pin"], values.Select(v => v.Key));
    }

    public sealed class IntegrationForm
    {
        public string? Email { get; init; }
        [JsonPropertyName("api_key")] public string? ApiKey { get; init; }
        [JsonPropertyName("auth-code")] public string? AuthCode { get; init; }
        public string? ApiKeyHint { get; init; }
    }

    [Fact]
    public async Task NoJsValidationFailure_HidesPrivateWordsWrittenWithSeparators()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx", "app/settings/page.tsx"],
            configure: o => o.PrivateFormFields = ["api_key", "auth-code"],
            map: a => a.MapAction("connect", ([FromForm] IntegrationForm input) => ServerAction.Invalid("email", "unknown")));
        var token = await TokenAsync(app);

        await app.Client.SendAsync(
            NoJsPost("connect", token, ("email", "a@b.c"), ("api_key", "x"), ("auth-code", "1"), ("apiKeyHint", "x"), ("__netjes_return", "/settings")),
            Ct);

        var values = app.Renderer.LastPayload["actionState"]!["values"]!.AsObject();
        Assert.Equal(["email"], values.Select(v => v.Key));
    }

    [Fact]
    public async Task ActionCall_FromOriginNull_IsForbidden()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"], map: a => a.MapAction("noop", () => ServerAction.Ok()));
        var token = await TokenAsync(app);
        var request = NoJsPost("noop", token);
        request.Headers.Add(NetjesHeaders.Data, "1");
        request.Headers.Add("Origin", "null");

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("cross-site", HttpStatusCode.Forbidden)]
    [InlineData("same-site", HttpStatusCode.Forbidden)]
    [InlineData("same-origin", HttpStatusCode.OK)]
    public async Task ActionCall_FromOriginNull_IsJudgedBySecFetchSite(string site, HttpStatusCode expected)
    {
        // A no-JS form post from a page with Referrer-Policy: no-referrer sends Origin: null, but the browser still
        // says it came from this origin.
        await using var app = await TestApp.StartAsync(["app/page.tsx"], map: a => a.MapAction("noop", () => ServerAction.Ok()));
        var token = await TokenAsync(app);
        var request = NoJsPost("noop", token);
        request.Headers.Add(NetjesHeaders.Data, "1");
        request.Headers.Add("Origin", "null");
        request.Headers.Add("Sec-Fetch-Site", site);

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task ActionCall_FromAnAllowedOrigin_IsAccepted()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configure: o => o.AllowedActionOrigins = ["https://shop.example"],
            map: a => a.MapAction("noop", () => ServerAction.Ok()));
        var token = await TokenAsync(app);
        var request = NoJsPost("noop", token);
        request.Headers.Add(NetjesHeaders.Data, "1");
        request.Headers.Add("Origin", "https://shop.example");

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
