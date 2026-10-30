using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Netjes.Routing;
using Netjes.Tests.Endpoints;

namespace Netjes.Tests.Routing;

/// <summary>
/// Files in app/ that break the conventions stop the app at startup, with an exception a test can catch by type. A
/// development rescan that finds one logs it and keeps the previous routes.
/// </summary>
public class RouteErrorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Development", "app/a/page.tsx", "app/(g)/a/page.tsx")]
    [InlineData("Development", "app/page.tsx", "app/page.jsx")]
    [InlineData("Production", "app/a/page.tsx", "app/(g)/a/page.tsx")]
    [InlineData("Production", "app/page.tsx", "app/page.jsx")]
    public async Task ConflictingFiles_FailStartup_NamingBoth(string environment, string one, string another)
    {
        var ex = await Assert.ThrowsAsync<RouteConflictException>(() => StartAsync(environment, [one, another]));

        Assert.Contains($"'{one}'", ex.Message);
        Assert.Contains($"'{another}'", ex.Message);
    }

    [Theory]
    [InlineData("Development", "app/[...a]/b/page.tsx")]
    [InlineData("Development", "app/blog/global-error.tsx")]
    [InlineData("Development", "app/api/route.ts")]
    // vite build stops on a route.ts (plugin.test.ts), so a production build cannot hold one.
    [InlineData("Production", "app/[...a]/b/page.tsx")]
    [InlineData("Production", "app/blog/global-error.tsx")]
    public async Task InvalidFileName_FailsStartup_NamingTheFile(string environment, string file)
    {
        var ex = await Assert.ThrowsAsync<InvalidPageFileException>(() => StartAsync(environment, ["app/page.tsx", file]));

        Assert.Contains($"'{file}'", ex.Message);
    }

    [Fact]
    public async Task Development_ConflictAddedWhileRunning_IsLoggedAndThePreviousRoutesAnswer()
    {
        await using var app = await TestApp.StartAsync(["app/page.tsx", "app/a/page.tsx"]);

        // A file in a folder the watcher already follows, so one change brings in the conflict.
        await File.WriteAllTextAsync(Path.Combine(app.Root, "app", "a", "page.jsx"), "export default () => null", Ct);
        LogEntry? error = null;
        for (var attempt = 0; attempt < 100 && error is null; attempt++)
        {
            await Task.Delay(100, Ct);
            error = app.Logs.FirstOrDefault(e => e.Category == typeof(FileSystemRouteTableProvider).FullName && e.Level == LogLevel.Error);
        }

        Assert.NotNull(error);
        Assert.Contains("Keeping the previous routes", error.Message);
        Assert.Contains("'app/a/page.jsx' and 'app/a/page.tsx'", error.Message);
        Assert.Equal("app/a/page.tsx", (string?)(await app.GetDataJsonAsync("/a"))["page"]);
    }

    // Development scans app/; production reads the page files from the client build's manifest, as Vite lists them.
    private static Task<TestApp> StartAsync(string environment, string[] files) => environment == "Production"
        ? TestApp.StartAsync([], environment: environment, prepareRoot: root => WriteBuild(root, files))
        : TestApp.StartAsync(files, environment: environment);

    private static void WriteBuild(string root, string[] files)
    {
        var manifest = new JsonObject
        {
            ["node_modules/netjes/dist/client-entry.js"] = new JsonObject
            {
                ["file"] = "assets/client-entry.js",
                ["src"] = "node_modules/netjes/dist/client-entry.js",
                ["isEntry"] = true,
            },
        };
        for (var i = 0; i < files.Length; i++)
        {
            manifest[files[i]] = new JsonObject { ["file"] = $"assets/module-{i}.js", ["src"] = files[i], ["isDynamicEntry"] = true };
        }

        var vite = Path.Combine(root, "dist", "client", ".vite");
        Directory.CreateDirectory(vite);
        File.WriteAllText(Path.Combine(vite, "manifest.json"), manifest.ToJsonString());
    }
}
