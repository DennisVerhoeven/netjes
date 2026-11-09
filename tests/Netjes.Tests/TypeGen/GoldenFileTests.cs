using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Netjes.Tests.Endpoints;
using Netjes.TypeGen;
using static Netjes.Tests.TypeGen.ZodSchemasTests;
using EdgeCase = Netjes.Tests.TypeGen.ZodSchemasEdgeCaseTests;

namespace Netjes.Tests.TypeGen;

/// <summary>
/// Pins the generated TypeScript and JSON that System.Text.Json writes for the same types. The netjes package's
/// Vitest suite typechecks that TypeScript and parses the JSON with it (packages/netjes/test/typegen.test.ts).
/// Set UPDATE_GOLDEN=1 to rewrite the files after an intended change.
/// </summary>
public class GoldenFileTests
{
    private static readonly string Fixtures = Path.Combine(RepoRoot(), "packages", "netjes", "test", "fixtures", "typegen");

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

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Schemas_MatchTheGoldenFile()
    {
        var schemas = new ZodSchemas(Web);
        schemas.Add(typeof(Post));
        schemas.Add(typeof(Category));
        schemas.Add(typeof(Shape));
        schemas.Add(typeof(Paged<Author>));
        var output = "import { z } from 'zod';\n\n" + schemas.Declarations();

        AssertGolden("schemas.generated.ts", output);
    }

