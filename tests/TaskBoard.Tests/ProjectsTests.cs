using System.Net;
using System.Text.Json.Nodes;

namespace TaskBoard.Tests;

[Collection(TaskBoardCollection.Name)]
[Trait("Category", "Docker")]
public sealed class ProjectsTests(TaskBoardApp app)
{
    public static string NewSlug() => $"p{Guid.NewGuid():N}"[..20];

    private static IEnumerable<string?> Slugs(JsonNode list) => list.AsArray().Select(p => (string?)p!["slug"]);

    [Fact]
    public async Task CreatedProjects_ShowOnTheProjectsPage_AndInTheSidebar()
    {
        var browser = app.NewBrowser();
        await browser.SignUpAsync();
        var slug = NewSlug();

        var (status, created) = await browser.ActionAsync("projects.create", new { name = "Website", slug, description = "The marketing site" });
        var (_, page) = await browser.PageAsync("/projects");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(slug, (string?)created["result"]!["slug"]);
        Assert.True((bool?)created["revalidate"]);
        Assert.Equal([slug], Slugs(page["props"]!["projects"]!));
        Assert.Equal("owner", (string?)page["props"]!["projects"]![0]!["role"]);
        var shell = page["segments"]!.AsArray().Single(s => (string?)s!["id"] == "app/(app)")!;
        Assert.Equal([slug], Slugs(shell["props"]!["projects"]!));
    }

    [Fact]
    public async Task ProjectsOfOtherUsers_AreNotListed()
    {
        var owner = app.NewBrowser();
        await owner.SignUpAsync();
        await owner.ActionAsync("projects.create", new { name = "Secret", slug = NewSlug() });
        var other = app.NewBrowser();
        await other.SignUpAsync();

        var (_, page) = await other.PageAsync("/projects");

        Assert.Empty(page["props"]!["projects"]!.AsArray());
    }

    [Fact]
    public async Task TakenKey_IsAFieldError()
    {
        var browser = app.NewBrowser();
        await browser.SignUpAsync();
        var slug = NewSlug();
        await browser.ActionAsync("projects.create", new { name = "One", slug });

        var (status, response) = await browser.ActionAsync("projects.create", new { name = "Two", slug });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Contains("taken", (string?)response["errors"]!["slug"]![0]);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("Has Spaces")]
    [InlineData("9starts-with-digit")]
    public async Task InvalidKey_IsAFieldError(string slug)
    {
        var browser = app.NewBrowser();
        await browser.SignUpAsync();

        var (status, response) = await browser.ActionAsync("projects.create", new { name = "Website", slug });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.NotNull(response["errors"]!["slug"]);
    }

    [Fact]
    public async Task CreatingAProject_NeedsASignedInUser()
    {
        var browser = app.NewBrowser();
        await browser.PageAsync("/");

        var (status, response) = await browser.ActionAsync("projects.create", new { name = "Website", slug = NewSlug() });

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("action", (string?)response["kind"]);
    }
}
