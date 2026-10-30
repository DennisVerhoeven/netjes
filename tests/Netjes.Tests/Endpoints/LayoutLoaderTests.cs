using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Netjes.Tests.Endpoints;

/// <summary>C# loaders for layouts: segment props, binding, boundaries, conventions and partial navigation.</summary>
public class LayoutLoaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Teams =
    [
        "app/layout.tsx",
        "app/not-found.tsx",
        "app/error.tsx",
        "app/[team]/layout.tsx",
        "app/[team]/not-found.tsx",
        "app/[team]/error.tsx",
        "app/[team]/page.tsx",
        "app/[team]/settings/page.tsx",
    ];

    private static async Task<JsonNode> DataAsync(TestApp app, string url, string? segments = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(NetjesHeaders.Data, "1");
        if (segments is not null) request.Headers.Add(NetjesHeaders.Segments, segments);
        var response = await app.Client.SendAsync(request, Ct);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
    }

    [Fact]
    public async Task LayoutLoader_BindsTheSegmentParamsAndServices_AndItsPropsReachTheSegment()
    {
        await using var app = await TestApp.StartAsync(
            Teams,
            map: a => a.MapLayoutLoader("/[team]", (string team, Greeter greeter) => Page.Props(new { Name = greeter.Greet(team) })),
            services: s => s.AddSingleton<Greeter>());

        var payload = await app.GetDataJsonAsync("/core/settings");

        var segment = payload["segments"]![1]!;
        Assert.Equal("app/[team]", (string?)segment["id"]);
        Assert.Equal("Hello, core", (string?)segment["props"]!["name"]);
        Assert.False(string.IsNullOrEmpty((string?)segment["key"]));
        Assert.Null(payload["segments"]![0]!["props"]);
    }

    [Fact]
    public async Task LayoutLoader_ArrayParameters_BindFromTheQueryString_AsOnPageLoaders()
    {
        await using var app = await TestApp.StartAsync(
            Teams,
            map: a => a.MapLayoutLoader("/[team]", (string team, string[] tags, int[] years) => Page.Props(new { Tags = tags, Years = years })));

        var tagged = await app.GetDataAsync("/core/settings?tags=a&tags=b&years=2025");
        var untagged = await app.GetDataAsync("/core/settings");

        Assert.Equal(HttpStatusCode.OK, tagged.StatusCode);
        Assert.Equal(HttpStatusCode.OK, untagged.StatusCode);
        var props = JsonNode.Parse(await tagged.Content.ReadAsStringAsync(Ct))!["segments"]![1]!["props"]!;
        Assert.Equal(["a", "b"], props["tags"]!.AsArray().Select(t => (string?)t));
        Assert.Equal([2025], props["years"]!.AsArray().Select(y => (int?)y));
        Assert.Empty(JsonNode.Parse(await untagged.Content.ReadAsStringAsync(Ct))!["segments"]![1]!["props"]!["tags"]!.AsArray());
    }

    [Fact]
    public async Task LayoutLoader_ArrayParameter_OnAPageActionsRender_BindsFromTheQueryStringNotTheForm()
    {
        await using var app = await TestApp.StartAsync(
            Teams,
            map: a =>
            {
                a.MapLayoutLoader("/[team]", (string team, string[] tags) => Page.Props(new { Tags = tags }));
                a.MapPageAction("/[team]/settings", () => Page.Props(new { Saved = false }).WithStatus(422)).DisableAntiforgery();
            });

        var response = await app.Client.PostAsync("/core/settings?tags=a", new FormUrlEncodedContent([new("tags", "from-the-form")]), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(["a"], app.Renderer.LastPayload["segments"]![1]!["props"]!["tags"]!.AsArray().Select(t => (string?)t));
    }

    [Fact]
    public async Task LayoutLoader_ANullableArrayParameter_BindsFromTheQueryString_NotAsAMissingBody()
    {
        await using var app = await TestApp.StartAsync(
            Teams,
            map: a => a.MapLayoutLoader("/[team]", (string team, string[]? tags) => Page.Props(new { Tags = tags })));

        var props = (await app.GetDataJsonAsync("/core/settings?tags=a&tags=b"))["segments"]![1]!["props"]!;

        Assert.Equal("""["a","b"]""", props["tags"]?.ToJsonString());
    }

    public sealed class TeamFilter
    {
        public string? Q { get; set; }
    }

    [Fact]
    public async Task LayoutLoader_ANullableComplexParameterWithoutABindingAttribute_FailsAtStartupNamingItAndTheFixes()
    {
        // A navigation has no body to read it from, and the loader is built without an inferred one: minimal APIs
        // refused it on every request of the section.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(
            Teams,
            map: a => a.MapLayoutLoader("/[team]", (string team, TeamFilter? filter) => Page.Props(new { filter?.Q }))));

        Assert.Contains("The layout loader of app/[team]/layout.tsx (/[team])", ex.Message);
        Assert.Contains("'filter'", ex.Message);
        Assert.Contains("of type TeamFilter", ex.Message);
        Assert.Contains("[AsParameters]", ex.Message);
        Assert.Contains("BindAsync", ex.Message);
        Assert.Contains("register TeamFilter as a service", ex.Message);
        Assert.DoesNotContain("'team'", ex.Message);
    }

    public static TheoryData<string, Action<WebApplication>, string> LayoutLoadersMinimalApisRefuse => new()
    {
        { "[FromQuery] on a type without TryParse", a => a.MapLayoutLoader("/[team]", (string team, [FromQuery] TeamFilter filter) => Page.Props()), "TryParse" },
        { "a nested [AsParameters]", a => a.MapLayoutLoader("/[team]", ([AsParameters] NestedQuery query) => Page.Props()), "Nested AsParameters" },
    };

    [Theory]
    [MemberData(nameof(LayoutLoadersMinimalApisRefuse))]
    public async Task LayoutLoader_AParameterMinimalApisRefuse_FailsAtStartupNamingTheLoaderAndWhy(string name, Action<WebApplication> map, string why)
    {
        // Refused when the section's first request built the loader, which failed every request of the section.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(Teams, map: map));

        Assert.Contains("The layout loader of app/[team]/layout.tsx (/[team])", ex.Message);
        Assert.True(ex.Message.Contains(why, StringComparison.OrdinalIgnoreCase), name);
    }

    public sealed record TeamQuery(string? Q);

    public sealed record NestedQuery(string? Sort, [AsParameters] TeamQuery Filter);

    public sealed class SelfBoundFilter
    {
        public string? Q { get; init; }

        public static ValueTask<SelfBoundFilter?> BindAsync(HttpContext http, ParameterInfo parameter) =>
            ValueTask.FromResult<SelfBoundFilter?>(new SelfBoundFilter { Q = http.Request.Query["q"] });
    }

    public static TheoryData<string, Action<WebApplication>> ComplexParametersThatSayWhereTheyComeFrom => new()
    {
        { "[AsParameters]", a => a.MapLayoutLoader("/[team]", (string team, [AsParameters] TeamQuery filter) => Page.Props(new { filter.Q })) },
        { "a type that binds itself", a => a.MapLayoutLoader("/[team]", (string team, SelfBoundFilter filter) => Page.Props(new { filter.Q })) },
    };

    [Theory]
    [MemberData(nameof(ComplexParametersThatSayWhereTheyComeFrom))]
    public async Task LayoutLoader_AComplexParameterThatSaysWhereItComesFrom_Binds_AlsoOnAPageActionsRender(string name, Action<WebApplication> map)
    {
        await using var app = await TestApp.StartAsync(
            Teams,
            map: a =>
            {
                map(a);
                a.MapPageAction("/[team]/settings", () => Page.Props(new { Saved = false }).WithStatus(422)).DisableAntiforgery();
            });

        var navigation = (await app.GetDataJsonAsync("/core/settings?q=x"))["segments"]![1]!["props"]!;
        var render = await app.Client.PostAsync("/core/settings?q=y", new FormUrlEncodedContent([new("q", "from-the-form")]), Ct);

        Assert.True((string?)navigation["q"] == "x", name);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, render.StatusCode);
        Assert.Equal("y", (string?)app.Renderer.LastPayload["segments"]![1]!["props"]!["q"]);
    }

    private static readonly string[] TeamProjects =
    [
        "app/layout.tsx",
        "app/error.tsx",
        "app/team/[team]/layout.tsx",
        "app/team/[team]/page.tsx",
        "app/team/[team]/projects/[id]/page.tsx",
    ];

    public sealed record ProjectRoute([FromRoute(Name = "id")] string? Project);

    public static TheoryData<string, Action<WebApplication>> RouteBindingsBelowTheLayout => new()
    {
        { "[FromRoute] by name", a => a.MapLayoutLoader("/team/[team]", (string team, [FromRoute(Name = "id")] string? project) => Page.Props(new { Project = project })) },
        { "[FromRoute] by the parameter's name", a => a.MapLayoutLoader("/team/[team]", ([FromRoute] string? id) => Page.Props(new { Project = id })) },
        { "[FromRoute] in another case", a => a.MapLayoutLoader("/team/[team]", ([FromRoute(Name = "ID")] string? project) => Page.Props(new { Project = project })) },
        { "an [AsParameters] member", a => a.MapLayoutLoader("/team/[team]", ([AsParameters] ProjectRoute route) => Page.Props(new { route.Project })) },
    };

    [Theory]
    [MemberData(nameof(RouteBindingsBelowTheLayout))]
    public async Task LayoutLoader_BindingARouteValueBelowItsUrl_FailsAtStartupNamingIt(string name, Action<WebApplication> map)
    {
        // Its key holds only the route values of its own URL: on /team/a/projects/1 then /team/a/projects/2 its data
        // would be reused. Minimal API binding refuses it on every request; this says so before the first.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(TeamProjects, map: map));

        Assert.Contains("app/team/[team]/layout.tsx", ex.Message);
        Assert.Contains("'id'", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/team/[team]", ex.Message);
        Assert.DoesNotContain("'team'", ex.Message);
        Assert.True(ex.Message.Contains("page's loader", StringComparison.Ordinal), name);
    }

    [Fact]
    public async Task LayoutLoader_FromRouteOfItsOwnRouteParameter_InAnyCase_BindsIt()
    {
        await using var app = await TestApp.StartAsync(
            TeamProjects,
            map: a => a.MapLayoutLoader("/team/[team]", ([FromRoute(Name = "TEAM")] string name, [FromRoute] string team) => Page.Props(new { Name = name, Team = team })));

        var props = (await app.GetDataJsonAsync("/team/core/projects/1"))["segments"]![1]!["props"]!;

        Assert.Equal("core", (string?)props["name"]);
        Assert.Equal("core", (string?)props["team"]);
    }

    [Fact]
    public async Task LayoutLoader_ByFolder_ResolvesRouteGroupsThatShareAUrl()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/(shop)/layout.tsx", "app/(shop)/cart/page.tsx"],
            map: a => a.MapLayoutLoader("app/(shop)", () => Page.Props(new { Items = 2 })));

        var payload = await app.GetDataJsonAsync("/cart");

        Assert.Equal(2, (int?)payload["segments"]![1]!["props"]!["items"]);
    }

    [Fact]
    public async Task LayoutLoader_AmbiguousUrl_FailsAtStartupNamingTheFolders()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(
            ["app/layout.tsx", "app/(shop)/layout.tsx", "app/(shop)/cart/page.tsx"],
            map: a => a.MapLayoutLoader("/", () => Page.Props())));

        Assert.Contains("app/(shop)", ex.Message);
        Assert.Contains("\"app\"", ex.Message);
    }

    [Fact]
    public async Task LayoutLoader_WithoutALayoutFile_FailsAtStartupNamingTheExpectedFile()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(
            ["app/page.tsx"], map: a => a.MapLayoutLoader("/dashboard", () => Page.Props())));

        Assert.Contains("app/dashboard/layout.tsx", ex.Message);
    }

    [Fact]
    public async Task LayoutLoader_NotFound_RendersTheParentsNotFoundInsideTheParentLayouts()
    {
        await using var app = await TestApp.StartAsync(
            Teams, map: a => a.MapLayoutLoader("/[team]", (string team) => team == "core" ? Page.Props() : Page.NotFound()));

        var response = await app.GetDataAsync("/ghost/settings");
        var payload = await app.GetDataJsonAsync("/ghost/settings");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // Not app/[team]/not-found.tsx: a layout's own boundary sits inside it.
        Assert.Equal("app/not-found.tsx", (string?)payload["page"]);
        Assert.Equal(["app"], payload["segments"]!.AsArray().Select(s => (string?)s!["id"]));
    }

    [Fact]
    public async Task LayoutLoader_Exception_RendersTheParentsErrorBoundary()
    {
        await using var app = await TestApp.StartAsync(
            Teams, map: a => a.MapLayoutLoader("/[team]", Func<PageResult> () => throw new InvalidOperationException("teams down")));

        var response = await app.GetDataAsync("/core");
        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("app/error.tsx", (string?)payload["page"]);
        Assert.Equal(["app"], payload["segments"]!.AsArray().Select(s => (string?)s!["id"]));
    }

    [Fact]
    public async Task LayoutLoader_Redirect_RedirectsThePage()
    {
        await using var app = await TestApp.StartAsync(
            Teams, map: a => a.MapLayoutLoader("/[team]", () => Page.Redirect("/login")));

        var response = await app.GetDocumentAsync("/core/settings");

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task BoundaryPages_GetThePropsOfTheLayoutsAroundThem()
    {
        await using var app = await TestApp.StartAsync(
            Teams,
            map: a =>
            {
                a.MapLayoutLoader("/[team]", (string team) => Page.Props(new { Team = team }));
                a.MapPageLoader("/[team]/settings", () => Page.NotFound());
            });

        var payload = await app.GetDataJsonAsync("/core/settings");

        Assert.Equal("app/[team]/not-found.tsx", (string?)payload["page"]);
        Assert.Equal("core", (string?)payload["segments"]![1]!["props"]!["team"]);
    }

    [Fact]
    public async Task LayoutLoaderConventions_ApplyToEveryPageBelowIt()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/admin/layout.tsx", "app/admin/page.tsx", "app/admin/users/page.tsx", "app/public/page.tsx"],
            map: a =>
            {
                a.MapLayoutLoader("/admin", () => Page.Props()).RequireAuthorization();
                a.MapPageLoader("/admin/users", () => Page.Props(new { Count = 1 }));
            },
            services: s => s.AddAuthorization().AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, NobodyHandler>("Test", null),
            configureApp: a => a.UseAuthorization());

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.GetDataAsync("/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.GetDataAsync("/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.GetDataAsync("/public")).StatusCode);
    }

    [Fact]
    public async Task ClientNavigation_SkipsLoadersOfSegmentsItAlreadyHas()
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(
            Teams, map: a => a.MapLayoutLoader("/[team]", (string team) => Page.Props(new { Team = team, Call = ++calls })));

        var first = await DataAsync(app, "/core");
        var key = (string)first["segments"]![1]!["key"]!;
        var sameTeam = await DataAsync(app, "/core/settings", segments: key);
        var otherTeam = await DataAsync(app, "/infra/settings", segments: key);

        Assert.Equal(2, calls);
        Assert.True((bool?)sameTeam["segments"]![1]!["reused"]);
        Assert.Null(sameTeam["segments"]![1]!["props"]);
        Assert.Equal(key, (string?)sameTeam["segments"]![1]!["key"]);
        Assert.Equal("infra", (string?)otherTeam["segments"]![1]!["props"]!["team"]);
        Assert.NotEqual(key, (string?)otherTeam["segments"]![1]!["key"]);
    }

    private static readonly string[] ProjectSections =
    [
        "app/layout.tsx",
        "app/error.tsx",
        "app/team/[team]/projects/[id]/layout.tsx",
        "app/team/[team]/projects/[id]/page.tsx",
    ];

    [Theory]
    [InlineData("/team/a/projects/2")]
    [InlineData("/team/a/projects/1%0A")]
    [InlineData("/team/a/projects/1%20")]
    [InlineData("/team/a/projects/%31%0A")]
    [InlineData("/team/A/projects/1")]
    public async Task AKeyForTheOpenProject_DoesNotSkipTheGuardOfAnother(string url)
    {
        // The guard is the loader of the layout whose URL has the project id, so the id is in its key.
        await using var app = await TestApp.StartAsync(
            ProjectSections,
            map: a =>
            {
                a.MapLayoutLoader("/team/[team]/projects/[id]", (string team, string id) =>
                    team == "a" && id == "1" ? Page.Props(new { Project = id }) : Page.Forbidden());
                a.MapPageLoader("/team/[team]/projects/[id]", (string id) => Page.Props(new { Plans = "plans of " + id }));
            });
        var key = (string)(await DataAsync(app, "/team/a/projects/1"))["segments"]![1]!["key"]!;
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(NetjesHeaders.Data, "1");
        request.Headers.Add(NetjesHeaders.Segments, key);

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("plans", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task AKeyIssuedToAnotherUser_DoesNotSkipTheGuardOnTheSameUrl()
    {
        await using var app = await TestApp.StartAsync(
            ProjectSections,
            map: a =>
            {
                a.MapLayoutLoader("/team/[team]/projects/[id]", (string id, ClaimsPrincipal user) =>
                    user.FindFirstValue(ClaimTypes.NameIdentifier) == "ada" ? Page.Props(new { Project = id }) : Page.Forbidden());
                a.MapPageLoader("/team/[team]/projects/[id]", (string id) => Page.Props(new { Plans = "plans of " + id }));
            },
            services: s => s.AddAuthorization().AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderUserHandler>("Test", null));
        var asAda = new HttpRequestMessage(HttpMethod.Get, "/team/a/projects/1");
        asAda.Headers.Add(NetjesHeaders.Data, "1");
        asAda.Headers.Add(HeaderUserHandler.Header, "ada");
        var adasKey = (string)JsonNode.Parse(await (await app.Client.SendAsync(asAda, Ct)).Content.ReadAsStringAsync(Ct))!["segments"]![1]!["key"]!;
        var asBob = new HttpRequestMessage(HttpMethod.Get, "/team/a/projects/1");
        asBob.Headers.Add(NetjesHeaders.Data, "1");
        asBob.Headers.Add(NetjesHeaders.Segments, adasKey);
        asBob.Headers.Add(HeaderUserHandler.Header, "bob");

        var response = await app.Client.SendAsync(asBob, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("plans", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task ASegmentsHeaderLongerThanAnyClientSends_ReusesNothing()
    {
        await using var app = await TestApp.StartAsync(
            Teams, map: a => a.MapLayoutLoader("/[team]", (string team) => Page.Props(new { Team = team })));
        var key = (string)(await DataAsync(app, "/core"))["segments"]![1]!["key"]!;

        var padded = await DataAsync(app, "/core/settings", segments: $"{key},{string.Join(',', Enumerable.Repeat(key, 200))}");

        Assert.Null(padded["segments"]![1]!["reused"]);
        Assert.Equal("core", (string?)padded["segments"]![1]!["props"]?["team"]);
    }

    [Fact]
    public async Task DocumentRequests_AlwaysRunEveryLoader()
    {
        var calls = 0;
        await using var app = await TestApp.StartAsync(
            Teams, map: a => a.MapLayoutLoader("/[team]", () => Page.Props(new { Call = ++calls })));
        var key = (string)(await DataAsync(app, "/core"))["segments"]![1]!["key"]!;

        var request = new HttpRequestMessage(HttpMethod.Get, "/core/settings");
        request.Headers.Add(NetjesHeaders.Segments, key);
        await app.Client.SendAsync(request, Ct);

        Assert.Equal(2, calls);
        Assert.Equal(2, (int?)app.Renderer.LastPayload["segments"]![1]!["props"]!["call"]);
    }

    [Fact]
    public async Task PageResponses_VaryOnTheSegmentsHeader()
    {
        await using var app = await TestApp.StartAsync(Teams);

        var response = await app.GetDataAsync("/core");

        Assert.Contains(NetjesHeaders.Segments, response.Headers.Vary);
    }

    [Fact]
    public async Task LayoutLoaderQueries_AreSeededWithThePages()
    {
        await using var app = await TestApp.StartAsync(
            Teams,
            map: a =>
            {
                a.MapLayoutLoader("/[team]", (string team) => Page.Props().WithQuery(["stats", team], 3));
                a.MapPageLoader("/[team]/settings", () => Page.Props().WithQuery(["settings"], "x"));
            });

        var payload = await app.GetDataJsonAsync("/core/settings");

        Assert.Equal(
            ["[\"settings\"]", "[\"stats\",\"core\"]"],
            payload["queries"]!.AsArray().Select(q => q!["key"]!.ToJsonString()).Order(StringComparer.Ordinal));
    }

    private sealed class NobodyHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }

    /// <summary>Signs in the user the request names in a header, without roles.</summary>
    private sealed class HeaderUserHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Header = "X-Test-User";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            Request.Headers[Header].ToString() is { Length: > 0 } id
                ? AuthenticateResult.Success(new AuthenticationTicket(
                    new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "Test")), "Test"))
                : AuthenticateResult.NoResult());
    }
}

internal sealed class Greeter
{
    public string Greet(string name) => $"Hello, {name}";
}
