using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Netjes.Tests.Endpoints;

/// <summary>The loader parameters that say where they come from, on page and layout loaders alike.</summary>
public class LoaderParameterTests
{
    private static readonly string[] Files = ["app/layout.tsx", "app/error.tsx", "app/[team]/layout.tsx", "app/[team]/page.tsx"];

    private const string Url = "/core?page=2&tags=a&tags=b&sort=new&size=10&q=x";

    public sealed record Paging(int? Size);

    public sealed class SelfBoundFilter
    {
        public string? Q { get; init; }

        public static ValueTask<SelfBoundFilter?> BindAsync(HttpContext http, ParameterInfo parameter) =>
            ValueTask.FromResult<SelfBoundFilter?>(new SelfBoundFilter { Q = http.Request.Query["q"] });
    }

    public sealed record Bound(string Greeting, int Page, string[] Tags, string? Order, int? Size, string? Q);

    // A route value, an int and an array from the query string, [FromQuery], [AsParameters], a type that binds itself
    // and a registered service.
    private static PageResult<Bound> Load(
        string team, int page, string[] tags, [FromQuery(Name = "sort")] string? order, [AsParameters] Paging paging,
        SelfBoundFilter filter, Greeter greeter) =>
        Page.Props(new Bound(greeter.Greet(team), page, tags, order, paging.Size, filter.Q));

    private static void AssertBound(JsonNode props)
    {
        Assert.Equal("Hello, core", (string?)props["greeting"]);
        Assert.Equal(2, (int?)props["page"]);
        Assert.Equal(["a", "b"], props["tags"]!.AsArray().Select(t => (string?)t));
        Assert.Equal("new", (string?)props["order"]);
        Assert.Equal(10, (int?)props["size"]);
        Assert.Equal("x", (string?)props["q"]);
    }

    [Fact]
    public async Task PageLoader_ParametersThatSayWhereTheyComeFrom_StartAndBind()
    {
        await using var app = await TestApp.StartAsync(Files, map: a => a.MapPageLoader("/[team]", Load), services: s => s.AddSingleton<Greeter>());

        AssertBound((await app.GetDataJsonAsync(Url))["props"]!);
    }

    [Fact]
    public async Task LayoutLoader_ParametersThatSayWhereTheyComeFrom_StartAndBind()
    {
        await using var app = await TestApp.StartAsync(Files, map: a => a.MapLayoutLoader("/[team]", Load), services: s => s.AddSingleton<Greeter>());

        AssertBound((await app.GetDataJsonAsync(Url))["segments"]![1]!["props"]!);
    }

    public sealed record Note(string? Text);

    public sealed record PostedNote(string? Q, [FromBody] Note Note);

    // Minimal APIs bind these on a GET endpoint too, from a body a navigation never has: a page loader's page answered
    // its not-found page on every navigation, and a layout loader failed every page below the layout.
    public static TheoryData<string, Delegate, string> ParametersThatReadTheRequestBody => new()
    {
        { "[FromBody]", (string team, [FromBody] Note note) => Page.Props(), "parameter 'note' of type Note: it reads" },
        { "[FromForm]", (string team, [FromForm] string name) => Page.Props(), "parameter 'name' of type string: it reads" },
        { "a file", (string team, IFormFile photo) => Page.Props(), "parameter 'photo' of type IFormFile: it reads" },
        { "files", (string team, IFormFileCollection photos) => Page.Props(), "parameter 'photos' of type IFormFileCollection: it reads" },
        { "a form", (string team, IFormCollection form) => Page.Props(), "parameter 'form' of type IFormCollection: it reads" },
        {
            "an [AsParameters] member", (string team, [AsParameters] PostedNote posted) => Page.Props(),
            "'Note' of type Note, a member of its [AsParameters] type PostedNote: it reads"
        },
        {
            "several", (string team, [FromForm] string name, IFormFile photo) => Page.Props(),
            "parameter 'name' of type string and parameter 'photo' of type IFormFile: they read"
        },
    };

