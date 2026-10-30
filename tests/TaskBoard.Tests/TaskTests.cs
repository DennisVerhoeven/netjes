using System.Net;

namespace TaskBoard.Tests;

[Collection(TaskBoardCollection.Name)]
[Trait("Category", "Docker")]
public sealed class TaskTests(TaskBoardApp app)
{
    private async Task<(Browser Owner, string Slug, int Number)> TaskAsync()
    {
        var owner = app.NewBrowser();
        await owner.SignUpAsync("Owner");
        var slug = ProjectsTests.NewSlug();
        await owner.ActionAsync("projects.create", new { name = "Website", slug });
        var (_, created) = await owner.ActionAsync("tasks.create", new { projectSlug = slug, title = "Launch post" });
        return (owner, slug, (int)created["result"]!["number"]!);
    }

    private static string TaskUrl(string slug, int number) => $"/projects/{slug}/tasks/{number}";

    [Fact]
    public async Task TheTaskPage_ShowsTheTask_AndStreamsTheActivity()
    {
        var (owner, slug, number) = await TaskAsync();
        await owner.ActionAsync("comments.add", new { projectSlug = slug, number, body = "First draft is up." });

        var (status, page) = await owner.PageAsync(TaskUrl(slug, number));
        var deferred = await owner.DeferredAsync(TaskUrl(slug, number));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Launch post", (string?)page["props"]!["task"]!["title"]);
        Assert.Equal("First draft is up.", (string?)page["props"]!["comments"]![0]!["body"]);
        Assert.Equal($"{slug.ToUpperInvariant()}-{number} Launch post", (string?)page["metadata"]!["title"]);
        var placeholder = page["props"]!["activity"]!;
        Assert.Equal("deferred", (string?)placeholder["$netjes"]);
        var activity = deferred[(string)placeholder["id"]!]!.AsArray().Select(a => (string?)a!["summary"]).ToList();
        Assert.Contains(activity, s => s!.Contains("commented", StringComparison.Ordinal));
        Assert.Contains(activity, s => s!.Contains("created", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConcurrentCreates_InOneProject_AllSucceed_WithConsecutiveNumbers()
    {
        var (owner, slug, first) = await TaskAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            owner.ActionAsync("tasks.create", new { projectSlug = slug, title = $"Parallel {i}" })));

        Assert.All(results, r => Assert.True(r.Status == HttpStatusCode.OK, r.Response.ToJsonString()));
        var numbers = results.Select(r => (int)r.Response["result"]!["number"]!).Order().ToList();
        Assert.Equal(Enumerable.Range(first + 1, 8), numbers);
    }

    [Fact]
    public async Task UnknownTasks_AreNotFound_InsideTheProject()
    {
        var (owner, slug, _) = await TaskAsync();

        var (status, page) = await owner.PageAsync(TaskUrl(slug, 999));
        var (textStatus, _) = await owner.PageAsync($"/projects/{slug}/tasks/not-a-number");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("app/(app)/projects/[id]/not-found.tsx", (string?)page["page"]);
        Assert.Contains(page["segments"]!.AsArray(), s => (string?)s!["id"] == "app/(app)/projects/[id]");
        Assert.Equal(HttpStatusCode.NotFound, textStatus);
    }

    [Fact]
    public async Task NonMembers_CannotSeeOrChangeTasks()
    {
        var (_, slug, number) = await TaskAsync();
        var stranger = app.NewBrowser();
        await stranger.SignUpAsync("Stranger");

        var (status, page) = await stranger.PageAsync(TaskUrl(slug, number));
        var (update, _) = await stranger.ActionAsync("tasks.update", new { projectSlug = slug, number, title = "Hijacked", state = "done", priority = "none" });
        var (comment, _) = await stranger.ActionAsync("comments.add", new { projectSlug = slug, number, body = "Hi" });

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.DoesNotContain("Launch post", page.ToJsonString());
        Assert.Equal(HttpStatusCode.Forbidden, update);
        Assert.Equal(HttpStatusCode.Forbidden, comment);
    }

    [Fact]
    public async Task UpdatingATask_ChangesIt_AndRecordsWhatChanged()
    {
        var (owner, slug, number) = await TaskAsync();
        var (_, layout) = await owner.PageAsync($"/projects/{slug}");
        var ownerId = (string)layout["shared"]!["user"]!["id"]!;

        var (status, response) = await owner.ActionAsync("tasks.update", new
        {
            projectSlug = slug, number, title = "Launch post v2", description = "Two pages.", state = "inProgress", priority = "high", assigneeId = ownerId,
        });
        var (_, page) = await owner.PageAsync(TaskUrl(slug, number));
        var deferred = await owner.DeferredAsync(TaskUrl(slug, number));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True((bool?)response["revalidate"]);
        var task = page["props"]!["task"]!;
        Assert.Equal(("Launch post v2", "Two pages.", "inProgress", "high", "Owner"),
            ((string?)task["title"], (string?)task["description"], (string?)task["state"], (string?)task["priority"], (string?)task["assignee"]!["name"]));
        Assert.Contains(deferred.Values.Single()!.AsArray(), a => ((string?)a!["summary"])!.Contains("updated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnlyMembersCanBeAssigned()
    {
        var (owner, slug, number) = await TaskAsync();

        var (status, response) = await owner.ActionAsync("tasks.update", new
        {
            projectSlug = slug, number, title = "Launch post", state = "todo", priority = "none", assigneeId = Guid.NewGuid().ToString(),
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.NotNull(response["errors"]!["assigneeId"]);
    }

    [Fact]
    public async Task UpdatesAndComments_AreValidated()
    {
        var (owner, slug, number) = await TaskAsync();

        var (update, updateErrors) = await owner.ActionAsync("tasks.update", new { projectSlug = slug, number, title = "", state = "todo", priority = "none" });
        var (comment, commentErrors) = await owner.ActionAsync("comments.add", new { projectSlug = slug, number, body = "" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, update);
        Assert.NotNull(updateErrors["errors"]!["title"]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, comment);
        Assert.NotNull(commentErrors["errors"]!["body"]);
    }

    [Fact]
    public async Task Members_CanBeAddedByOwners_ByEmail()
    {
        var (owner, slug, number) = await TaskAsync();
        var colleague = app.NewBrowser();
        var email = await colleague.SignUpAsync("Colleague");

        var (before, _) = await colleague.PageAsync(TaskUrl(slug, number));
        var (added, _) = await owner.ActionAsync("projects.addMember", new { projectSlug = slug, email = email.ToUpperInvariant() });
        var (after, _) = await colleague.PageAsync(TaskUrl(slug, number));
        var (_, settings) = await owner.PageAsync($"/projects/{slug}/settings");

        Assert.Equal(HttpStatusCode.Forbidden, before);
        Assert.Equal(HttpStatusCode.OK, added);
        Assert.Equal(HttpStatusCode.OK, after);
        Assert.Equal(["Colleague", "Owner"], settings["props"]!["members"]!.AsArray().Select(m => (string?)m!["name"]).Order());
    }

    [Fact]
    public async Task AddingMembers_IsForOwners_AndNeedsAnAccount()
    {
        var (owner, slug, _) = await TaskAsync();
        var member = app.NewBrowser();
        var memberEmail = await member.SignUpAsync("Member");
        await owner.ActionAsync("projects.addMember", new { projectSlug = slug, email = memberEmail });
        await member.PageAsync($"/projects/{slug}");

        var (byMember, _) = await member.ActionAsync("projects.addMember", new { projectSlug = slug, email = "someone@taskboard.test" });
        var (unknown, unknownErrors) = await owner.ActionAsync("projects.addMember", new { projectSlug = slug, email = "nobody-here@taskboard.test" });
        var (twice, twiceErrors) = await owner.ActionAsync("projects.addMember", new { projectSlug = slug, email = memberEmail });

        Assert.Equal(HttpStatusCode.Forbidden, byMember);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown);
        Assert.NotNull(unknownErrors["errors"]!["email"]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, twice);
        Assert.Contains("already", (string?)twiceErrors["errors"]!["email"]![0]);
    }

    [Fact]
    public async Task CommentsWithoutJavaScript_PostAndComeBackToTheTask()
    {
        var (owner, slug, number) = await TaskAsync();

        var response = await owner.PostFormAsync("comments.add", ("projectSlug", slug), ("number", number.ToString()), ("body", "No JS here."), ("__netjes_return", TaskUrl(slug, number)));
        var (_, page) = await owner.PageAsync(TaskUrl(slug, number));

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal(TaskUrl(slug, number), response.Headers.Location?.OriginalString);
        Assert.Equal("No JS here.", (string?)page["props"]!["comments"]!.AsArray().Last()!["body"]);
    }
}
