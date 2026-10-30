using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netjes.Tests.Endpoints;
using Netjes.TypeGen;

namespace Netjes.Tests.TypeGen;

public class TypeGenHostedServiceTests
{
    public sealed record HomeProps(string Greeting);

    private static string TypesPath(TestApp app) => Path.Combine(app.Root, "generated", "netjes.ts");

    private static string Expected(TestApp app) => app.App.Services.GetRequiredService<TypeScriptGenerator>().Generate();

    private static Action<WebApplication> HomeLoader => a => a.MapPageLoader("/", () => Page.Props(new HomeProps("hi")));

    [Fact]
    public async Task Development_Startup_WritesTheTypesFile()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"], map: HomeLoader);

        Assert.Equal(Expected(app), await File.ReadAllTextAsync(TypesPath(app), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Development_UnchangedTypes_AreNotRewritten()
    {
        string content;
        await using (var first = await TestApp.StartAsync(["app/page.tsx"], map: HomeLoader))
        {
            content = Expected(first);
        }

        var past = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await using var app = await TestApp.StartAsync(["app/page.tsx"], map: HomeLoader, prepareRoot: root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "generated"));
            File.WriteAllText(Path.Combine(root, "generated", "netjes.ts"), content);
            File.SetLastWriteTimeUtc(Path.Combine(root, "generated", "netjes.ts"), past);
        });