    [Theory]
    [MemberData(nameof(ParametersThatReadTheRequestBody))]
    public async Task PageLoader_AParameterThatReadsTheRequestBody_FailsAtStartupNamingItAndWhereToReadFrom(string name, Delegate loader, string named)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(Files, map: a => a.MapPageLoader("/[team]", loader)));

        AssertSaysLoadersReadNoBody(ex.Message, $"The loader of app/[team]/page.tsx (/[team]) cannot bind {named}", name);
    }

    [Theory]
    [MemberData(nameof(ParametersThatReadTheRequestBody))]
    public async Task LayoutLoader_AParameterThatReadsTheRequestBody_FailsAtStartupNamingItAndWhereToReadFrom(string name, Delegate loader, string named)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(Files, map: a => a.MapLayoutLoader("/[team]", loader)));

        AssertSaysLoadersReadNoBody(ex.Message, $"The layout loader of app/[team]/layout.tsx (/[team]) cannot bind {named}", name);
    }

    public sealed record IdsQuery(string? Q, IEnumerable<int> Ids);

    // The service container supplies an IEnumerable<T> whether or not one is registered, so minimal APIs bound these
    // from services, an empty sequence, and never read the query string: the loader saw no values on any request.
    public static TheoryData<string, Delegate, string, string> CollectionsTheServicesSupply => new()
    {
        { "IEnumerable<int>", (string team, IEnumerable<int> ids) => Page.Props(), "parameter 'ids' of type IEnumerable<int>", "int" },
        { "IEnumerable<string>", (string team, IEnumerable<string> tags) => Page.Props(), "parameter 'tags' of type IEnumerable<string>", "string" },
        { "IEnumerable<Guid?>", (string team, IEnumerable<Guid?> ids) => Page.Props(), "parameter 'ids' of type IEnumerable<Guid?>", "Guid?" },
        {
            "an [AsParameters] member", (string team, [AsParameters] IdsQuery query) => Page.Props(),
            "'Ids' of type IEnumerable<int>, a member of its [AsParameters] type IdsQuery", "int"
        },
    };

    [Theory]
    [MemberData(nameof(CollectionsTheServicesSupply))]
    public async Task PageLoader_ACollectionTheServicesSupplyUnregistered_FailsAtStartupSayingToTakeAnArray(string name, Delegate loader, string named, string element)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(Files, map: a => a.MapPageLoader("/[team]", loader)));

        AssertSaysToTakeAnArray(ex.Message, $"The loader of app/[team]/page.tsx (/[team]) cannot bind {named}", element, name);
    }

    [Theory]
    [MemberData(nameof(CollectionsTheServicesSupply))]
    public async Task LayoutLoader_ACollectionTheServicesSupplyUnregistered_FailsAtStartupSayingToTakeAnArray(string name, Delegate loader, string named, string element)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(Files, map: a => a.MapLayoutLoader("/[team]", loader)));

        AssertSaysToTakeAnArray(ex.Message, $"The layout loader of app/[team]/layout.tsx (/[team]) cannot bind {named}", element, name);
    }

    private static void AssertSaysToTakeAnArray(string message, string named, string element, string name)
    {
        Assert.True(message.Contains(named, StringComparison.Ordinal), $"{name}: {message}");
        Assert.True(message.Contains($"Take {element}[] instead", StringComparison.Ordinal), $"{name}: {message}");
        Assert.True(message.Contains($"[FromQuery] {element}[]", StringComparison.Ordinal), $"{name}: {message}");
        Assert.Contains("the app's services", message);
        Assert.Contains("[FromServices]", message);
        Assert.DoesNotContain("'team'", message);
    }

    public interface IPlugin;

    public sealed record Services(int Greeters, int Plugins, int Ints, string[] Hosts);

    // What the services are for: the registered implementations of a service, none of a service nothing implements,
    // a sequence marked [FromServices], and a collection registered as a service in its own right.
    private static PageResult<Services> LoadServices(
        string team, IEnumerable<Greeter> greeters, IEnumerable<IPlugin> plugins, [FromServices] IEnumerable<int> ints, IReadOnlyList<string> hosts) =>
        Page.Props(new Services(greeters.Count(), plugins.Count(), ints.Count(), [.. hosts]));

    private static void RegisterServices(IServiceCollection services) =>
        services.AddSingleton<Greeter>().AddSingleton<IReadOnlyList<string>>(["a.example", "b.example"]);

    private static void AssertServices(JsonNode props)
    {
        Assert.Equal(1, (int?)props["greeters"]);
        Assert.Equal(0, (int?)props["plugins"]);
        Assert.Equal(0, (int?)props["ints"]);
        Assert.Equal(["a.example", "b.example"], props["hosts"]!.AsArray().Select(h => (string?)h));
    }

    [Fact]
    public async Task PageLoader_CollectionsOfServices_StartAndBindFromTheServices()
    {
        await using var app = await TestApp.StartAsync(Files, map: a => a.MapPageLoader("/[team]", LoadServices), services: RegisterServices);

        AssertServices((await app.GetDataJsonAsync("/core?ints=1&hosts=c.example"))["props"]!);
    }

    [Fact]
    public async Task LayoutLoader_CollectionsOfServices_StartAndBindFromTheServices()
    {
        await using var app = await TestApp.StartAsync(Files, map: a => a.MapLayoutLoader("/[team]", LoadServices), services: RegisterServices);

        AssertServices((await app.GetDataJsonAsync("/core?ints=1&hosts=c.example"))["segments"]![1]!["props"]!);
    }

    /// <summary>Accepts metadata as minimal APIs add it for a body: required, or optional as for an empty body allowed.</summary>
    private sealed record Accepts(IReadOnlyList<string> ContentTypes, bool IsOptional = false) : IAcceptsMetadata
    {
        public Type? RequestType => null;
    }

    private static ValueTask<T?> FromQuery<T>(HttpContext http, Func<string?, T> filter) => ValueTask.FromResult<T?>(filter(http.Request.Query["q"]));

    // Types that bind themselves and add accepts metadata for a body, as a type might that reads one on a POST
    // endpoint and the query string on a GET. Routing matched a page loader's endpoint only for requests with that
    // Content-Type, and the not-found fallback took every navigation, which has none.
    public sealed class JsonFilter : IEndpointParameterMetadataProvider
    {
        public string? Q { get; init; }

        public static ValueTask<JsonFilter?> BindAsync(HttpContext http, ParameterInfo parameter) => FromQuery(http, q => new JsonFilter { Q = q });

        public static void PopulateMetadata(ParameterInfo parameter, EndpointBuilder builder) => builder.Metadata.Add(new Accepts(["application/json"]));
    }

    public sealed class OptionalJsonFilter : IEndpointParameterMetadataProvider
    {
        public string? Q { get; init; }

        public static ValueTask<OptionalJsonFilter?> BindAsync(HttpContext http, ParameterInfo parameter) => FromQuery(http, q => new OptionalJsonFilter { Q = q });

        public static void PopulateMetadata(ParameterInfo parameter, EndpointBuilder builder) => builder.Metadata.Add(new Accepts(["application/json"], IsOptional: true));
    }

    public sealed class TextFilter : IEndpointMetadataProvider
    {
        public string? Q { get; init; }

        public static ValueTask<TextFilter?> BindAsync(HttpContext http, ParameterInfo parameter) => FromQuery(http, q => new TextFilter { Q = q });

        public static void PopulateMetadata(MethodInfo method, EndpointBuilder builder) => builder.Metadata.Add(new Accepts(["text/plain", "application/json"]));
    }

    public sealed class OptionalTextFilter : IEndpointMetadataProvider
    {
        public string? Q { get; init; }

        public static ValueTask<OptionalTextFilter?> BindAsync(HttpContext http, ParameterInfo parameter) => FromQuery(http, q => new OptionalTextFilter { Q = q });

        public static void PopulateMetadata(MethodInfo method, EndpointBuilder builder) => builder.Metadata.Add(new Accepts(["text/plain"], IsOptional: true));
    }

    public sealed class JsonBodyResult : IResult, IEndpointMetadataProvider
    {
        public Task ExecuteAsync(HttpContext httpContext) => Task.CompletedTask;

        public static void PopulateMetadata(MethodInfo method, EndpointBuilder builder) => builder.Metadata.Add(new Accepts(["application/json"]));
    }

    public sealed record JsonQuery(string? Sort, JsonFilter Filter);

    public static TheoryData<string, Delegate, string, string> TypesThatAddAcceptsMetadata => new()
    {
        {
            "through IEndpointParameterMetadataProvider", (string team, JsonFilter filter) => Page.Props(),
            "bind parameter 'filter' of type JsonFilter: its type adds", "a Content-Type of application/json"
        },
        {
            "optional, through IEndpointParameterMetadataProvider", (string team, OptionalJsonFilter filter) => Page.Props(),
            "bind parameter 'filter' of type OptionalJsonFilter: its type adds", "a Content-Type of application/json"
        },
        {
            "through IEndpointMetadataProvider", (string team, TextFilter filter) => Page.Props(),
            "bind parameter 'filter' of type TextFilter: its type adds", "a Content-Type of text/plain or application/json"
        },
        {
            "optional, through IEndpointMetadataProvider", (string team, OptionalTextFilter filter) => Page.Props(),
            "bind parameter 'filter' of type OptionalTextFilter: its type adds", "a Content-Type of text/plain"
        },
        {
            "several", (string team, string? q, JsonFilter filter, TextFilter text) => Page.Props(),
            "bind parameter 'filter' of type JsonFilter and parameter 'text' of type TextFilter: their types add", "a Content-Type of text/plain or application/json"
        },
        {
            "an [AsParameters] member", (string team, [AsParameters] JsonQuery query) => Page.Props(),
            "bind 'Filter' of type JsonFilter, a member of its [AsParameters] type JsonQuery: its type adds", "a Content-Type of application/json"
        },
        { "the result", (string team) => new JsonBodyResult(), "return JsonBodyResult: its type adds", "a Content-Type of application/json" },
    };

    [Theory]
    [MemberData(nameof(TypesThatAddAcceptsMetadata))]
    public async Task PageLoader_ATypeThatAddsAcceptsMetadataForItself_FailsAtStartupSayingNavigationsWouldNotReachThePage(
        string name, Delegate loader, string named, string contentTypes)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApp.StartAsync(Files, map: a => a.MapPageLoader("/[team]", loader)));

        Assert.True(ex.Message.Contains($"The loader of app/[team]/page.tsx (/[team]) cannot {named} accepts metadata (IAcceptsMetadata)", StringComparison.Ordinal), $"{name}: {ex.Message}");
        Assert.True(ex.Message.Contains($"only for requests with {contentTypes},", StringComparison.Ordinal), $"{name}: {ex.Message}");
        Assert.Contains("every navigation, a GET request without a body, gets the not-found page", ex.Message);
        Assert.Contains("a page action (MapPageAction)", ex.Message);
        Assert.DoesNotContain("'team'", ex.Message);
        Assert.DoesNotContain("'q'", ex.Message);
    }

    // A layout loader's delegate is called while the page renders, not routed to, so the metadata changes nothing.
    [Fact]
    public async Task LayoutLoader_TypesThatAddAcceptsMetadataForThemselves_StartAndBind()
    {
        await using var app = await TestApp.StartAsync(Files, map: a => a.MapLayoutLoader("/[team]",
            (string team, JsonFilter json, OptionalTextFilter text) => Page.Props(new { Json = json.Q, Text = text.Q })));

        var response = await app.GetDocumentAsync("/core?q=x");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var props = (await app.GetDataJsonAsync("/core?q=x"))["segments"]![1]!["props"]!;
        Assert.Equal("x", (string?)props["json"]);
        Assert.Equal("x", (string?)props["text"]);
    }

    private static void AssertSaysLoadersReadNoBody(string message, string named, string name)
    {
        Assert.True(message.Contains(named, StringComparison.Ordinal), $"{name}: {message}");
        Assert.Contains("the request body, which loaders do not read", message);
        Assert.Contains("the route or the query string", message);
        Assert.Contains("a service", message);
        Assert.Contains("a page action (MapPageAction)", message);
        Assert.DoesNotContain("'team'", message);
    }

    public sealed record Requested(string Greeting, string Method, string Path, string? Header, bool SignedIn, bool Cancelled);

    // The request, its headers, the user and the request's cancellation, none of which reads the body.
    private static PageResult<Requested> LoadFromTheRequest(
        string team, HttpContext http, HttpRequest request, System.Security.Claims.ClaimsPrincipal user,
        CancellationToken cancellationToken, [FromHeader(Name = "X-Team")] string? header, [FromServices] Greeter greeter) =>
        Page.Props(new Requested(
            greeter.Greet(team), http.Request.Method, request.Path, header, user.Identity?.IsAuthenticated == true,
            cancellationToken.IsCancellationRequested));

    private static async Task<JsonNode> GetWithHeaderAsync(TestApp app)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/core");
        request.Headers.Add(NetjesHeaders.Data, "1");
        request.Headers.Add("X-Team", "from-a-header");
        var response = await app.Client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
    }

    private static void AssertRequested(JsonNode props)
    {
        Assert.Equal("Hello, core", (string?)props["greeting"]);
        Assert.Equal("GET", (string?)props["method"]);
        Assert.Equal("/core", (string?)props["path"]);
        Assert.Equal("from-a-header", (string?)props["header"]);
        Assert.False((bool?)props["signedIn"]);
        Assert.False((bool?)props["cancelled"]);
    }

    [Fact]
    public async Task PageLoader_ParametersThatReadTheRequestButNotItsBody_StartAndBind()
    {
        await using var app = await TestApp.StartAsync(Files, map: a => a.MapPageLoader("/[team]", LoadFromTheRequest), services: s => s.AddSingleton<Greeter>());

        AssertRequested((await GetWithHeaderAsync(app))["props"]!);
    }

    [Fact]
    public async Task LayoutLoader_ParametersThatReadTheRequestButNotItsBody_StartAndBind()
    {
        await using var app = await TestApp.StartAsync(Files, map: a => a.MapLayoutLoader("/[team]", LoadFromTheRequest), services: s => s.AddSingleton<Greeter>());

        AssertRequested((await GetWithHeaderAsync(app))["segments"]![1]!["props"]!);
    }
}
