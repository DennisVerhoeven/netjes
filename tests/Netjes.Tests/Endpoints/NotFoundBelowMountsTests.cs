using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Netjes.Routing;

namespace Netjes.Tests.Endpoints;

/// <summary>
/// An unknown URL below a page package's mount: the user its layout loaders run with, inside a back office that signs its
/// staff in through a scheme of its own, and the conventions an app gives the not-found below a mount.
/// </summary>
public class NotFoundBelowMountsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Inner = TestPagePackage.Name;

    private static readonly string Outer = TestPagePackage.Other.Name;

    /// <summary>The back office's policy: a user that its own scheme, Staff, signs in.</summary>
    private static readonly AuthorizationPolicy Staff = new AuthorizationPolicyBuilder("Staff").RequireAuthenticatedUser().Build();

    private sealed class NobodyHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }

    /// <summary>Signs Ada in, through the scheme it is registered as.</summary>
    private sealed class AdaHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ada")], Scheme.Name)), Scheme.Name)));
    }

    /// <summary>
    /// The site's own scheme, Site, is the default and signs nobody in; the back office's, Staff, signs Ada in when she is
    /// <paramref name="staff"/>.
    /// </summary>
    private static Action<IServiceCollection> Schemes(bool staff) => services =>
    {
        var builder = services.AddAuthorization().AddAuthentication("Site").AddScheme<AuthenticationSchemeOptions, NobodyHandler>("Site", null);
        if (staff)
        {
            builder.AddScheme<AuthenticationSchemeOptions, AdaHandler>("Staff", null);
        }
        else
        {
            builder.AddScheme<AuthenticationSchemeOptions, NobodyHandler>("Staff", null);
        }
    };

    /// <summary>What the desk's layout loader saw.</summary>
    private sealed class Desk
    {
        public int Calls;

        public int SignOuts;
    }

    /// <summary>The app's fallback policy: a user that the site's own scheme signs in.</summary>
    private static readonly AuthorizationPolicy SiteUsers = new AuthorizationPolicyBuilder("Site").RequireAuthenticatedUser().Build();

    /// <summary>
    /// A back office mounted at <c>/staff</c>, whose desk (<c>app/(desk)</c>) has a layout loader behind
    /// <see cref="Staff"/> that ends a sign-in it finds no user for, as a back office does for a user that is gone; and a
    /// second package mounted inside the desk at <c>/staff/(desk)/help</c>, behind the same policy.
    /// </summary>
    /// <param name="outer">More calls on the back office's mount.</param>
    /// <param name="fallbackPolicy">Whether the app sets <see cref="SiteUsers"/> as its fallback policy.</param>
    private static Task<TestApp> BackOfficeAsync(Desk desk, bool staff, Action<PagePackageBuilder>? outer = null, bool fallbackPolicy = false) =>
        TestApp.StartAsync(
            ["app/layout.tsx", "app/not-found.tsx", "app/unauthorized.tsx", "app/page.tsx"],
            prepareRoot: root =>
            {
                TestPagePackage.Install(root, ["not-found.tsx", "(desk)/layout.tsx", "(desk)/page.tsx"], name: Outer);
                TestPagePackage.Install(root, ["layout.tsx", "not-found.tsx", "page.tsx"]);
            },
            map: a =>
            {
                var office = a.MapPagePackage(TestPagePackage.Other, "/staff");
                office.MapLayoutLoader("app/(desk)", (HttpContext http) =>
                {
                    if (http.User.Identity?.IsAuthenticated != true)
                    {
                        desk.SignOuts++;
                        return Page.Redirect("/staff/sign-in");
                    }

                    desk.Calls++;
                    return Page.Props(new { Staff = http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value });
                }).RequireAuthorization(Staff);
                outer?.Invoke(office);
                a.MapPagePackage(TestPagePackage.Descriptor, "/staff/(desk)/help").RequireAuthorization(Staff);
            },
            services: s =>
            {
                Schemes(staff)(s);
                if (fallbackPolicy)
                {
                    s.AddAuthorizationBuilder().SetFallbackPolicy(SiteUsers);
                }
            },
            configureApp: a => a.UseAuthorization());

    private static IEnumerable<string?> Ids(JsonNode payload) => payload["segments"]!.AsArray().Select(s => (string?)s!["id"]);

    private static string? DeskStaff(JsonNode payload) =>
        (string?)payload["segments"]!.AsArray().Single(s => (string?)s!["id"] == "app/staff/(desk)")!["props"]?["staff"];

    [Theory]
    [InlineData("/staff/help/nope")]
    [InlineData("/staff/help/nope/deeper")]
    [InlineData("/STAFF/Help/Nope")]
    public async Task UnknownUrlBelowAMountInsideAnother_RunsTheLayoutLoadersWithTheUserTheirPoliciesSignIn(string url)
    {
        var desk = new Desk();
        await using var app = await BackOfficeAsync(desk, staff: true);

        var page = await app.GetDataJsonAsync("/staff");
        var response = await app.GetDataAsync(url);
        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        var document = await app.GetDocumentAsync(url);

        // As on the desk's own page: the loader runs for Ada, whom only the back office's scheme signs in.
        Assert.Equal("ada", DeskStaff(page));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal($"{Inner}/app/not-found.tsx", (string?)payload["page"]);
        Assert.Equal(["app", "app/staff", "app/staff/(desk)", "app/staff/(desk)/help"], Ids(payload));
        Assert.Equal("ada", DeskStaff(payload));
        Assert.Equal(HttpStatusCode.NotFound, document.StatusCode);
        Assert.Equal($"{Inner}/app/not-found.tsx", (string?)app.Renderer.LastPayload["page"]);
        Assert.Equal("ada", DeskStaff(app.Renderer.LastPayload));
        Assert.Equal(0, desk.SignOuts);
        Assert.Equal(3, desk.Calls);
    }

    [Fact]
    public async Task AStaffersNavigationFromTheDeskToAnUnknownUrlBelowTheInnerMount_ReusesTheDeskAsAPageThereWould()
    {
        var desk = new Desk();
        await using var app = await BackOfficeAsync(desk, staff: true);
        var page = await app.GetDataJsonAsync("/staff");
        var key = (string)page["segments"]!.AsArray().Single(s => (string?)s!["id"] == "app/staff/(desk)")!["key"]!;

        var request = new HttpRequestMessage(HttpMethod.Get, "/staff/help/nope");
        request.Headers.Add(NetjesHeaders.Data, "1");
        request.Headers.Add(NetjesHeaders.Segments, key);
        var response = await app.Client.SendAsync(request, Ct);
        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;

        // The desk's key names the user it loaded for, and the not-found loads it for the same one.
        var segment = payload["segments"]!.AsArray().Single(s => (string?)s!["id"] == "app/staff/(desk)")!;
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True((bool?)segment["reused"]);
        Assert.Equal(key, (string?)segment["key"]);
        Assert.Equal(1, desk.Calls);
        Assert.Equal(0, desk.SignOuts);
    }

    [Fact]
    public async Task UnknownUrlBelowAMountInsideAnother_NeverRunsTheProtectedLoadersForAVisitorTheirPoliciesRefuse()
    {
        var desk = new Desk();
        await using var app = await BackOfficeAsync(desk, staff: false);

        var response = await app.GetDataAsync("/staff/help/nope");
        var body = await response.Content.ReadAsStringAsync(Ct);
        var payload = JsonNode.Parse(body)!;
        var document = await app.GetDocumentAsync("/staff/help/nope");

        // The nearest not-found above the desk, inside the layouts above it: nothing of the desk reaches them.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal($"{Outer}/app/not-found.tsx", (string?)payload["page"]);
        Assert.Equal(["app", "app/staff"], Ids(payload));
        Assert.DoesNotContain("ada", body);
        Assert.Equal(HttpStatusCode.NotFound, document.StatusCode);
        Assert.Equal($"{Outer}/app/not-found.tsx", (string?)app.Renderer.LastPayload["page"]);
        Assert.Equal(0, desk.Calls);
        Assert.Equal(0, desk.SignOuts);
    }

    private static async Task<(HttpStatusCode Status, JsonNode Payload, string Body)> DataAsync(TestApp app, string url)
    {
        // From the origin, so an encoded segment stays as it was sent.
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(app.Client.BaseAddress!.GetLeftPart(UriPartial.Authority) + url));
        request.Headers.Add(NetjesHeaders.Data, "1");
        var response = await app.Client.SendAsync(request, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, JsonNode.Parse(body)!, body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnderAFallbackPolicy_AnUnknownUrlBelowAMountWhoseNotFoundAllowsAnonymousVisitors_IsItsNotFound(bool configured)
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/not-found.tsx", "app/unauthorized.tsx", "app/page.tsx"],
            prepareRoot: root => TestPagePackage.Install(root, ["layout.tsx", "not-found.tsx", "page.tsx"]),
            map: a =>
            {
                var mount = a.MapPagePackage(TestPagePackage.Descriptor, "/help");
                if (configured)
                {
                    mount.ConfigureNotFound(notFound => notFound.AllowAnonymous());
                }
            },
            services: s =>
            {
                Schemes(staff: false)(s);
                s.AddAuthorizationBuilder().SetFallbackPolicy(SiteUsers);
            },
            configureApp: a => a.UseAuthorization());

        var below = await DataAsync(app, "/help/nope");
        var document = await app.GetDocumentAsync("/help/nope/deeper");
        var outside = await DataAsync(app, "/nope");
        var page = await DataAsync(app, "/help");

        // Without conventions of its own, the not-found is covered by the fallback policy, as every unknown URL of the app.
        Assert.Equal(configured ? HttpStatusCode.NotFound : HttpStatusCode.Unauthorized, below.Status);
        if (configured)
        {
            Assert.Equal($"{Inner}/app/not-found.tsx", (string?)below.Payload["page"]);
        }

        Assert.Equal(configured ? HttpStatusCode.NotFound : HttpStatusCode.Unauthorized, document.StatusCode);
        // The rest of the app keeps its fallback policy, the package's pages included.
        Assert.Equal(HttpStatusCode.Unauthorized, outside.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, page.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnderAFallbackPolicy_ABackOfficesNotFound_NeverDropsItsStaff_AndNeverShowsAnonymousVisitorsTheDesk(bool staff)
    {
        var desk = new Desk();
        await using var app = await BackOfficeAsync(
            desk, staff, outer: office => office.ConfigureNotFound(notFound => notFound.AllowAnonymous()), fallbackPolicy: true);

        var inner = await DataAsync(app, "/staff/help/nope");
        var document = await app.GetDocumentAsync("/staff/help/nope");
        var documentPayload = app.Renderer.LastPayload;
        var outer = await DataAsync(app, "/staff/nope");
        var deskPage = await DataAsync(app, "/staff");

        Assert.Equal(HttpStatusCode.NotFound, inner.Status);
        Assert.Equal(HttpStatusCode.NotFound, document.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, outer.Status);
        Assert.Equal($"{Outer}/app/not-found.tsx", (string?)outer.Payload["page"]);
        Assert.Equal(["app", "app/staff"], Ids(outer.Payload));
        Assert.Equal(0, desk.SignOuts);
        if (staff)
        {
            // Inside the desk, for the user the back office signs in, as on its pages.
            Assert.Equal($"{Inner}/app/not-found.tsx", (string?)inner.Payload["page"]);
            Assert.Equal("ada", DeskStaff(inner.Payload));
            Assert.Equal("ada", DeskStaff(documentPayload));
            Assert.Equal(HttpStatusCode.OK, deskPage.Status);
            Assert.Equal(3, desk.Calls);
        }
        else
        {
            // The nearest not-found above the desk, which neither runs nor reaches the visitor.
            Assert.Equal($"{Outer}/app/not-found.tsx", (string?)inner.Payload["page"]);
            Assert.Equal(["app", "app/staff"], Ids(inner.Payload));
            Assert.DoesNotContain("ada", inner.Body);
            Assert.Equal($"{Outer}/app/not-found.tsx", (string?)documentPayload["page"]);
            Assert.Equal(HttpStatusCode.Unauthorized, deskPage.Status);
            Assert.DoesNotContain("ada", deskPage.Body);
            Assert.Equal(0, desk.Calls);
        }
    }

    [Theory]
    [InlineData("/help/nope", HttpStatusCode.Unauthorized)]
    [InlineData("/help/nope/deeper", HttpStatusCode.Unauthorized)]
    [InlineData("/HELP/Nope", HttpStatusCode.Unauthorized)]
    [InlineData("/help", HttpStatusCode.Unauthorized)]
    [InlineData("/help%2Fnope", HttpStatusCode.NotFound)]
    [InlineData("/helpx/nope", HttpStatusCode.NotFound)]
    [InlineData("/nope", HttpStatusCode.NotFound)]
    public async Task TheNotFoundsConventions_ReachTheUnknownUrlsBelowTheMount_AndNoOthers(string url, HttpStatusCode status)
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/not-found.tsx", "app/unauthorized.tsx", "app/page.tsx"],
            prepareRoot: root => TestPagePackage.Install(root, ["layout.tsx", "not-found.tsx"]),
            map: a => a.MapPagePackage(TestPagePackage.Descriptor, "/help").ConfigureNotFound(notFound => notFound.RequireAuthorization(Staff)),
            services: Schemes(staff: false),
            configureApp: a => a.UseAuthorization());

        var (actual, payload, _) = await DataAsync(app, url);

        Assert.Equal(status, actual);
        if (status == HttpStatusCode.NotFound)
        {
            Assert.Equal("app/not-found.tsx", (string?)payload["page"]);
        }
    }

    [Theory]
    [InlineData(false, "/a/b/nope", HttpStatusCode.Unauthorized)]
    [InlineData(false, "/a/nope", HttpStatusCode.Unauthorized)]
    [InlineData(true, "/a/b/nope", HttpStatusCode.NotFound)]
    [InlineData(true, "/a/nope", HttpStatusCode.Unauthorized)]
    public async Task AMountInsideAnother_TakesTheOuterNotFoundsConventions_UnlessItHasItsOwn(bool own, string url, HttpStatusCode status)
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/not-found.tsx", "app/unauthorized.tsx", "app/page.tsx"],
            prepareRoot: root =>
            {
                TestPagePackage.Install(root, ["layout.tsx", "not-found.tsx", "page.tsx"], name: Outer);
                TestPagePackage.Install(root, ["layout.tsx", "not-found.tsx", "page.tsx"]);
            },
            map: a =>
            {
                a.MapPagePackage(TestPagePackage.Other, "/a").ConfigureNotFound(notFound => notFound.RequireAuthorization(Staff));
                var inner = a.MapPagePackage(TestPagePackage.Descriptor, "/a/(g)/b");
                if (own)
                {
                    inner.ConfigureNotFound(notFound => notFound.AllowAnonymous());
                }
            },
            services: Schemes(staff: false),
            configureApp: a => a.UseAuthorization());

        var (actual, payload, _) = await DataAsync(app, url);

        Assert.Equal(status, actual);
        if (status == HttpStatusCode.NotFound)
        {
            Assert.Equal($"{Inner}/app/not-found.tsx", (string?)payload["page"]);
        }
    }

    [Fact]
    public async Task AMountsNotFound_IsAnEndpointOfItsOwn_LastInOrder_WithItsConventionsAlone()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/not-found.tsx", "app/page.tsx"],
            prepareRoot: root => TestPagePackage.Install(root, ["layout.tsx", "not-found.tsx", "page.tsx"]),
            map: a => a.MapPagePackage(TestPagePackage.Descriptor, "/[tenant]/(desk)/help")
                .RequireAuthorization(Staff)
                .ConfigureNotFound(notFound => notFound.AllowAnonymous())
                .MapLayoutLoader("/", (string tenant) => Page.Props(new { Tenant = tenant })),
            services: Schemes(staff: false),
            configureApp: a => a.UseAuthorization());

        var endpoints = app.App.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<PageEndpointMetadata>() is { Pattern: null })
            .ToList();
        var (status, payload, _) = await DataAsync(app, "/acme/help/nope");

        Assert.Equal(["/{tenant}/help/{**not-found:nonfile}", "{**path:nonfile}"], endpoints.Select(e => e.RoutePattern.RawText).Order(StringComparer.Ordinal));
        Assert.All(endpoints, e => Assert.Equal(int.MaxValue, e.Order));
        var mounts = endpoints.Single(e => e.RoutePattern.RawText != "{**path:nonfile}");
        Assert.Equal("Page not found", mounts.DisplayName);
        Assert.NotNull(mounts.Metadata.GetMetadata<IAllowAnonymous>());
        // The mount's own conventions, which would challenge every unknown URL below it, do not reach its not-found.
        Assert.Empty(mounts.Metadata.GetOrderedMetadata<IAuthorizeData>());
        Assert.Empty(mounts.Metadata.GetOrderedMetadata<AuthorizationPolicy>());
        // Nothing to warn about: it ties with no page, and no route warning names it.
        Assert.Empty(app.App.Services.GetRequiredService<RouteDiagnostics>().Check());
        // A 404, not the mount's challenge: the mount refuses the visitor its folder, so the not-found above it renders,
        // with the prefix's value routed as the package's own pages route it.
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("app/not-found.tsx", (string?)payload["page"]);
        Assert.Equal("/acme/help", (string?)payload["mounts"]![Inner]);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/(site)")]
    public async Task ConfigureNotFound_OnAMountAtTheRootUrl_StopsTheApp(string prefix)
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(
            ["app/layout.tsx", "app/not-found.tsx"],
            prepareRoot: root => TestPagePackage.Install(root, ["page.tsx"]),
            map: a => a.MapPagePackage(TestPagePackage.Descriptor, prefix).ConfigureNotFound(notFound => notFound.AllowAnonymous())));

        Assert.Contains("root URL", failure.Message);
        Assert.Contains(Inner, failure.Message);
    }

    /// <summary>
    /// A mount whose URL is values alone has the unknown URLs below the app's other mounts below it too: its not-found would
    /// answer <c>/staff/nope</c> under the conventions given for <c>/[tenant]</c>.
    /// </summary>
    [Theory]
    [InlineData("/[tenant]")]
    [InlineData("/(site)/[tenant]")]
    [InlineData("/[tenant]/(desk)/[section]")]
    public async Task ConfigureNotFound_OnAMountWhoseUrlHasNoLiteralSegment_StopsTheApp(string prefix)
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(
            ["app/layout.tsx", "app/not-found.tsx"],
            prepareRoot: root => TestPagePackage.Install(root, ["page.tsx"]),
            map: a => a.MapPagePackage(TestPagePackage.Descriptor, prefix).ConfigureNotFound(notFound => notFound.AllowAnonymous())));

        Assert.Contains("no literal segment", failure.Message);
        Assert.Contains(Inner, failure.Message);
    }

    [Fact]
    public async Task ConfigureNotFound_AfterMapPages_StopsTheApp()
    {
        PagePackageBuilder? mount = null;
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(
            ["app/layout.tsx", "app/not-found.tsx"],
            prepareRoot: root => TestPagePackage.Install(root, ["not-found.tsx", "page.tsx"]),
            map: a => mount = a.MapPagePackage(TestPagePackage.Descriptor, "/help"),
            pages: _ => mount!.ConfigureNotFound(notFound => notFound.AllowAnonymous())));

        Assert.Contains("after MapPages()", failure.Message);
    }

    [Fact]
    public async Task ConfigureNotFound_OnTwoMountsAtOneUrl_StopsTheApp()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(
            ["app/layout.tsx", "app/not-found.tsx"],
            prepareRoot: root =>
            {
                TestPagePackage.Install(root, ["not-found.tsx", "page.tsx"]);
                TestPagePackage.Install(root, ["not-found.tsx"], name: Outer);
            },
            map: a =>
            {
                a.MapPagePackage(TestPagePackage.Descriptor, "/(a)/help").ConfigureNotFound(notFound => notFound.AllowAnonymous());
                a.MapPagePackage(TestPagePackage.Other, "/(b)/help").ConfigureNotFound(notFound => notFound.AllowAnonymous());
            }));

        Assert.Contains(Inner, failure.Message);
        Assert.Contains(Outer, failure.Message);
    }
}