    [Fact]
    public void Samples_MatchTheGoldenFile()
    {
        var author = new Author("Ada", null);
        var samples = new Dictionary<string, object>
        {
            ["post"] = new Post(
                "Hello", null, 3, 4.5, true, new DateOnly(2026, 10, 28), new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.FromHours(2)),
                new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Unspecified), new TimeOnly(9, 30), Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"),
                new Uri("https://example.com/a"), Status.InReview, author, ["a", "b"], [author], new() { ["x"] = 1 }, [1, 2, 3], null,
                JsonDocument.Parse("""{"any":true}""").RootElement, "orig", "hidden", null),
            ["category"] = new Category("root", [new Category("child", [], null)], null),
            ["shape"] = (Shape)new Circle("red", 2),
            ["paged"] = new Paged<Author>([author], 1),
        };

        // Indented JSON breaks lines with Environment.NewLine unless told otherwise, and the golden file has LF everywhere.
        AssertGolden("samples.json", JsonSerializer.Serialize<object>(samples, new JsonSerializerOptions(Web) { WriteIndented = true, NewLine = "\n" }) + "\n");
    }

    private static readonly JsonSerializerOptions NamedFloats = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals | JsonNumberHandling.WriteAsString,
    };

    /// <summary>The generator's edge cases from ZodSchemasEdgeCaseTests, whose schemas must load and parse what System.Text.Json writes.</summary>
    [Fact]
    public void EdgeCaseSchemas_MatchTheGoldenFiles()
    {
        var schemas = new ZodSchemas(Web);
        foreach (var type in new[]
        {
            typeof(EdgeCase.Shape), typeof(EdgeCase.Pet), typeof(EdgeCase.Comment), typeof(EdgeCase.Invoice), typeof(EdgeCase.Counters),
            typeof(EdgeCase.Precise), typeof(EdgeCase.UsesGlobals),
        })
        {
            schemas.Add(type);
        }

        AssertGolden("edge-cases.generated.ts", "import { z } from 'zod';\n\n" + schemas.Declarations());

        var floats = new ZodSchemas(NamedFloats);
        floats.Add(typeof(EdgeCase.Measurements));
        AssertGolden("named-floats.generated.ts", "import { z } from 'zod';\n\n" + floats.Declarations());
    }

    [Fact]
    public void EdgeCaseSamples_MatchTheGoldenFile()
    {
        var reply = new EdgeCase.Comment("b", []);
        var samples = new Dictionary<string, string>
        {
            ["shapeBase"] = JsonSerializer.Serialize(new EdgeCase.Shape { Name = "b" }, Web),
            ["shapeCircle"] = JsonSerializer.Serialize<EdgeCase.Shape>(new EdgeCase.Circle { Name = "c", R = 1 }, Web),
            ["petDog"] = JsonSerializer.Serialize<EdgeCase.Pet>(new EdgeCase.Dog2 { Name = "d" }, Web),
            ["petPuppy"] = JsonSerializer.Serialize<EdgeCase.Pet>(new EdgeCase.Puppy { Name = "p", Age = 1 }, Web),
            ["comment"] = JsonSerializer.Serialize(new EdgeCase.Comment("a", [reply]), Web),
            ["pinned"] = JsonSerializer.Serialize<EdgeCase.Comment>(new EdgeCase.PinnedComment("p", [reply]), Web),
            ["invoice"] = JsonSerializer.Serialize(new EdgeCase.Invoice(3, 1.5m), Web),
            ["counters"] = JsonSerializer.Serialize(new EdgeCase.Counters([1, 2], new() { ["a"] = 9007199254740993, ["b"] = null }, [new EdgeCase.Owner("o")]), Web),
            ["precise"] = JsonSerializer.Serialize(new EdgeCase.Precise((Half)1.5, (Half)2), Web),
            ["usesGlobals"] = JsonSerializer.Serialize(new EdgeCase.UsesGlobals(new("a"), new("b"), new("c"), new(4)), Web),
            ["measurements"] = JsonSerializer.Serialize(new EdgeCase.Measurements(double.NaN, float.NegativeInfinity, 2.5m, 4), NamedFloats),
        };
        var json = "{\n" + string.Join(",\n", samples.Select(s => $"  {JsonSerializer.Serialize(s.Key)}: {s.Value}")) + "\n}\n";

        AssertGolden("edge-case-samples.json", json);
    }

    public enum TaskState
    {
        Open,
        InProgress,
    }

    public sealed record LikeCount(string Slug, int Likes);

    public sealed record LikeRequest(string Slug);

    /// <summary>
    /// The typed URLs of endpoints with each kind of route value, which packages/netjes/test/typegen-urls.test.ts calls
    /// and typechecks against buildEndpointUrl.
    /// </summary>
    [Fact]
    public async Task EndpointUrls_MatchTheGoldenFile()
    {
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx", "app/docs/[...path]/page.tsx"],
            map: a =>
            {
                var likes = a.MapGroup("/api/posts/{slug}/likes");
                likes.MapGet("/", (string slug) => TypedResults.Ok(new LikeCount(slug, 1)));
                likes.MapPost("/", (string slug, LikeRequest request) => TypedResults.Ok(new LikeCount(slug, 2)));
                a.MapGet("/api/search", (string? q, int? take, TaskState? state, string[] tag, int page = 1) => TypedResults.Ok(new LikeCount(q ?? "", page)));
                a.MapMethods("/docs/{**path:regex(\\.md$)}", [HttpMethods.Get, HttpMethods.Head], (string path) => Results.Text(path)).WithOrder(-1);
                a.MapGet("/files/{*name}", (string name) => Results.Text(name));
                a.MapGet("/opt/{id?}", (int? id) => Results.Text($"{id}"));
                a.MapGet("/list/{page=1}", (int page) => Results.Text($"{page}"));
                a.MapGet("/openapi/{documentName}.json", (string documentName) => Results.Text(documentName));
                a.MapGet("/feed.xml", http => http.Response.WriteAsync("<rss/>"));
            });
        var endpoints = app.App.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

        var output = new StringBuilder("import { buildEndpointUrl, type SearchParamsInput } from '../../../src/href.ts';\n");
        EndpointUrls.Append(output, EndpointUrls.Entries(endpoints, TypeScriptGenerator.IsInTheApiMap));

        AssertGolden("urls.generated.ts", output.ToString());
    }

    /// <summary>
    /// An app that mounts a page package, and the package's own file, which packages/netjes/test/typegen-mounted.test.ts
    /// type-checks together in one program and calls, with an export the package adds to both.
    /// </summary>
    [Fact]
    public async Task PagePackageFiles_MatchTheGoldenFiles()
    {
        await using var app = await PagePackageTypesTests.StartAsync(
            ["app/layout.tsx", "app/page.tsx", "app/help/(center)/shortcuts/page.tsx"],
            map: a => a.MapPageLoader("/", () => Page.Props(new PagePackageTypesTests.AppHomeProps("Hi"))),
            mount: PagePackageTypesTests.MapWithBlocks);
        var generator = app.App.Services.GetRequiredService<TypeScriptGenerator>();
        var mounted = app.App.Services.GetRequiredService<Netjes.Routing.RouteSources>().Mounts.Single();

        AssertGolden(Path.Combine("mounted", "app.generated.ts"), generator.Generate());
        AssertGolden(Path.Combine("mounted", "package.generated.ts"), generator.Generate(mounted));
    }

    private static void AssertGolden(string name, string actual)
    {
        var path = Path.Combine(Fixtures, name);
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
        }

        Assert.True(File.Exists(path), $"{path} is missing; run with UPDATE_GOLDEN=1.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }
}
