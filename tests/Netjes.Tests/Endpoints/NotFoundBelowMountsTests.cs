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

    /// <summary>
    /// A back office mounted at <c>/staff</c>, whose desk (<c>app/(desk)</c>) has a layout loader behind
    /// <see cref="Staff"/> that ends a sign-in it finds no user for, as a back office does for a user that is gone; and a
    /// second package mounted inside the desk at <c>/staff/(desk)/help</c>, behind the same policy.
    /// </summary>
    private static Task<TestApp> BackOfficeAsync(Desk desk, bool staff, Action<PagePackageBuilder>? inner = null) =>
        TestApp.StartAsync(
            ["app/layout.tsx", "app/not-found.tsx", "app/page.tsx"],
            prepareRoot: root =>
            {
                TestPagePackage.Install(root, ["not-found.tsx", "(desk)/layout.tsx", "(desk)/page.tsx"], name: Outer);
                TestPagePackage.Install(root, ["layout.tsx", "not-found.tsx", "page.tsx"]);
            },
            map: a =>
            {
                a.MapPagePackage(TestPagePackage.Other, "/staff").MapLayoutLoader("app/(desk)", (HttpContext http) =>
                {
                    if (http.User.Identity?.IsAuthenticated != true)
                    {
                        desk.SignOuts++;
                        return Page.Redirect("/staff/sign-in");
                    }

                    desk.Calls++;
                    return Page.Props(new { Staff = http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value });
                }).RequireAuthorization(Staff);
                var mount = a.MapPagePackage(TestPagePackage.Descriptor, "/staff/(desk)/help").RequireAuthorization(Staff);
                inner?.Invoke(mount);
            },
            services: Schemes(staff),
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
}