        Assert.Equal(past, File.GetLastWriteTimeUtc(TypesPath(app)));
    }

    private static async Task<string> ExpectedContentAsync()
    {
        await using var reference = await TestApp.StartAsync(["app/page.tsx"], map: HomeLoader, configure: o => o.TypesFile = null);
        return Expected(reference);
    }

    [Fact]
    public async Task Development_TheSameTypesWithWindowsLineEndings_AreNotRewritten()
    {
        // What git checks out with core.autocrlf.
        var crlf = (await ExpectedContentAsync()).Replace("\n", "\r\n");
        var past = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await using var app = await TestApp.StartAsync(["app/page.tsx"], map: HomeLoader, prepareRoot: root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "generated"));
            File.WriteAllText(Path.Combine(root, "generated", "netjes.ts"), crlf);
            File.SetLastWriteTimeUtc(Path.Combine(root, "generated", "netjes.ts"), past);
        });

        Assert.Equal(past, File.GetLastWriteTimeUtc(TypesPath(app)));
    }

    [Fact]
    public async Task GenerateCheck_TheSameTypesWithWindowsLineEndings_AreUpToDate()
    {
        int? exitCode = null;
        var crlf = (await ExpectedContentAsync()).Replace("\n", "\r\n");
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: HomeLoader,
            configure: o => o.Command = new NetjesCommandOptions { Name = "generate", Check = true },
            services: s => s.AddSingleton(new CommandExit(code => exitCode = code)),
            prepareRoot: root =>
            {
                Directory.CreateDirectory(Path.Combine(root, "generated"));
                File.WriteAllText(Path.Combine(root, "generated", "netjes.ts"), crlf);
            });

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Development_Rewrite_LeavesNoTemporaryFiles()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"], map: HomeLoader, prepareRoot: root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "generated"));
            File.WriteAllText(Path.Combine(root, "generated", "netjes.ts"), "// stale\n");
        });

        Assert.Equal(Expected(app), await File.ReadAllTextAsync(TypesPath(app), TestContext.Current.CancellationToken));
        Assert.Equal(["netjes.ts"], Directory.GetFiles(Path.Combine(app.Root, "generated")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task TypesFileNull_WritesNothing()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx"], configure: o => o.TypesFile = null);

        Assert.False(File.Exists(TypesPath(app)));
    }

    [Fact]
    public async Task Production_WritesNothing()
    {
        await using var app = await TestApp.StartAsync([], environment: "Production", prepareRoot: ModeTests.WriteProductionBuild);

        Assert.False(File.Exists(TypesPath(app)));
    }

    [Fact]
    public async Task GenerateCommand_WritesTheFileAndStopsTheApp()
    {
        int? exitCode = null;
        var output = Path.Combine(Path.GetTempPath(), $"netjes-{Guid.NewGuid():N}.ts");
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: HomeLoader,
            configure: o => o.Command = new NetjesCommandOptions { Name = "generate", Output = output },
            services: s => s.AddSingleton(new CommandExit(code => exitCode = code)));

        Assert.Equal(Expected(app), await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
        Assert.Equal(0, exitCode);
        Assert.True(app.App.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        Assert.False(File.Exists(TypesPath(app)));
        File.Delete(output);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task GenerateCheck_ExitsNonZeroWhenTheFileIsStale(bool current, int expectedExit)
    {
        int? exitCode = null;
        string content;
        await using (var reference = await TestApp.StartAsync(["app/page.tsx"], map: HomeLoader, configure: o => o.TypesFile = null))
        {
            content = Expected(reference);
        }

        var written = current ? content : "// stale\n";
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            map: HomeLoader,
            configure: o => o.Command = new NetjesCommandOptions { Name = "generate", Check = true },
            services: s => s.AddSingleton(new CommandExit(code => exitCode = code)),
            prepareRoot: root =>
            {
                Directory.CreateDirectory(Path.Combine(root, "generated"));
                File.WriteAllText(Path.Combine(root, "generated", "netjes.ts"), written);
            });

        Assert.Equal(expectedExit, exitCode);
        Assert.Equal(written, await File.ReadAllTextAsync(TypesPath(app), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GenerateCheck_StaleFile_ShowsWhereItDiffers()
    {
        var expected = (await ExpectedContentAsync()).ReplaceLineEndings("\n").Split('\n');
        var lines = expected.ToArray();
        lines[2] = "// edited by hand";
        await using var app = await CheckAsync(string.Join("\n", lines));

        var error = Assert.Single(app.Logs, l => l.Level == Microsoft.Extensions.Logging.LogLevel.Error).Message;
        Assert.Contains("is out of date; run `netjes generate`", error);
        Assert.Contains("- 3: // edited by hand", error);
        Assert.Contains($"+ 3: {expected[2]}", error);
        Assert.DoesNotContain("4:", error);
        Assert.Equal(1, _checkExit);
    }

    [Fact]
    public async Task GenerateCheck_TwoChangesFarApart_ListsOnlyTheFirstAndSaysThereIsMore()
    {
        var expected = (await ExpectedContentAsync()).ReplaceLineEndings("\n").Split('\n');
        var lines = expected.ToArray();
        lines[2] = "// first edit";
        lines[^5] = "// second edit";
        await using var app = await CheckAsync(string.Join("\n", lines));

        var error = Assert.Single(app.Logs, l => l.Level == Microsoft.Extensions.Logging.LogLevel.Error).Message;
        Assert.Contains("- 3: // first edit", error);
        Assert.Contains($"+ 3: {expected[2]}", error);
        Assert.DoesNotContain("second edit", error);
        Assert.DoesNotContain($": {expected[10]}", error);
        Assert.Contains($"differs further down, up to line {expected.Length - 4}", error);
        Assert.DoesNotContain("more differing lines", error);
    }

    [Fact]
    public async Task GenerateCheck_MissingFinalNewline_SaysSo()
    {
        var expected = await ExpectedContentAsync();
        await using var app = await CheckAsync(expected.ReplaceLineEndings("\n").TrimEnd('\n'));

        var error = Assert.Single(app.Logs, l => l.Level == Microsoft.Extensions.Logging.LogLevel.Error).Message;
        Assert.Contains("(no newline at end of file)", error);
    }

    [Fact]
    public async Task GenerateCheck_FileOnlyShrank_UsesTheWholeCap()
    {
        var expected = (await ExpectedContentAsync()).ReplaceLineEndings("\n").Split('\n');
        var stale = string.Join("\n", expected.Take(2).Concat(expected.Skip(2 + 30)));
        await using var app = await CheckAsync(stale);

        var error = Assert.Single(app.Logs, l => l.Level == Microsoft.Extensions.Logging.LogLevel.Error).Message;
        Assert.Contains("+ 22: ", error);
        Assert.DoesNotContain("+ 23: ", error);
    }

    [Fact]
    public async Task GenerateCheck_StaleFile_CapsTheExcerptAndCountsTheRest()
    {
        var expected = (await ExpectedContentAsync()).ReplaceLineEndings("\n").Split('\n');
        var stale = string.Join("\n", expected.Take(2).Concat(Enumerable.Range(0, 100).Select(i => $"// old {i}")));
        await using var app = await CheckAsync(stale);

        var error = Assert.Single(app.Logs, l => l.Level == Microsoft.Extensions.Logging.LogLevel.Error).Message;
        Assert.True(error.Split('\n').Length <= 25, error);
        Assert.Contains("- 3: // old 0", error);
        Assert.Contains("more", error);
        Assert.Equal(1, _checkExit);
    }

    [Fact]
    public async Task GenerateCheck_MissingFile_SaysItDoesNotExist()
    {
        await using var app = await CheckAsync(null);

        var error = Assert.Single(app.Logs, l => l.Level == Microsoft.Extensions.Logging.LogLevel.Error).Message;
        Assert.Contains("does not exist; run `netjes generate`", error);
        Assert.Equal(1, _checkExit);
    }

    private int _checkExit = -1;

    private Task<TestApp> CheckAsync(string? written) => TestApp.StartAsync(
        ["app/page.tsx"],
        map: HomeLoader,
        configure: o => o.Command = new NetjesCommandOptions { Name = "generate", Check = true },
        services: s => s.AddSingleton(new CommandExit(code => _checkExit = code)),
        prepareRoot: root =>
        {
            if (written is null)
            {
                return;
            }

            Directory.CreateDirectory(Path.Combine(root, "generated"));
            File.WriteAllText(Path.Combine(root, "generated", "netjes.ts"), written);
        });

    [Fact]
    public async Task RoutesCommand_WritesTheRouteTableAsJson()
    {
        int? exitCode = null;
        var output = Path.Combine(Path.GetTempPath(), $"netjes-{Guid.NewGuid():N}.json");
        await using var app = await TestApp.StartAsync(
            ["app/layout.tsx", "app/page.tsx", "app/blog/[slug]/page.tsx"],
            map: a =>
            {
                HomeLoader(a);
                a.MapPageLoader("/blog/[slug]", (string slug) => Page.Props(new HomeProps(slug))).CachePage(TimeSpan.FromMinutes(1));
                a.MapPageAction("/blog/[slug]", () => Page.Redirect("/"));
            },
            configure: o => o.Command = new NetjesCommandOptions { Name = "routes", Output = output },
            services: s => s.AddSingleton(new CommandExit(code => exitCode = code)).AddOutputCache());

        var text = await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken);
        var json = JsonNode.Parse(text)!;
        var blog = json["routes"]!.AsArray().Single(r => (string?)r!["pattern"] == "/blog/[slug]")!;

        Assert.Equal(0, exitCode);
        // The same bytes on every system, as netjes routes --json prints them.
        Assert.DoesNotContain('\r', text);
        Assert.Equal("/blog/{slug}", (string?)blog["template"]);
        Assert.Equal("app/blog/[slug]/page.tsx", (string?)blog["file"]);
        Assert.Equal(["app/layout.tsx"], blog["layouts"]!.AsArray().Select(n => (string?)n));
        Assert.True((bool?)blog["loader"]);
        Assert.True((bool?)blog["action"]);
        Assert.True((bool?)blog["cached"]);
        Assert.Equal("HomeProps", (string?)blog["props"]);
        Assert.False((bool?)json["routes"]![0]!["action"]);
        File.Delete(output);
    }

    [Fact]
    public async Task GenerateCommand_InProduction_ScansPageFilesInsteadOfTheBuildManifest()
    {
        int? exitCode = null;
        var output = Path.Combine(Path.GetTempPath(), $"netjes-{Guid.NewGuid():N}.ts");
        await using var app = await TestApp.StartAsync(
            ["app/about/page.tsx"],
            environment: "Production",
            configure: o => o.Command = new NetjesCommandOptions { Name = "generate", Output = output },
            services: s => s.AddSingleton(new CommandExit(code => exitCode = code)));

        Assert.Equal(0, exitCode);
        Assert.Contains("\"/about\": z.object({})", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
        File.Delete(output);
    }

    [Fact]
    public async Task UnknownCommand_ExitsWithAnError()
    {
        int? exitCode = null;
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configure: o => o.Command = new NetjesCommandOptions { Name = "nope" },
            services: s => s.AddSingleton(new CommandExit(code => exitCode = code)));

        Assert.Equal(2, exitCode);
        Assert.Contains(app.Logs, l => l.Message.Contains("Unknown Netjes command 'nope'"));
    }
}
