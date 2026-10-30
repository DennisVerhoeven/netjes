using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace TaskBoard.Tests;

/// <summary>
/// Tenant isolation: a project, its tasks and its members are for the project's members only. Every loader and action
/// of the app is listed here with what it takes from the request and where its refusal is tested.
/// <list type="table">
///   <listheader><term>Loader or action</term><description>Tenant input; coverage</description></listheader>
///   <item><term>page /login, /register (AuthEndpoints)</term><description>None; only a return URL (AuthTests).</description></item>
///   <item><term>layout app/(app) (AppShell)</term><description>The signed-in user only: the sidebar lists their own
///   projects. 401 when signed out; never another user's projects, on any page below it (all tests here).</description></item>
///   <item><term>page /projects (ProjectsEndpoints)</term><description>The signed-in user only (ProjectsTests).</description></item>
///   <item><term>layout /projects/[id] (ProjectLayout)</term><description>The slug; NotFound or Forbidden, and the stats query
///   it seeds. Strangers, unknown and respelled slugs, signed-out visitors, a revoked member holding the layout.</description></item>
///   <item><term>page /projects/[id] (BoardEndpoints)</term><description>The slug, filters. As the layout, plus its own check
///   when the layout's props are reused.</description></item>
///   <item><term>page /projects/[id]/settings (MembersEndpoints)</term><description>The slug: member names and emails. As the board.</description></item>
///   <item><term>page /projects/[id]/tasks/[taskId] (TaskEndpoints)</term><description>The slug and the task number, and the
///   deferred activity. As the board; a number is looked up inside the slug's project only, and a refused page streams no
///   deferred value.</description></item>
///   <item><term>GET /api/projects/{slug}/stats</term><description>The slug. Strangers in any spelling, unknown slugs, signed out.</description></item>
///   <item><term>action projects.create</term><description>A new slug; a taken one is a field error that reveals nothing more (ProjectsTests).</description></item>
///   <item><term>actions tasks.create, tasks.move, tasks.update, comments.add</term><description>projectSlug and number:
///   strangers and unknown projects, as action calls and as forms without JavaScript, change nothing and get no data back;
///   a number from another project is not found; only members of the project can be assigned.</description></item>
///   <item><term>action projects.addMember</term><description>projectSlug: strangers (adding themselves) and members who are not
///   owners (TaskTests) are refused.</description></item>
///   <item><term>actions auth.register, auth.login, auth.logout</term><description>None (AuthTests, AuthLimitTests).</description></item>
/// </list>
/// Every request kind is tried: documents, client navigations (also from a page holding the owner's layout keys), HEAD,
/// action calls and form posts without JavaScript. "No data" means none of the project's or owner's distinctive strings
/// appear anywhere in the answer, the document's payload included.
/// </summary>
[Collection(TaskBoardCollection.Name)]
[Trait("Category", "Docker")]
public sealed class TenantIsolationTests(TaskBoardApp app)
{
    private const string ShellSegment = "app/(app)";
    private const string ProjectSegment = "app/(app)/projects/[id]";

    /// <summary>
    /// Another user's project with a task and a comment, every value distinctive, and the keys of the layouts the owner's
    /// browser holds on its board.
    /// </summary>
    private sealed record Project(Browser Owner, string Slug, int Task, string[] Secrets, string[] OwnerKeys)
    {
        public string Url(string page) => page switch
        {
            "board" => $"/projects/{Slug}",
            "settings" => $"/projects/{Slug}/settings",
            "task" => $"/projects/{Slug}/tasks/{Task}",
            _ => throw new ArgumentOutOfRangeException(nameof(page)),
        };
    }

    public static TheoryData<string> Pages => ["board", "settings", "task"];

