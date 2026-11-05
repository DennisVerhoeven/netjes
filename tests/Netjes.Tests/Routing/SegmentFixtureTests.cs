using System.Text.Json;
using System.Text.Json.Nodes;
using Netjes.Tests.Endpoints;

namespace Netjes.Tests.Routing;

/// <summary>
/// The route and segments the server sends with a page's document, for each case in
/// packages/netjes/test/fixtures/segments/cases.json. <c>renderDocument</c> in netjes/testing builds that part of the
/// payload in TypeScript, and packages/netjes/test/render-document.test.tsx checks it against the same cases: a change
/// to either rule fails one of the two suites until the fixture says what both send.
/// </summary>
public class SegmentFixtureTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static TheoryData<string> Cases => new(Load().Select(c => c.Name));

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TheServer_SendsTheSegmentsTheSharedFixtureExpects(string name)
    {
        var expected = Load().Single(c => c.Name == name);
        await using var app = await TestApp.StartAsync(expected.Files, configure: o => o.AppDirectory = expected.AppDir ?? "app");

        await app.GetDocumentAsync(expected.Url);

        var payload = app.Renderer.LastPayload;
        Assert.Equal(expected.Page, (string?)payload["page"]);
        Assert.Equal(expected.Route, (string?)payload["route"]);
        Assert.Equal(expected.PageKind, (string?)payload["pageKind"]);
        Assert.Equal(expected.Status, (int?)payload["status"]);
        AssertSameJson("params", expected.Params, payload["params"]);
        AssertSameJson("segments", expected.Segments, payload["segments"]);
    }

    private static void AssertSameJson(string what, JsonNode expected, JsonNode? actual) =>
        Assert.True(
            JsonNode.DeepEquals(expected, actual),
            $"The fixture's {what} are {expected.ToJsonString()}; the server sent {actual?.ToJsonString() ?? "none"}.");

    private static IReadOnlyList<SegmentCase> Load()
    {
        var path = Path.Combine(RepoRoot(), "packages", "netjes", "test", "fixtures", "segments", "cases.json");
        return JsonSerializer.Deserialize<SegmentCases>(File.ReadAllText(path), Web)!.Cases;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Netjes.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    private sealed record SegmentCases(IReadOnlyList<SegmentCase> Cases);

    private sealed record SegmentCase(
        string Name,
        string? AppDir,
        IReadOnlyList<string> Files,
        string Url,
        string Page,
        JsonObject Params,
        string? Route,
        string? PageKind,
        int Status,
        JsonArray Segments);
}
