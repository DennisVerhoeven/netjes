using System.Diagnostics;
using System.Text.Json;

namespace Netjes.Tests.Packaging;

/// <summary>
/// The host ports of the template's Compose stacks: APP_PORT for the app, from the shell or a .env file, or 8080, and in
/// development VITE_PORT for the Vite dev server, or 5173. The tests read the configuration Compose resolves
/// (<c>docker compose config</c>) in the template's folder.
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
        var moved = await ConfigAsync([], appPort: "8090");
        var kept = await ConfigAsync([], appPort: null);

        Assert.Equal(("127.0.0.1", "8090"), HostAndPublished(Port(moved, 8080)));
        Assert.Equal(("127.0.0.1", "8080"), HostAndPublished(Port(kept, 8080)));
        Assert.Equal(("127.0.0.1", "5173"), HostAndPublished(Port(moved, 5173)));
        Assert.Equal(("127.0.0.1", "5173"), HostAndPublished(Port(kept, 5173)));
    }

    [Fact]
    public async Task DevelopmentStack_MovesViteToTheSamePortInsideAndOut()
    {
        // The browser loads modules from http://localhost:<ViteDevPort>, so the port is the same on the host and in the
        // container, and the app is told which it is.
        var moved = await ConfigAsync([], appPort: null, vitePort: "5190");
        var kept = await ConfigAsync([], appPort: null, vitePort: null);

        Assert.Equal(("127.0.0.1", "5190"), HostAndPublished(Port(moved, 5190)));
        Assert.Equal("5190", ViteDevPort(moved));
        Assert.Equal(("127.0.0.1", "5173"), HostAndPublished(Port(kept, 5173)));
        Assert.Equal("5173", ViteDevPort(kept));
        Assert.DoesNotContain(moved.GetProperty("services").GetProperty("app").GetProperty("ports").EnumerateArray(), port => port.GetProperty("target").GetInt32() == 5173);
    }

    [Fact]
    public async Task DevelopmentStack_KeepsTheDataProtectionKeysOnAVolume()
    {
        var config = await ConfigAsync([], appPort: null);

        var keys = Assert.Single(
            config.GetProperty("services").GetProperty("app").GetProperty("volumes").EnumerateArray(),
            volume => volume.GetProperty("target").GetString() == "/root/.aspnet/DataProtection-Keys");
        Assert.Equal("volume", keys.GetProperty("type").GetString());
        Assert.True(config.GetProperty("volumes").EnumerateObject().Any(), "The key volume is declared.");
    }

    [Fact]
    public void Dockerfile_LeavesTheAppsPortToTheImagesHttpPorts()
    {
        // The .NET images set ASPNETCORE_HTTP_PORTS=8080; ASPNETCORE_URLS beside it takes precedence and has to be kept
        // in step with it.
        var setting = File.ReadAllLines(Path.Combine(Template(), "Dockerfile"))
            .Where(line => !line.TrimStart().StartsWith('#'))
            .FirstOrDefault(line => line.Contains("ASPNETCORE_URLS", StringComparison.Ordinal));

        Assert.Null(setting);
    }

    private static string? ViteDevPort(JsonElement config) =>
        config.GetProperty("services").GetProperty("app").GetProperty("environment").GetProperty("Netjes__ViteDevPort").GetString();

    private static (string? HostIp, string? Published) HostAndPublished(JsonElement port) =>
        (port.GetProperty("host_ip").GetString(), port.GetProperty("published").GetString());

    /// <summary>The one port of the <c>app</c> service that forwards to <paramref name="target"/> in the container.</summary>
    private static JsonElement Port(JsonElement config, int target)
    {
        Assert.True(config.GetProperty("services").TryGetProperty("app", out var app), $"No service named app:\n{config}");
        return Assert.Single(app.GetProperty("ports").EnumerateArray(), port => port.GetProperty("target").GetInt32() == target);
    }

    /// <summary>
    /// What <c>docker compose config</c> resolves in the template's folder from <paramref name="files"/>, with APP_PORT
    /// set to <paramref name="appPort"/> and VITE_PORT to <paramref name="vitePort"/>, or not set at all.
    /// </summary>
    private static async Task<JsonElement> ConfigAsync(string[] files, string? appPort, string? vitePort = null)
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

        start.Environment.Remove("VITE_PORT");
        if (vitePort is not null)
        {
            start.Environment["VITE_PORT"] = vitePort;
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