    private async Task<Project> ProjectAsync()
    {
        var id = Guid.NewGuid().ToString("N")[..10];
        var owner = app.NewBrowser();
        var ownerName = $"Owner {id}";
        var ownerEmail = await owner.SignUpAsync(ownerName);
        var slug = ProjectsTests.NewSlug();
        string[] values = [$"Secret project {id}", $"Secret description {id}", $"Secret task {id}", $"Secret comment {id}"];
        var (status, created) = await owner.ActionAsync("projects.create", new { name = values[0], slug, description = values[1] });
        Assert.True(status == HttpStatusCode.OK, created.ToJsonString());
        var (_, task) = await owner.ActionAsync("tasks.create", new { projectSlug = slug, title = values[2] });
        var number = (int)task["result"]!["number"]!;
        await owner.ActionAsync("comments.add", new { projectSlug = slug, number, body = values[3] });
        var (_, board) = await owner.PageAsync($"/projects/{slug}");
        var keys = board["segments"]!.AsArray().Select(s => (string?)s!["key"]).OfType<string>().ToArray();
        Assert.Equal(2, keys.Length);
        return new Project(owner, slug, number, [ownerName, ownerEmail, .. values], keys);
    }

    /// <summary>A signed-in user with a project of their own, so their sidebar is not empty.</summary>
    private async Task<(Browser Browser, string Email)> StrangerAsync()
    {
        var stranger = app.NewBrowser();
        var email = await stranger.SignUpAsync("Stranger");
        await stranger.ActionAsync("projects.create", new { name = "Stranger's own", slug = ProjectsTests.NewSlug() });
        await stranger.PageAsync("/projects");
        return (stranger, email);
    }

