using Microsoft.AspNetCore.Builder;

namespace Netjes.Tests.Endpoints;

public class MetadataTests
{
    [Fact]
    public async Task WithMetadata_TravelsInThePayload_WithoutEmptyFields()
    {
        await using var app = await TestApp.StartAsync(
            ["app/posts/[slug]/page.tsx"],
            map: a => a.MapPageLoader("/posts/[slug]", (string slug) => Page.Props(new { Slug = slug }).WithMetadata(new PageMetadata
            {
                Title = $"Post {slug}",
                Robots = new PageMetadataRobots(Index: false),
                OpenGraph = new PageMetadataOpenGraph { Images = [new PageMetadataImage($"https://acme.test/og/{slug}.png", 1200, 630)] },
            })));

        var payload = await app.GetDataJsonAsync("/posts/hello");

        Assert.Equal(
            """{"title":"Post hello","robots":{"index":false,"follow":true},"openGraph":{"images":[{"url":"https://acme.test/og/hello.png","width":1200,"height":630}]}}""",
            payload["metadata"]!.ToJsonString());
    }

    [Fact]
    public async Task WithMetadata_CarriesImageDetails_ForOpenGraphAndTwitter()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props().WithMetadata(new PageMetadata
            {
                OpenGraph = new PageMetadataOpenGraph { Images = [new PageMetadataImage("/og.png", 1200, 630, "A \"quoted\" & plain alt")] },
                Twitter = new PageMetadataTwitter { Images = [new PageMetadataImage("/tw.png", Alt: "Card alt"), "/plain.png"] },
            })));

        var metadata = (await app.GetDataJsonAsync("/"))["metadata"]!;

        Assert.Equal(
            """{"openGraph":{"images":[{"url":"/og.png","width":1200,"height":630,"alt":"A \u0022quoted\u0022 \u0026 plain alt"}]},"twitter":{"images":[{"url":"/tw.png","alt":"Card alt"},{"url":"/plain.png"}]}}""",
            metadata.ToJsonString());
    }

    [Fact]
    public async Task WithMetadata_SurvivesWithStatusAndWithQuery()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props().WithMetadata(new PageMetadata { Title = "Home" }).WithStatus(202).WithQuery(["a"], 1)));

        var payload = await app.GetDataJsonAsync("/");

        Assert.Equal("Home", (string?)payload["metadata"]!["title"]);
    }

    [Fact]
    public async Task Payload_WithoutMetadata_OmitsIt()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"]);

        Assert.Null((await app.GetDataJsonAsync("/"))["metadata"]);
    }
}
