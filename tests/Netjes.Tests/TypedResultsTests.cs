using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Netjes.Rendering;
using Netjes.Tests.Endpoints;

namespace Netjes.Tests;

/// <summary>A loader's inferred return type names its props type, which code generation and tests rely on.</summary>
public class TypedResultsTests
{
    public sealed record Post(string Title);

    private static readonly bool Found = DateTime.UtcNow.Ticks > 0;

    private static Type ReturnTypeOf(Delegate loader) => loader.Method.ReturnType;

    [Fact]
    public void Props_ReturnsAResultTypedByItsProps()
    {
        PageResult<Post> result = Page.Props(new Post("Hello"));

        Assert.Equal("Hello", result.Props?.Title);
    }

    [Fact]
    public void Ternary_WithNotFound_InfersTheTypedResult()
    {
        Assert.Equal(typeof(PageResult<Post>), ReturnTypeOf((string slug) => Found ? Page.Props(new Post(slug)) : Page.NotFound()));
    }

    [Fact]
    public void BlockBody_WithRedirectsAndNotFound_InfersTheTypedResult()
    {
        var loader = (string slug) =>
        {
            if (slug == "old") return Page.Redirect("/new");
            if (slug == "home") return Page.LocalRedirect("/");
            if (slug == "") return Page.NotFound();
            return Page.Props(new Post(slug));
        };

        Assert.Equal(typeof(PageResult<Post>), ReturnTypeOf(loader));
    }

    [Fact]
    public void AsyncLoader_InfersATaskOfTheTypedResult()
    {
        var loader = async (string slug) =>
        {
            await Task.Yield();
            return Found ? Page.Props(new Post(slug)) : Page.NotFound();
        };

        Assert.Equal(typeof(Task<PageResult<Post>>), ReturnTypeOf(loader));
    }

    [Fact]
    public void WithStatusAndWithQuery_KeepTheType()
    {
        PageResult<Post> result = Page.Props(new Post("a")).WithStatus(422).WithQuery(["likes", "a"], 3);

        Assert.Equal("a", result.Props?.Title);
    }

    [Fact]
    public void NotFound_ConvertedToATypedResult_HasNoProps()
    {
        PageResult<Post> result = Page.NotFound();

        Assert.Null(result.Props);
    }

    [Fact]
    public void PropsWithoutArguments_IsTypedAsNoProps()
    {
        Assert.Equal(typeof(PageResult<NoProps>), ReturnTypeOf(() => Page.Props()));
    }

    [Theory]
    [InlineData("sync")]
    [InlineData("task")]
    [InlineData("valuetask")]
    public async Task MapPageLoader_RecordsThePropsTypeAsEndpointMetadata(string shape)
    {
        await using var app = await TestApp.StartAsync(
            ["app/blog/[slug]/page.tsx"],
            map: a => _ = shape switch
            {
                "sync" => a.MapPageLoader("/blog/[slug]", (string slug) => Page.Props(new Post(slug))),
                "task" => a.MapPageLoader("/blog/[slug]", async (string slug) => { await Task.Yield(); return Page.Props(new Post(slug)); }),
                _ => a.MapPageLoader("/blog/[slug]", (string slug) => ValueTask.FromResult(Page.Props(new Post(slug)))),
            });

        var endpoint = PageEndpoint(app, "/blog/[slug]");

        Assert.Equal(typeof(Post), endpoint.Metadata.GetMetadata<PagePropsMetadata>()?.PropsType);
    }

    [Fact]
    public async Task MapPageLoader_UntypedResult_RecordsNoPropsType()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"], map: a => a.MapPageLoader("/", () => TypedResults.StatusCode(StatusCodes.Status418ImATeapot)));

        Assert.Null(PageEndpoint(app, "/").Metadata.GetMetadata<PagePropsMetadata>());
    }

    [Fact]
    public async Task MapPageAction_RecordsThePropsTypeOfTheRerenderedPage()
    {
        await using var app = await TestApp.StartAsync(
            ["app/contact/page.tsx"],
            map: a => a.MapPageAction("/contact", (HttpContext http) => Found ? Page.Props(new Post("x")).WithStatus(422) : Page.Redirect("/thanks")));

        var action = app.App.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Single(e => e.DisplayName == "Page action /contact");

        Assert.Equal(typeof(Post), action.Metadata.GetMetadata<PagePropsMetadata>()?.PropsType);
    }

    [Fact]
    public async Task TypedResult_RendersLikeAnUntypedOne()
    {
        await using var app = await TestApp.StartAsync(
            ["app/blog/[slug]/page.tsx"], map: a => a.MapPageLoader("/blog/[slug]", (string slug) => Page.Props(new Post(slug))));

        var payload = await app.GetDataJsonAsync("/blog/hi");

        Assert.Equal("hi", (string?)payload["props"]!["title"]);
    }

    [Fact]
    public async Task UseSharedProps_SetsTheSharedPropsAndRecordsTheirType()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"], configure: o => o.UseSharedProps(_ => ValueTask.FromResult(new Post("site"))));

        var payload = await app.GetDataJsonAsync("/");

        Assert.Equal("site", (string?)payload["shared"]!["title"]);
        Assert.Equal(typeof(Post), app.App.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<NetjesOptions>>().Value.SharedPropsType);
    }

    private static Endpoint PageEndpoint(TestApp app, string pattern) =>
        app.App.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Single(e => e.DisplayName == $"Page {pattern}");
}
