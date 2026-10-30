using System.Net;
using System.Text.Json.Nodes;

namespace TaskBoard.Tests;

/// <summary>
/// One browser, two users: the first signs out (or the second signs in over their session) while the tab keeps its
/// layouts mounted, so its next client navigation still sends the first user's layout keys. The keys name the user, so
/// the server must load every layout again for the second user and never hand back the first user's projects. The
/// browser half (history entries and mounted layouts in a real tab) is in examples/TaskBoard/e2e/user-change.spec.ts.
/// <list type="bullet">
///   <item>layout app/(app): the sidebar of projects, reused while the user stays the same.</item>
///   <item>layout /projects/[id]: the project header and stats, reused within one project.</item>
///   <item>page /projects and the project pages below the layouts.</item>
/// </list>
/// </summary>
[Collection(TaskBoardCollection.Name)]
[Trait("Category", "Docker")]
public sealed class UserChangeTests(TaskBoardApp app)
{
    private const string ShellSegment = "app/(app)";

    private static JsonNode Segment(JsonNode payload, string id) => payload["segments"]!.AsArray().Single(s => (string?)s!["id"] == id)!;

    private static string[] Keys(JsonNode payload) => [.. payload["segments"]!.AsArray().Select(s => (string?)s!["key"]).OfType<string>()];

    public static TheoryData<bool> SignsOutFirst => [true, false];

    [Theory]
    [MemberData(nameof(SignsOutFirst))]
    public async Task AnotherUserInTheSameBrowser_GetsTheirOwnLayouts_NotTheHeldOnes(bool signsOutFirst)
    {
        var second = app.NewBrowser();
        var secondEmail = await second.SignUpAsync("Second user");
        var secondSlug = ProjectsTests.NewSlug();
        await second.ActionAsync("projects.create", new { name = "Second's project", slug = secondSlug });

        var tab = app.NewBrowser();
        await tab.SignUpAsync("First user");
        var firstSlug = ProjectsTests.NewSlug();
        await tab.ActionAsync("projects.create", new { name = "First's private project", slug = firstSlug });
        var (_, firstBoard) = await tab.PageAsync($"/projects/{firstSlug}");
        var firstKeys = Keys(firstBoard);
        var (_, firstProjects) = await tab.PageAsync("/projects");
        // While the user stays the same, the held layouts are reused: what the rest of the test relies on not happening.
        var sameUser = await tab.RequestPageAsync("/projects", data: true, heldSegments: Keys(firstProjects));
        Assert.True((bool?)Segment(JsonNode.Parse(sameUser.Body)!, ShellSegment)["reused"]);

        if (signsOutFirst)
        {
            var (_, signedOut) = await tab.ActionAsync("auth.logout", new { });
            Assert.Equal("/", (string?)signedOut["redirect"]);
            await tab.PageAsync("/login");
        }

        var (status, signedIn) = await tab.ActionAsync("auth.login", new { email = secondEmail, password = "correct horse battery" });
        Assert.True(status == HttpStatusCode.OK, signedIn.ToJsonString());

        var projects = await tab.RequestPageAsync("/projects", data: true, heldSegments: firstKeys);
        var board = await tab.RequestPageAsync($"/projects/{firstSlug}", data: true, heldSegments: firstKeys);

        Assert.Equal(HttpStatusCode.OK, projects.Status);
        var payload = JsonNode.Parse(projects.Body)!;
        Assert.Equal("Second user", (string?)payload["shared"]!["user"]!["name"]);
        var shell = Segment(payload, ShellSegment);
        Assert.Null(shell["reused"]);
        Assert.Equal([secondSlug], shell["props"]!["projects"]!.AsArray().Select(p => (string?)p!["slug"]));
        Assert.DoesNotContain("First", projects.Body, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.Forbidden, board.Status);
        var refused = JsonNode.Parse(board.Body)!;
        Assert.Equal("app/(app)/projects/forbidden.tsx", (string?)refused["page"]);
        Assert.All(refused["segments"]!.AsArray(), s => Assert.Null(s!["reused"]));
        Assert.DoesNotContain("First", board.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SigningOut_WhileHoldingTheLayouts_AsksToSignIn_WithNothingOfTheUser()
    {
        var tab = app.NewBrowser();
        await tab.SignUpAsync("Leaving user");
        var slug = ProjectsTests.NewSlug();
        await tab.ActionAsync("projects.create", new { name = "Left behind", slug });
        var (_, board) = await tab.PageAsync($"/projects/{slug}");

        await tab.ActionAsync("auth.logout", new { });
        var answer = await tab.RequestPageAsync($"/projects/{slug}", data: true, heldSegments: Keys(board));

        Assert.Equal(HttpStatusCode.Unauthorized, answer.Status);
        var payload = JsonNode.Parse(answer.Body)!;
        Assert.Equal("app/unauthorized.tsx", (string?)payload["page"]);
        Assert.Null(payload["shared"]!["user"]);
        Assert.DoesNotContain("Left behind", answer.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Leaving user", answer.Body, StringComparison.Ordinal);
    }
}
