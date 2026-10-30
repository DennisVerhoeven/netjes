using System.Net;
using System.Text.Json.Nodes;

namespace TaskBoard.Tests;

[Collection(TaskBoardCollection.Name)]
[Trait("Category", "Docker")]
public sealed class BoardTests(TaskBoardApp app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Browser Browser, string Slug)> OwnerWithProjectAsync(string name = "Owner")
    {
        var browser = app.NewBrowser();
        await browser.SignUpAsync(name);
        var slug = ProjectsTests.NewSlug();
        var (status, response) = await browser.ActionAsync("projects.create", new { name = "Website", slug });
        Assert.True(status == HttpStatusCode.OK, response.ToJsonString());
        return (browser, slug);
    }

    private static async Task<int> CreateTaskAsync(Browser browser, string slug, string title, string? priority = null, string? state = null)
    {
        var (status, response) = await browser.ActionAsync("tasks.create", new { projectSlug = slug, title, priority, state });
        Assert.True(status == HttpStatusCode.OK, response.ToJsonString());
        return (int)response["result"]!["number"]!;
    }

    private static JsonNode Column(JsonNode page, string state) =>
        page["props"]!["columns"]!.AsArray().Single(c => (string?)c!["state"] == state)!;

    private static IEnumerable<string?> Titles(JsonNode column) => column["tasks"]!.AsArray().Select(t => (string?)t!["title"]);

    [Fact]
    public async Task NonMembers_GetTheForbiddenPage_WithoutTheProjectLayout()
    {
        var (_, slug) = await OwnerWithProjectAsync();
        var stranger = app.NewBrowser();
        await stranger.SignUpAsync("Stranger");

        var (status, page) = await stranger.PageAsync($"/projects/{slug}");

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("app/(app)/projects/forbidden.tsx", (string?)page["page"]);
        Assert.DoesNotContain(page["segments"]!.AsArray(), s => (string?)s!["id"] == "app/(app)/projects/[id]");
        Assert.DoesNotContain("Website", page.ToJsonString());
    }

    [Fact]
    public async Task UnknownProjects_AreNotFound()
    {
        var browser = app.NewBrowser();
        await browser.SignUpAsync();

        var (status, page) = await browser.PageAsync("/projects/no-such-project");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("app/(app)/projects/not-found.tsx", (string?)page["page"]);
    }

    [Fact]
    public async Task TheBoard_GroupsTasksByState_AndSeedsTheStats()
    {
        var (owner, slug) = await OwnerWithProjectAsync();
        await CreateTaskAsync(owner, slug, "Write copy");
        var shipped = await CreateTaskAsync(owner, slug, "Pick a domain");
        await owner.ActionAsync("tasks.move", new { projectSlug = slug, number = shipped, state = "done" });

        var (status, page) = await owner.PageAsync($"/projects/{slug}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["Write copy"], Titles(Column(page, "todo")));
        Assert.Equal(["Pick a domain"], Titles(Column(page, "done")));
        var project = page["segments"]!.AsArray().Single(s => (string?)s!["id"] == "app/(app)/projects/[id]")!;
        Assert.Equal("Website", (string?)project["props"]!["project"]!["name"]);
        var stats = page["queries"]!.AsArray().Single(q => q!["key"]!.ToJsonString() == $"[\"projects\",\"{slug}\",\"stats\"]")!;
        Assert.Equal(2, (int?)stats["data"]!["total"]);
        Assert.Equal(1, (int?)stats["data"]!["done"]);
    }

    [Fact]
    public async Task Tasks_AreNumberedPerProject()
    {
        var (owner, slug) = await OwnerWithProjectAsync();
        var (_, other) = await OwnerWithProjectAsync();

        var first = await CreateTaskAsync(owner, slug, "One");
        var second = await CreateTaskAsync(owner, slug, "Two");

        Assert.Equal((1, 2), (first, second));
        Assert.NotEqual(slug, other);
    }

    [Fact]
    public async Task CreatingATask_RevalidatesTheBoard_AndInvalidatesTheStats()
    {
        var (owner, slug) = await OwnerWithProjectAsync();

        var (_, response) = await owner.ActionAsync("tasks.create", new { projectSlug = slug, title = "Ship it" });

        Assert.True((bool?)response["revalidate"]);
        Assert.Equal($"[[\"projects\",\"{slug}\",\"stats\"]]", response["invalidate"]!.ToJsonString());
    }

    [Fact]
    public async Task ATaskNeedsATitle()
    {
        var (owner, slug) = await OwnerWithProjectAsync();

        var (status, response) = await owner.ActionAsync("tasks.create", new { projectSlug = slug, title = "" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.NotNull(response["errors"]!["title"]);
    }

    [Fact]
    public async Task NonMembers_CannotAddOrMoveTasks()
    {
        var (owner, slug) = await OwnerWithProjectAsync();
        var number = await CreateTaskAsync(owner, slug, "Mine");
        var stranger = app.NewBrowser();
        await stranger.SignUpAsync("Stranger");

        var (createStatus, _) = await stranger.ActionAsync("tasks.create", new { projectSlug = slug, title = "Theirs" });
        var (moveStatus, _) = await stranger.ActionAsync("tasks.move", new { projectSlug = slug, number, state = "done" });
        var (_, page) = await owner.PageAsync($"/projects/{slug}");

        Assert.Equal(HttpStatusCode.Forbidden, createStatus);
        Assert.Equal(HttpStatusCode.Forbidden, moveStatus);
        Assert.Equal(["Mine"], Titles(Column(page, "todo")));
    }

    [Fact]
    public async Task MovingATask_ChangesItsColumn_AndIsRecorded()
    {
        var (owner, slug) = await OwnerWithProjectAsync();
        var number = await CreateTaskAsync(owner, slug, "Launch post");

        var (status, response) = await owner.ActionAsync("tasks.move", new { projectSlug = slug, number, state = "inProgress" });
        var (_, page) = await owner.PageAsync($"/projects/{slug}");
        var activity = await app.WithDbAsync(db => Task.FromResult(
            db.Activity.Where(a => a.TaskNumber == number && db.Projects.Any(p => p.Id == a.ProjectId && p.Slug == slug)).Select(a => a.Summary).ToList()));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True((bool?)response["revalidate"]);
        Assert.Equal(["Launch post"], Titles(Column(page, "inProgress")));
        Assert.Contains(activity, s => s.Contains("In progress", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Filters_NarrowTheBoard()
    {
        var (owner, slug) = await OwnerWithProjectAsync();
        await CreateTaskAsync(owner, slug, "Fix login bug", priority: "urgent");
        await CreateTaskAsync(owner, slug, "Write docs", priority: "low");
        await CreateTaskAsync(owner, slug, "Fix footer", priority: "low");

        var (_, byText) = await owner.PageAsync($"/projects/{slug}?q=fix");
        var (_, byPriority) = await owner.PageAsync($"/projects/{slug}?priority=low");
        var (_, both) = await owner.PageAsync($"/projects/{slug}?q=FIX&priority=low");

        Assert.Equal(["Fix footer", "Fix login bug"], Titles(Column(byText, "todo")).Order());
        Assert.Equal(["Fix footer", "Write docs"], Titles(Column(byPriority, "todo")).Order());
        Assert.Equal(["Fix footer"], Titles(Column(both, "todo")));
    }

    [Fact]
    public async Task TheDoneColumn_IsPaged()
    {
        var (owner, slug) = await OwnerWithProjectAsync();
        for (var i = 1; i <= 12; i++)
        {
            await CreateTaskAsync(owner, slug, $"Done {i:00}", state: "done");
        }

        var (_, first) = await owner.PageAsync($"/projects/{slug}");
        var (_, second) = await owner.PageAsync($"/projects/{slug}?done=2");

        Assert.Equal(10, Column(first, "done")["tasks"]!.AsArray().Count);
        Assert.Equal(12, (int?)Column(first, "done")["total"]);
        Assert.True((bool?)first["props"]!["doneHasMore"]);
        Assert.Equal(2, Column(second, "done")["tasks"]!.AsArray().Count);
        Assert.False((bool?)second["props"]!["doneHasMore"]);
    }

    [Fact]
    public async Task TheStatsApi_IsForMembersOnly()
    {
        var (owner, slug) = await OwnerWithProjectAsync();
        await CreateTaskAsync(owner, slug, "One");
        var stranger = app.NewBrowser();
        await stranger.SignUpAsync("Stranger");

        var mine = await owner.Http.GetAsync($"/api/projects/{slug}/stats", Ct);
        var theirs = await stranger.Http.GetAsync($"/api/projects/{slug}/stats", Ct);
        var anonymous = await app.NewBrowser().Http.GetAsync($"/api/projects/{slug}/stats", Ct);

        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Equal(1, (int?)JsonNode.Parse(await mine.Content.ReadAsStringAsync(Ct))!["todo"]);
        Assert.Equal(HttpStatusCode.Forbidden, theirs.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }
}
