using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Netjes.Tests.Endpoints;

public class PageEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Get_PageWithoutLoader_ServerRendersItWithEmptyProps()
    {
        await using var app = await TestApp.StartAsync(["app/layout.tsx", "app/about/page.tsx"]);

        var response = await app.GetDocumentAsync("/about");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.MediaType()?.MediaType);
        Assert.Equal("<!DOCTYPE html><html><body>rendered</body></html>", await response.Content.ReadAsStringAsync(Ct));
        var payload = app.Renderer.LastPayload;
        Assert.Equal("page", (string?)payload["kind"]);
        Assert.Equal("/about", (string?)payload["route"]);
        Assert.Equal("app/about/page.tsx", (string?)payload["page"]);
        Assert.Equal(["app/layout.tsx"], payload["segments"]!.AsArray().Select(n => (string?)n!["layout"]));
        Assert.Empty(payload["props"]!.AsObject());
        Assert.Equal(200, (int?)payload["status"]);
        Assert.Equal("/about", (string?)payload["url"]);
    }

    [Fact]
    public async Task Get_DocumentRequest_PayloadCarriesDocumentAssetsAndVersion()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"], configure: o => o.ViteDevPort = 5999);

        await app.GetDocumentAsync("/?q=1");

        var payload = app.Renderer.LastPayload;
        var scripts = payload["document"]!["assets"]!["scripts"]!.AsArray().Select(n => (string?)n).ToList();
        Assert.Equal(["http://localhost:5999/@vite/client", "http://localhost:5999/@netjes/client-entry"], scripts);
        Assert.Equal("en", (string?)payload["document"]!["lang"]);
        Assert.Equal("development", (string?)payload["version"]);
        Assert.Equal("/?q=1", (string?)payload["url"]);
    }

    [Fact]
    public async Task Get_DataRequest_ReturnsJsonPayloadWithoutServerRendering()
    {
        await using var app = await TestApp.StartAsync(["app/about/page.tsx"]);

        var payload = await app.GetDataJsonAsync("/about");

        Assert.Equal("app/about/page.tsx", (string?)payload["page"]);
        Assert.Null(payload["document"]);
        Assert.Empty(app.Renderer.Payloads);
    }

    [Fact]
    public async Task Get_AnyPage_VariesOnTheDataHeader()
    {
        await using var app = await TestApp.StartAsync(["app/about/page.tsx"]);

        var document = await app.GetDocumentAsync("/about");
        var data = await app.GetDataAsync("/about");

        Assert.Contains(NetjesHeaders.Data, document.Headers.Vary);
        Assert.Contains(NetjesHeaders.Data, data.Headers.Vary);
    }

    [Fact]
    public async Task MapPageLoader_BindsRouteValuesAndServicesIntoProps()
    {
        await using var app = await TestApp.StartAsync(
            ["app/blog/[slug]/page.tsx"],
            map: a => a.MapPageLoader("/blog/[slug]", (string slug, Greeter greeter) =>
                Page.Props(new { Title = greeter.Greet(slug) })),
            services: s => s.AddSingleton<Greeter>());

        var payload = await app.GetDataJsonAsync("/blog/hello-world");

        Assert.Equal("Hello, hello-world", (string?)payload["props"]!["title"]);
        Assert.Equal("hello-world", (string?)payload["params"]!["slug"]);
        Assert.Equal("/blog/[slug]", (string?)payload["route"]);
    }

    [Fact]
    public async Task MapPageLoader_AsyncLoader_IsAwaited()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", async () =>
            {
                await Task.Yield();
                return Page.Props(new { Count = 3 });
            }));

        var payload = await app.GetDataJsonAsync("/");

        Assert.Equal(3, (int?)payload["props"]!["count"]);
    }

    [Fact]
    public async Task MapPageLoader_PatternWithoutPageFile_FailsAtStartupNamingTheExpectedFile()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/blog/[slug]", () => Page.Props())));

        Assert.Contains("/blog/[slug]", ex.Message);
        Assert.Contains("app/blog/[slug]/page.tsx", ex.Message);
    }

    public sealed record Filter(string? Q);

    [Fact]
    public async Task MapPageLoader_AComplexParameterThatBindsFromNowhere_FailsAtStartupNamingItAndTheFixes()
    {
        // Not a service, no binding attribute, no BindAsync: minimal APIs take it for a request body, which a GET
        // endpoint refuses. Building the endpoint failed on the first request, and every other endpoint with it.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(
            ["app/about/page.tsx", "app/[team]/page.tsx"],
            map: a => a.MapPageLoader("/[team]", (string team, Filter? filter) => Page.Props(new { Team = team, filter?.Q }))));

        Assert.Contains("app/[team]/page.tsx", ex.Message);
        Assert.Contains("(/[team])", ex.Message);
        Assert.Contains("'filter'", ex.Message);
        Assert.Contains("of type Filter", ex.Message);
        Assert.Contains("[AsParameters]", ex.Message);
        Assert.Contains("BindAsync", ex.Message);
        Assert.Contains("register Filter as a service", ex.Message);
        Assert.DoesNotContain("'team'", ex.Message);
    }

    public interface IPostStore;

    public interface IClock;

    [Fact]
    public async Task MapPageLoader_SeveralParametersThatBindFromNowhere_FailsAtStartupNamingEach()
    {
        // Services that were never registered: minimal APIs see two request bodies, and suggest [FromBody].
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", (IPostStore posts, string? q, IClock clock) => Page.Props())));

        Assert.Contains("The loader of app/page.tsx (/) cannot bind parameter 'posts' of type IPostStore", ex.Message);
        Assert.Contains("Register IPostStore as a service", ex.Message);
        Assert.Contains("Minimal APIs would read 'clock' of type IClock from the request body too.", ex.Message);
        Assert.DoesNotContain("'q'", ex.Message);
        Assert.DoesNotContain("[FromBody]", ex.Message);
    }

    public abstract class PostStoreBase;

    public record struct Point(int X, int Y);

    public sealed record IdsQuery(string? Q, List<int> Ids);

    public static TheoryData<string, Action<WebApplication>, string[], string[]> ParametersThatBindFromNowhere => new()
    {
        {
            "an interface", a => a.MapPageLoader("/", (IPostStore posts) => Page.Props()),
            ["of type IPostStore:", "Register IPostStore as a service, or give IPostStore a static BindAsync method"], ["[AsParameters]"]
        },
        {
            "an abstract class", a => a.MapPageLoader("/", (PostStoreBase posts) => Page.Props()),
            ["of type PostStoreBase:", "Register PostStoreBase as a service"], ["[AsParameters]"]
        },
        {
            "a struct", a => a.MapPageLoader("/", (Point? point) => Page.Props()),
            ["of type Point?:", "[AsParameters] to bind the properties of Point", "give Point a static BindAsync method"], ["as a service"]
        },
        {
            "a list of what an array binds", a => a.MapPageLoader("/", (List<int> ids) => Page.Props()),
            ["of type List<int>:", "Take int[] instead"], ["[AsParameters]", "BindAsync", "as a service"]
        },
        {
            "a collection interface", a => a.MapPageLoader("/", (IReadOnlyList<string> tags) => Page.Props()),
            ["of type IReadOnlyList<string>:", "Take string[] instead"], ["[AsParameters]", "BindAsync", "as a service"]
        },
        {
            "an array of a type that does not bind from a string", a => a.MapPageLoader("/", (Filter[] filters) => Page.Props()),
            ["of type Filter[]:", "give Filter a static TryParse method."], ["[AsParameters]", "BindAsync", "as a service"]
        },
        {
            "a list of a type that does not bind from a string", a => a.MapPageLoader("/", (List<Filter> filters) => Page.Props()),
            ["of type List<Filter>:", "give Filter a static TryParse method and take Filter[] instead"], ["[AsParameters]", "BindAsync", "as a service"]
        },
        {
            "another collection", a => a.MapPageLoader("/", (Dictionary<string, string> tags) => Page.Props()),
            ["of type Dictionary<string, string>:", "a type of your own that has a static BindAsync method"], ["[AsParameters]", "as a service"]
        },
        {
            "a type of .NET", a => a.MapPageLoader("/", (object value) => Page.Props()),
            ["of type object:", "Register object as a service, or take a type of your own that has a static BindAsync method instead"], ["[AsParameters]"]
        },
        {
            "a member of an [AsParameters] type", a => a.MapPageLoader("/", ([AsParameters] IdsQuery query) => Page.Props()),
            ["'Ids' of type List<int>, a member of its [AsParameters] type IdsQuery:", "Take int[] instead"], ["BindAsync", "as a service"]
        },
    };

    [Theory]
    [MemberData(nameof(ParametersThatBindFromNowhere))]
    public async Task MapPageLoader_AParameterThatBindsFromNowhere_OffersTheFixesThatFitItsType(
        string name, Action<WebApplication> map, string[] says, string[] doesNotSay)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(["app/page.tsx"], map: map));

        Assert.All(says, text => Assert.True(ex.Message.Contains(text, StringComparison.Ordinal), $"{name}: {ex.Message}"));
        Assert.All(doesNotSay, text => Assert.False(ex.Message.Contains(text, StringComparison.Ordinal), $"{name}: {ex.Message}"));
    }

    public static TheoryData<string, Action<WebApplication>, string> LoadersMinimalApisRefuse => new()
    {
        { "[FromRoute] naming no route parameter", a => a.MapPageLoader("/[team]", ([FromRoute] string id) => Page.Props()), "'id' is not a route parameter" },
        { "[FromQuery] on a type without TryParse", a => a.MapPageLoader("/[team]", (string team, [FromQuery] Filter filter) => Page.Props()), "TryParse" },
    };

    [Theory]
    [MemberData(nameof(LoadersMinimalApisRefuse))]
    public async Task MapPageLoader_AParameterMinimalApisRefuse_FailsAtStartupNamingTheLoaderAndWhy(string name, Action<WebApplication> map, string why)
    {
        // Refused when the endpoints were first built, which failed every request of the app.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(["app/about/page.tsx", "app/[team]/page.tsx"], map: map));

        Assert.Contains("The loader of app/[team]/page.tsx (/[team])", ex.Message);
        Assert.True(ex.Message.Contains(why, StringComparison.Ordinal), name);
    }

    public static TheoryData<string, Action<WebApplication>, string> PageActionsMinimalApisRefuse => new()
    {
        { "two inferred bodies", a => a.MapPageAction("/[team]", (Filter one, Filter two) => Page.Props()), "Failure to infer" },
        { "a form and a JSON body", a => a.MapPageAction("/[team]", ([FromForm] string q, Filter filter) => Page.Props()), "both form and JSON body" },
        { "[FromRoute] naming no route parameter", a => a.MapPageAction("/[team]", ([FromRoute] string id) => Page.Props()), "'id' is not a route parameter" },
    };

    [Theory]
    [MemberData(nameof(PageActionsMinimalApisRefuse))]
    public async Task MapPageAction_AParameterMinimalApisRefuse_FailsAtStartupNamingTheActionAndWhy(string name, Action<WebApplication> map, string why)
    {
        // Refused when the endpoints were first built, which failed every request of the app.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(["app/about/page.tsx", "app/[team]/page.tsx"], map: map));

        Assert.Contains("The action of app/[team]/page.tsx (/[team]) cannot bind its parameters", ex.Message);
        Assert.True(ex.Message.Contains(why, StringComparison.Ordinal), name);
    }

    public sealed record Tenancy(string Tenant, string Again, string Region);

    [Fact]
    public async Task MapPageLoader_InRouteGroupsWithParameters_BindsTheirRouteValues()
    {
        // ASP.NET Core puts the groups' prefixes before the page's pattern: their parameters are route values of the page.
        await using var app = await TestApp.StartAsync(
            ["app/projects/page.tsx"],
            map: a => a.MapGroup("/{tenant}").MapGroup("/{region}").MapPageLoader("/projects",
                ([FromRoute] string tenant, [FromRoute(Name = "TENANT")] string again, string region) => Page.Props(new Tenancy(tenant, again, region))));

        var props = (await app.GetDataJsonAsync("/acme/eu/projects"))["props"]!;

        Assert.Equal("acme", (string?)props["tenant"]);
        Assert.Equal("acme", (string?)props["again"]);
        Assert.Equal("eu", (string?)props["region"]);
    }

    [Fact]
    public async Task MapPageAction_InARouteGroupWithAParameter_StartsBindingItWithFromRoute()
    {
        await using var app = await TestApp.StartAsync(
            ["app/projects/page.tsx"],
            map: a => a.MapGroup("/{tenant}").MapPageAction("/projects", ([FromRoute] string tenant) => Page.Redirect($"/{tenant}/projects")));

        Assert.Contains(app.App.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints,
            e => e.DisplayName == "Page action /projects");
    }

    [Fact]
    public async Task MapPageLoader_InARouteGroup_AFromRouteNamingNoRouteParameter_FailsAtStartup()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(
            ["app/projects/page.tsx"],
            map: a => a.MapGroup("/{tenant}").MapPageLoader("/projects", ([FromRoute] string id) => Page.Props())));

        Assert.Contains("The loader of app/projects/page.tsx (/projects)", ex.Message);
        Assert.Contains("'id' is not a route parameter", ex.Message);
    }

    [Fact]
    public async Task CatchAllParameters_AreArraysOfSegments()
    {
        await using var app = await TestApp.StartAsync(["app/docs/[...path]/page.tsx", "app/shop/[[...filters]]/page.tsx"]);

        var docs = await app.GetDataJsonAsync("/docs/getting-started/install");
        var shop = await app.GetDataJsonAsync("/shop");

        Assert.Equal(["getting-started", "install"], docs["params"]!["path"]!.AsArray().Select(n => (string?)n));
        Assert.Empty(shop["params"]!["filters"]!.AsArray());
    }

    [Fact]
    public async Task CatchAll_WithoutAnySegment_DoesNotMatch()
    {
        await using var app = await TestApp.StartAsync(["app/docs/[...path]/page.tsx"]);

        var response = await app.GetDataAsync("/docs");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StaticPage_WinsOverDynamicPageWithLoader()
    {
        await using var app = await TestApp.StartAsync(
            ["app/blog/new/page.tsx", "app/blog/[slug]/page.tsx"],
            map: a => a.MapPageLoader("/blog/[slug]", (string slug) => Page.Props(new { slug })));

        var payload = await app.GetDataJsonAsync("/blog/new");

        Assert.Equal("app/blog/new/page.tsx", (string?)payload["page"]);
    }

    [Fact]
    public async Task Loader_ReturningNotFound_RendersThe404PageWithItsLayouts()
    {
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/not-found.tsx", "app/blog/[slug]/page.tsx"],
            map: a => a.MapPageLoader("/blog/[slug]", (string slug) => Page.NotFound()));

        var response = await app.GetDocumentAsync("/blog/missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var payload = app.Renderer.LastPayload;
        Assert.Equal("app/not-found.tsx", (string?)payload["page"]);
        Assert.Equal(["app/layout.tsx"], payload["segments"]!.AsArray().Select(n => (string?)n!["layout"]));
        Assert.Equal(404, (int?)payload["status"]);
    }

    [Fact]
    public async Task UnknownUrl_WithoutCustom404Page_RendersBuiltInNotFound()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"]);

        var response = await app.GetDataAsync("/nope");
        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null((string?)payload["page"]);
        Assert.Equal(404, (int?)payload["status"]);
    }

    [Fact]
    public async Task UnknownFileUrl_IsLeftToStaticFilesInsteadOfRendering404Page()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"]);

        var response = await app.GetDocumentAsync("/missing.png");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(app.Renderer.Payloads);
    }

    [Fact]
    public async Task PublicFiles_AreServedInDevelopment()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            prepareRoot: root =>
            {
                Directory.CreateDirectory(Path.Combine(root, "public"));
                File.WriteAllText(Path.Combine(root, "public", "robots.txt"), "User-agent: *");
            });

        var response = await app.GetDocumentAsync("/robots.txt");

        Assert.Equal("User-agent: *", await response.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData(false, HttpStatusCode.TemporaryRedirect)]
    [InlineData(true, HttpStatusCode.PermanentRedirect)]
    public async Task Redirect_DocumentRequest_SendsHttpRedirect(bool permanent, HttpStatusCode expected)
    {
        await using var app = await TestApp.StartAsync(
            ["app/old/page.tsx"],
            map: a => a.MapPageLoader("/old", () => Page.Redirect("/new", permanent)));

        var response = await app.GetDocumentAsync("/old");

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("/new", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Redirect_DataRequest_ReturnsRedirectPayloadForTheClientRouter()
    {
        await using var app = await TestApp.StartAsync(
            ["app/old/page.tsx"],
            map: a => a.MapPageLoader("/old", () => Page.Redirect("/new")));

        var payload = await app.GetDataJsonAsync("/old");

        Assert.Equal("redirect", (string?)payload["kind"]);
        Assert.Equal("/new", (string?)payload["location"]);
    }

    [Fact]
    public async Task PageAction_RedirectAfterPost_UsesSeeOther()
    {
        await using var app = await TestApp.StartAsync(
            ["app/contact/page.tsx"],
            map: a => a.MapPageAction("/contact", ([FromForm] string name) => Page.Redirect("/thanks")).DisableAntiforgery());

        var response = await app.Client.PostAsync(
            "/contact", new FormUrlEncodedContent([new("name", "Ada")]), Ct);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/thanks", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task PageAction_CanReRenderThePageWithPropsAndStatus()
    {
        await using var app = await TestApp.StartAsync(
            ["app/contact/page.tsx"],
            map: a => a.MapPageAction("/contact", ([FromForm] string? name) =>
                Page.Props(new { Errors = new { Name = "Name is required" } }).WithStatus(422)).DisableAntiforgery());

        var response = await app.Client.PostAsync(
            "/contact", new FormUrlEncodedContent([new("name", "")]), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var payload = app.Renderer.LastPayload;
        Assert.Equal("app/contact/page.tsx", (string?)payload["page"]);
        Assert.Equal("Name is required", (string?)payload["props"]!["errors"]!["name"]);
        Assert.Equal(422, (int?)payload["status"]);
    }

    [Fact]
    public async Task Loader_Throwing_RendersErrorPageWithMessageInDevelopment()
    {
        await using var app = await TestApp.StartAsync(
            ["app/error.tsx", "app/page.tsx"],
            map: a => a.MapPageLoader("/", IResult () => throw new InvalidOperationException("database is down")));

        var response = await app.GetDocumentAsync("/");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var payload = app.Renderer.LastPayload;
        Assert.Equal("app/error.tsx", (string?)payload["page"]);
        Assert.Equal(500, (int?)payload["props"]!["status"]);
        Assert.Equal("database is down", (string?)payload["props"]!["message"]);
        Assert.Contains("InvalidOperationException", (string?)payload["props"]!["stack"]);
    }

    [Fact]
    public async Task Loader_Throwing_HidesDetailsOutsideDevelopment()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", IResult () => throw new InvalidOperationException("secret detail")),
            configure: o => o.Mode = NetjesMode.Development,
            environment: "Production");

        var payload = await app.GetDataJsonAsync("/");

        Assert.Equal(500, (int?)payload["status"]);
        Assert.Null((string?)payload["page"]);
        Assert.DoesNotContain("secret detail", payload.ToJsonString());
    }

    [Fact]
    public async Task SsrFailure_FallsBackToPlainErrorDocument()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"]);
        app.Renderer.OnRender = _ => throw new SsrRenderException("window is not defined", "ReferenceError: window is not defined\n    at Page");

        var response = await app.GetDocumentAsync("/");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("window is not defined", body);
    }

    [Fact]
    public async Task SharedProps_AreAddedToEveryPayload()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configure: o => o.SharedProps = ctx => ValueTask.FromResult<object?>(new { SiteName = "Demo", Path = ctx.Request.Path.Value }));

        var payload = await app.GetDataJsonAsync("/");

        Assert.Equal("Demo", (string?)payload["shared"]!["siteName"]);
        Assert.Equal("/", (string?)payload["shared"]!["path"]);
    }

    [Fact]
    public async Task Antiforgery_WhenRegistered_TokenIsIncludedForForms()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"], services: s => s.AddAntiforgery());

        var payload = await app.GetDataJsonAsync("/");

        Assert.False(string.IsNullOrEmpty((string?)payload["antiforgery"]!["requestToken"]));
        Assert.False(string.IsNullOrEmpty((string?)payload["antiforgery"]!["formFieldName"]));
    }

    [Fact]
    public async Task Render_FromAPlainEndpoint_RendersTheNamedPage()
    {
        await using var app = await TestApp.StartAsync(
            ["app/blog/[slug]/page.tsx"],
            map: a => a.MapGet("/featured", () => Page.Render("/blog/[slug]", new { Featured = true })));

        var payload = await app.GetDataJsonAsync("/featured");

        Assert.Equal("app/blog/[slug]/page.tsx", (string?)payload["page"]);
        Assert.True((bool?)payload["props"]!["featured"]);
    }

    [Fact]
    public async Task MapPages_Conventions_ApplyToEveryFilePage()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx", "app/about/page.tsx"],
            pages: p => p.WithMetadata(new MarkerMetadata()));

        var endpoints = app.App.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is "/" or "/about")
            .ToList();

        Assert.Equal(2, endpoints.Count);
        Assert.All(endpoints, e => Assert.NotNull(e.Metadata.GetMetadata<MarkerMetadata>()));
        Assert.Equal(["Page /", "Page /about"], endpoints.Select(e => e.DisplayName).Order());
    }

    private sealed class MarkerMetadata;

    private sealed class Greeter
    {
        public string Greet(string name) => $"Hello, {name}";
    }
}
