using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Netjes.Tests.Sidecar;

/// <summary>Pages with deferred values, rendered through the real pool and a Node process (not a fake renderer).</summary>
public class PoolRenderingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Document_WithAnAlreadyCompletedDeferredValue_GetsItRightAway(int processes)
    {
        var (app, root) = await StartAsync("deferred-sidecar.mjs", processes);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var html = await app.GetTestClient().GetStringAsync("/", Ct);

        Assert.Equal("<html>shell d0=42</html>", html);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"took {watch.Elapsed}");
        await app.DisposeAsync();
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Document_WhoseSidecarStopsAnsweringResolves_EndsWithTheStreamTimeout()
    {
        var (app, root) = await StartAsync("hung-resolve-sidecar.mjs", processes: 1);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var html = await app.GetTestClient().GetStringAsync("/", Ct).WaitAsync(TimeSpan.FromSeconds(15), Ct);

        Assert.Equal("<html>shell d0=TIMED-OUT</html>", html);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(6), $"took {watch.Elapsed}");
        await app.DisposeAsync();
        Directory.Delete(root, recursive: true);
    }

    private static async Task<(WebApplication App, string Root)> StartAsync(string sidecar, int processes)
    {
        var root = Directory.CreateTempSubdirectory("netjes-pool-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "app"));
        await File.WriteAllTextAsync(Path.Combine(root, "app", "page.tsx"), "export default function Page() { return null }", Ct);
        // What `npm run build` leaves for production: the client manifest (the fake sidecar needs no server bundle).
        Directory.CreateDirectory(Path.Combine(root, "dist", "client", ".vite"));
        await File.WriteAllTextAsync(Path.Combine(root, "dist", "client", ".vite", "manifest.json"), """
            {
              "node_modules/netjes/dist/client-entry.js": { "file": "assets/entry.js", "src": "node_modules/netjes/dist/client-entry.js", "isEntry": true },
              "app/page.tsx": { "file": "assets/page.js", "src": "app/page.tsx", "isDynamicEntry": true }
            }
            """, Ct);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", ContentRootPath = root });
        builder.WebHost.UseTestServer();
        builder.Services.AddNetjes(o =>
        {
            o.SidecarScript = Path.Combine(AppContext.BaseDirectory, "Sidecar", sidecar);
            o.StreamTimeout = TimeSpan.FromSeconds(3);
            o.SidecarProcesses = processes;
            o.Mode = NetjesMode.Production;
            o.TypesFile = null;
        });
        var app = builder.Build();
        app.UseNetjes();
        app.UseAntiforgery();
        app.MapPageLoader("/", () => Page.Props(new { Value = Page.Defer(Task.FromResult(42)) }));
        app.MapPages();
        await app.StartAsync(Ct);
        return (app, root);
    }
}