    private static void AssertNoData(Project project, string answer)
    {
        foreach (var secret in project.Secrets)
        {
            Assert.DoesNotContain(secret, answer, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>A refused page's payload: the boundary page, without the project's layout or its seeded stats.</summary>
    private static void AssertRefusedPayload(JsonNode payload, string page)
    {
        Assert.Equal(page, (string?)payload["page"]);
        Assert.DoesNotContain(payload["segments"]!.AsArray(), s => (string?)s!["id"] == ProjectSegment);
        Assert.True(payload["queries"] is null || payload["queries"]!.AsArray().Count == 0, payload.ToJsonString());
    }

    private Task<string> SnapshotAsync(string slug) => app.WithDbAsync(async db =>
    {
        var project = await db.Projects.Where(p => p.Slug == slug).Select(p => new { p.Id, p.Name, p.Description, p.NextTaskNumber }).SingleAsync();
        var tasks = await db.Tasks.Where(t => t.ProjectId == project.Id).OrderBy(t => t.Number)
            .Select(t => new { t.Number, t.Title, t.Description, t.State, t.Priority, t.AssigneeId, t.UpdatedAt }).ToListAsync();
        var comments = await db.Comments.Where(c => c.Task.ProjectId == project.Id).OrderBy(c => c.CreatedAt).Select(c => c.Body).ToListAsync();
        var members = await db.Memberships.Where(m => m.ProjectId == project.Id).OrderBy(m => m.UserId).Select(m => new { m.UserId, m.Role }).ToListAsync();
        var activity = await db.Activity.CountAsync(a => a.ProjectId == project.Id);
        return JsonSerializer.Serialize(new { project, tasks, comments, members, activity });
    });

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task Strangers_AreForbidden_WithNoProjectData_OnDocumentsNavigationsAndHead(string page)
    {
        var project = await ProjectAsync();
        var (stranger, _) = await StrangerAsync();
        var url = project.Url(page);

        var document = await stranger.RequestPageAsync(url, data: false);
        var documentPayload = JsonNode.Parse(app.Renderer.Last)!;
        var navigation = await stranger.RequestPageAsync(url, data: true);
        var withOwnersKeys = await stranger.RequestPageAsync(url, data: true, heldSegments: project.OwnerKeys);
        var head = await stranger.RequestPageAsync(url, data: true, method: HttpMethod.Head);

        Assert.Equal(HttpStatusCode.Forbidden, document.Status);
        Assert.Equal(url, (string?)documentPayload["url"]);
        AssertRefusedPayload(documentPayload, "app/(app)/projects/forbidden.tsx");
        AssertNoData(project, app.Renderer.Last);
        foreach (var answer in new[] { navigation, withOwnersKeys })
        {
            Assert.Equal(HttpStatusCode.Forbidden, answer.Status);
            // A whole JSON payload: a refused task page streams no deferred activity after it.
            Assert.Equal("application/json", answer.ContentType);
            var payload = JsonNode.Parse(answer.Body)!;
            AssertRefusedPayload(payload, "app/(app)/projects/forbidden.tsx");
            // The owner's keys are not the stranger's: nothing is reused, the stranger's own sidebar is loaded.
            Assert.All(payload["segments"]!.AsArray(), s => Assert.Null(s!["reused"]));
            Assert.Contains("Stranger's own", answer.Body, StringComparison.Ordinal);
            AssertNoData(project, answer.Body);
        }

        // Kestrel sends no body for HEAD; TestServer passes what was written, which must carry nothing either.
        Assert.Equal(HttpStatusCode.Forbidden, head.Status);
        AssertNoData(project, head.Body);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task UnknownProjects_AreNotFound_OnDocumentsAndNavigations(string page)
    {
        var project = await ProjectAsync();
        var (stranger, _) = await StrangerAsync();
        var url = project.Url(page).Replace(project.Slug, ProjectsTests.NewSlug(), StringComparison.Ordinal);

        var document = await stranger.RequestPageAsync(url, data: false);
        var documentPayload = JsonNode.Parse(app.Renderer.Last)!;
        var navigation = await stranger.RequestPageAsync(url, data: true);

        Assert.Equal(HttpStatusCode.NotFound, document.Status);
        AssertRefusedPayload(documentPayload, "app/(app)/projects/not-found.tsx");
        Assert.Equal(HttpStatusCode.NotFound, navigation.Status);
        AssertRefusedPayload(JsonNode.Parse(navigation.Body)!, "app/(app)/projects/not-found.tsx");
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task SignedOutVisitors_AreAskedToSignIn_WithNoProjectData(string page)
    {
        var project = await ProjectAsync();
        var visitor = app.NewBrowser();
        await visitor.PageAsync("/");

        var document = await visitor.RequestPageAsync(project.Url(page), data: false);
        var documentPayload = app.Renderer.Last;
        var navigation = await visitor.RequestPageAsync(project.Url(page), data: true, heldSegments: project.OwnerKeys);

        Assert.Equal(HttpStatusCode.Unauthorized, document.Status);
        AssertNoData(project, documentPayload);
        Assert.Equal(HttpStatusCode.Unauthorized, navigation.Status);
        Assert.Equal("app/unauthorized.tsx", (string?)JsonNode.Parse(navigation.Body)!["page"]);
        AssertNoData(project, navigation.Body);
    }

    /// <summary>Other spellings of the slug reach the same project (or none), never past the membership check.</summary>
    [Theory]
    [InlineData("upper")]
    [InlineData("percent-encoded")]
    [InlineData("double-encoded")]
    [InlineData("trailing-slash")]
    public async Task OtherSpellingsOfTheSlug_AreRefusedToo(string spelling)
    {
        var project = await ProjectAsync();
        var (stranger, _) = await StrangerAsync();
        var slug = spelling switch
        {
            "upper" => project.Slug.ToUpperInvariant(),
            "percent-encoded" => $"%{(int)project.Slug[0]:X2}{project.Slug[1..]}",
            "double-encoded" => $"%25{(int)project.Slug[0]:X2}{project.Slug[1..]}",
            _ => project.Slug,
        };
        var suffix = spelling == "trailing-slash" ? "/" : "";

        foreach (var url in new[] { $"/projects/{slug}{suffix}", $"/projects/{slug}/settings{suffix}", $"/projects/{slug}/tasks/{project.Task}{suffix}" })
        {
            var navigation = await stranger.RequestPageAsync(url, data: true);

            Assert.True(navigation.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, $"{url}: {navigation.Status}");
            AssertNoData(project, navigation.Body);
        }

        var statsUrl = $"/api/projects/{slug}/stats{suffix}";
        var stats = await stranger.Http.GetAsync(statsUrl, TestContext.Current.CancellationToken);

        Assert.True(stats.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, $"{statsUrl}: {stats.StatusCode}");
        AssertNoData(project, await stats.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheStatsApi_IsNotFoundForUnknownProjects()
    {
        var (stranger, _) = await StrangerAsync();

        var response = await stranger.Http.GetAsync($"/api/projects/{ProjectsTests.NewSlug()}/stats", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public static TheoryData<string> ProjectActions => ["tasks.create", "tasks.move", "tasks.update", "comments.add", "projects.addMember"];

    /// <summary>What a stranger would send to each action to change another user's project (for addMember: themselves).</summary>
    private static (string Name, string Value)[] HostileInput(string action, string slug, int task, string strangerEmail) => action switch
    {
        "tasks.create" => [("projectSlug", slug), ("title", "Injected")],
        "tasks.move" => [("projectSlug", slug), ("number", $"{task}"), ("state", "done")],
        "tasks.update" => [("projectSlug", slug), ("number", $"{task}"), ("title", "Hijacked"), ("state", "done"), ("priority", "urgent")],
        "comments.add" => [("projectSlug", slug), ("number", $"{task}"), ("body", "Injected")],
        "projects.addMember" => [("projectSlug", slug), ("email", strangerEmail)],
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    [Theory]
    [MemberData(nameof(ProjectActions))]
    public async Task Strangers_CannotRunProjectActions_AsCalls_AndNothingChanges(string action)
    {
        var project = await ProjectAsync();
        var (stranger, email) = await StrangerAsync();
        var before = await SnapshotAsync(project.Slug);

        var (status, response) = await stranger.ActionAsync(action, HostileInput(action, project.Slug, project.Task, email));

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Null(response["result"]);
        Assert.Null(response["redirect"]);
        AssertNoData(project, response.ToJsonString());
        Assert.Equal(before, await SnapshotAsync(project.Slug));
    }

    [Theory]
    [MemberData(nameof(ProjectActions))]
    public async Task Strangers_CannotRunProjectActions_AsFormsWithoutJavaScript_AndNothingChanges(string action)
    {
        var project = await ProjectAsync();
        var (stranger, email) = await StrangerAsync();
        var before = await SnapshotAsync(project.Slug);
        var rendered = app.Renderer.LastOrNull;

        // The form a real page would post, returning to the project's own page.
        var response = await stranger.PostFormAsync(action, [.. HostileInput(action, project.Slug, project.Task, email), ("__netjes_return", project.Url("board"))]);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Headers.Location);
        AssertNoData(project, body);
        var after = app.Renderer.LastOrNull;
        if (after is not null && !ReferenceEquals(rendered, after))
        {
            AssertNoData(project, after);
        }

        Assert.Equal(before, await SnapshotAsync(project.Slug));
    }

    [Theory]
    [MemberData(nameof(ProjectActions))]
    public async Task ProjectActions_OnUnknownProjects_AreRefused(string action)
    {
        var (stranger, email) = await StrangerAsync();

        var (status, response) = await stranger.ActionAsync(action, HostileInput(action, ProjectsTests.NewSlug(), 1, email));

        Assert.True(status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, $"{status}: {response.ToJsonString()}");
        Assert.Null(response["result"]);
    }

    [Fact]
    public async Task ATaskNumber_IsLookedUpInTheProjectOfTheUrl_Only()
    {
        var project = await ProjectAsync();
        var member = app.NewBrowser();
        await member.SignUpAsync("Member elsewhere");
        var ownSlug = ProjectsTests.NewSlug();
        await member.ActionAsync("projects.create", new { name = "Own", slug = ownSlug });
        var before = await SnapshotAsync(project.Slug);
        // The other project's task number does not exist in the member's own project.
        Assert.True(project.Task >= 1);

        var (pageStatus, page) = await member.PageAsync($"/projects/{ownSlug}/tasks/{project.Task}");
        var (update, _) = await member.ActionAsync("tasks.update", new { projectSlug = ownSlug, number = project.Task, title = "Hijacked", state = "done", priority = "urgent" });
        var (comment, _) = await member.ActionAsync("comments.add", new { projectSlug = ownSlug, number = project.Task, body = "Injected" });
        var (move, _) = await member.ActionAsync("tasks.move", new { projectSlug = ownSlug, number = project.Task, state = "done" });

        Assert.Equal(HttpStatusCode.NotFound, pageStatus);
        Assert.Equal("app/(app)/projects/[id]/not-found.tsx", (string?)page["page"]);
        AssertNoData(project, page.ToJsonString());
        Assert.Equal(HttpStatusCode.NotFound, update);
        Assert.Equal(HttpStatusCode.NotFound, comment);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, move);
        Assert.Equal(before, await SnapshotAsync(project.Slug));
    }

    [Fact]
    public async Task OnlyMembersOfTheProject_CanBeAssigned_NotUsersFromOtherProjects()
    {
        var project = await ProjectAsync();
        var (stranger, _) = await StrangerAsync();
        var (_, strangersPage) = await stranger.PageAsync("/projects");
        var strangerId = (string)strangersPage["shared"]!["user"]!["id"]!;
        var before = await SnapshotAsync(project.Slug);

        var (status, response) = await project.Owner.ActionAsync("tasks.update", new
        {
            projectSlug = project.Slug, number = project.Task, title = "Reassigned", state = "todo", priority = "none", assigneeId = strangerId,
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.NotNull(response["errors"]!["assigneeId"]);
        Assert.Equal(before, await SnapshotAsync(project.Slug));
    }

    /// <summary>
    /// A member removed while their browser holds the project's layout: the page loaders check membership themselves,
    /// so reusing the layout's props (whose loader does not run again) lets nothing through.
    /// </summary>
    [Theory]
    [MemberData(nameof(Pages))]
    public async Task ARemovedMember_HoldingTheProjectLayout_IsForbidden_AndGetsNoData(string page)
    {
        var project = await ProjectAsync();
        var member = app.NewBrowser();
        var email = await member.SignUpAsync("Soon removed");
        await project.Owner.ActionAsync("projects.addMember", new { projectSlug = project.Slug, email });
        var (_, board) = await member.PageAsync(project.Url("board"));
        var held = board["segments"]!.AsArray().Select(s => (string?)s!["key"]).OfType<string>().ToArray();
        var reused = await member.RequestPageAsync(project.Url(page), data: true, heldSegments: held);
        Assert.Equal(HttpStatusCode.OK, reused.Status);
        Assert.Contains(JsonNode.Parse(reused.Body.Split('\n')[0])!["segments"]!.AsArray(), s => (string?)s!["id"] == ProjectSegment && (bool?)s["reused"] == true);
        await app.WithDbAsync(db => db.Memberships
            .Where(m => m.Project.Slug == project.Slug && m.User.Email == email)
            .ExecuteDeleteAsync(TestContext.Current.CancellationToken));

        var answer = await member.RequestPageAsync(project.Url(page), data: true, heldSegments: held);

        Assert.Equal(HttpStatusCode.Forbidden, answer.Status);
        Assert.Equal("application/json", answer.ContentType);
        AssertRefusedPayload(JsonNode.Parse(answer.Body)!, "app/(app)/projects/forbidden.tsx");
        AssertNoData(project, answer.Body);
    }
}
