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
    public async Task WithMetadata_AnAbsoluteTitle_TravelsAsTheTitlesAbsoluteForm()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props().WithMetadata(new PageMetadata { AbsoluteTitle = "Users · Back office", Description = "d" })));

        var metadata = (await app.GetDataJsonAsync("/"))["metadata"]!;

        Assert.Equal("""{"title":{"absolute":"Users · Back office"},"description":"d"}""", metadata.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    [Fact]
    public async Task WithMetadata_AnAbsoluteTitleAndATitle_SendsTheAbsoluteOne()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props().WithMetadata(new PageMetadata { Title = "Plain", AbsoluteTitle = "Bare" })));

        var title = (await app.GetDataJsonAsync("/"))["metadata"]!["title"]!;

        Assert.Equal("Bare", (string?)title["absolute"]);
    }

    [Fact]
    public async Task WithMetadata_ATitleAlone_StaysAString()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props().WithMetadata(new PageMetadata { Title = "Plain" })));

        Assert.Equal("""{"title":"Plain"}""", (await app.GetDataJsonAsync("/"))["metadata"]!.ToJsonString());
    }

    [Fact]
    public async Task Payload_WithoutMetadata_OmitsIt()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"]);

        Assert.Null((await app.GetDataJsonAsync("/"))["metadata"]);
    }

    [Fact]
    public async Task WithMetadata_ALang_TravelsAsThePagesLang_OutsideTheTags()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props().WithMetadata(new PageMetadata { Title = "Thuis", Lang = "nl" })));

        var payload = await app.GetDataJsonAsync("/");

        Assert.Equal("nl", (string?)payload["lang"]);
        Assert.Equal("""{"title":"Thuis"}""", payload["metadata"]!.ToJsonString());
    }

    [Fact]
    public async Task Payload_WithoutALangFromTheLoader_OmitsIt()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props().WithMetadata(new PageMetadata { Title = "Home" })));

        var payload = await app.GetDataJsonAsync("/");

        Assert.False(payload.AsObject().ContainsKey("lang"));
    }

    [Theory]
    [InlineData("nl")]
    [InlineData("en-GB")]
    [InlineData("zh-Hant-TW")]
    [InlineData("sr-Latn-RS")]
    [InlineData("de-CH-1996")]
    [InlineData("en-US-u-ca-gregory")]
    [InlineData("x-private")]
    [InlineData("i-klingon")]
    [InlineData("NL")]
    public void Lang_ALanguageTag_IsKept(string tag) => Assert.Equal(tag, new PageMetadata { Lang = tag }.Lang);

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("n")]
    [InlineData("x")]
    [InlineData("nl_NL")]
    [InlineData("-nl")]
    [InlineData("nl-")]
    [InlineData("nl--BE")]
    [InlineData("toolongtag")]
    [InlineData("nl-toolongsub")]
    [InlineData("nl\n")]
    [InlineData("nl\" onmouseover=\"alert(1)")]
    [InlineData("nl><script>alert(1)</script>")]
    [InlineData("nl&amp;")]
    [InlineData("ｎｌ")]
    public void Lang_ThatIsNoLanguageTag_Throws(string value)
    {
        var error = Assert.Throws<ArgumentException>(() => new PageMetadata { Lang = value });

        Assert.Equal("Lang", error.ParamName);
    }
}
