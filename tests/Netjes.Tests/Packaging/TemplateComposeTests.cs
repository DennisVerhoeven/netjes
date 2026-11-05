using System.Diagnostics;
using System.Text.Json;

namespace Netjes.Tests.Packaging;

/// <summary>
/// The host port of the template's Compose stacks: APP_PORT, from the shell or a .env file, or 8080. The tests read the
/// configuration Compose resolves (<c>docker compose config</c>) in the template's folder.
/// </summary>
[Trait("Category", "Docker")]
public class TemplateComposeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ProductionStack_PublishesTheHostPortAppPortNames()
    {
        string[] files = ["-f", Path.Combine(Template(), "docker-compose.yml"), "-f", Path.Combine(Template(), "docker-compose.prod.yml")];

        var moved = Port(await ConfigAsync(files, appPort: "8090"), 8080);
        var kept = Port(await ConfigAsync(files, appPort: null), 8080);

        Assert.Equal("8090", moved.GetProperty("published").GetString());
        Assert.Equal("8080", kept.GetProperty("published").GetString());
    }

    [Fact]
    public async Task DevelopmentStack_PublishesAppPortOnLoopbackOnly()
    {
        // No -f: Compose reads docker-compose.yml and docker-compose.override.yml, as `docker compose up` does.
        var config = await ConfigAsync([], appPort: "8090");

        var app = Port(config, 8080);
        var vite = Port(config, 5173);

        Assert.Equal(("127.0.0.1", "8090"), (app.GetProperty("host_ip").GetString(), app.GetProperty("published").GetString()));
        Assert.Equal(("127.0.0.1", "5173"), (vite.GetProperty("host_ip").GetString(), vite.GetProperty("published").GetString()));
    }

    /// <summary>The one port of the <c>app</c> service that forwards to <paramref name="target"/> in the container.</summary>
    private static JsonElement Port(JsonElement config, int target)
    {
        Assert.True(config.GetProperty("services").TryGetProperty("app", out var app), $"No service named app:\n{config}");
        return Assert.Single(app.GetProperty("ports").EnumerateArray(), port => port.GetProperty("target").GetInt32() == target);
    }

    /// <summary>
    /// What <c>docker compose config</c> resolves in the template's folder from <paramref name="files"/>, with APP_PORT
    /// set to <paramref name="appPort"/>, or not set at all.
    /// </summary>
    private static async Task<JsonElement> ConfigAsync(string[] files, string? appPort)
    {
        var start = new ProcessStartInfo("docker")
        {
            WorkingDirectory = Template(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in (string[])["compose", .. files, "config", "--format", "json"])
        {
            start.ArgumentList.Add(arg);
        }

        // Nothing in this process's environment chooses other files or a port.
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("COMPOSE_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(name);
        }

        start.Environment.Remove("APP_PORT");
        if (appPort is not null)
        {
            start.Environment["APP_PORT"] = appPort;
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = process.StandardError.ReadToEndAsync(Ct);
        try
        {
            await process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromMinutes(1), Ct);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }

        Assert.True(process.ExitCode == 0, $"docker compose config exited with {process.ExitCode}:\n{await output}{await error}");
        using var json = JsonDocument.Parse(await output);
        return json.RootElement.Clone();
    }

    private static string Template() => Path.Combine(RepoRoot(), "packages", "netjes", "template");

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
}
