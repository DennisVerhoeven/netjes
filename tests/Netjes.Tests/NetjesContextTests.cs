using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;

namespace Netjes.Tests;

public class NetjesContextTests : IDisposable
{
    private readonly List<string> _directories = [];

    /// <summary>A temporary directory, removed when the test ends.</summary>
    private string NewDirectory(string prefix)
    {
        var path = Directory.CreateTempSubdirectory(prefix).FullName;
        _directories.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _directories)
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static NetjesContext Create(string contentRoot, NetjesMode mode, string? root = null) =>
        new(Options.Create(new NetjesOptions { Mode = mode, Root = root }),
            new HostingEnvironment { ContentRootPath = contentRoot, EnvironmentName = "Development" });

    [Fact]
    public void SidecarScript_InDevelopment_IsFoundInAnAncestorNodeModulesLikeNodeResolution()
    {
        var workspace = NewDirectory("netjes-workspace-");
        var app = Directory.CreateDirectory(Path.Combine(workspace, "apps", "web")).FullName;
        var script = Path.Combine(workspace, "node_modules", "netjes", "sidecar", "server.mjs");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(script, "");

        var context = Create(app, NetjesMode.Development);

        Assert.Equal(script, context.SidecarScript);
    }

    [Fact]
    public void SidecarScript_InDevelopment_PrefersTheAppsOwnNodeModules()
    {
        var workspace = NewDirectory("netjes-workspace-");
        var app = Directory.CreateDirectory(Path.Combine(workspace, "app")).FullName;
        foreach (var dir in new[] { workspace, app })
        {
            var path = Path.Combine(dir, "node_modules", "netjes", "sidecar", "server.mjs");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
        }

        var context = Create(app, NetjesMode.Development);

        Assert.Equal(Path.Combine(app, "node_modules", "netjes", "sidecar", "server.mjs"), context.SidecarScript);
    }

    [Fact]
    public void SidecarScript_InProduction_IsTheCopyInTheServerBuild()
    {
        var app = NewDirectory("netjes-app-");

        var context = Create(app, NetjesMode.Production);

        Assert.Equal(Path.Combine(app, "dist", "server", "netjes-sidecar.mjs"), context.SidecarScript);
    }

    [Fact]
    public void Root_IsResolvedAgainstTheContentRoot()
    {
        var contentRoot = NewDirectory("netjes-app-");

        var context = Create(contentRoot, NetjesMode.Production, root: "ClientApp");

        Assert.Equal(Path.Combine(contentRoot, "ClientApp"), context.Root);
    }

    [Theory]
    [InlineData("NETJES_COMMAND")]
    [InlineData("Netjes:Command:Name")]
    public void Command_InTheAppsConfiguration_IsIgnored(string key)
    {
        // appsettings, command-line args or a host-level environment provider (Netjes__Command__Name): none of them
        // puts an app in command mode.
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = "export", ["NETJES_OUTPUT"] = "out" })
            .Build());
        services.AddSingleton<IHostEnvironment>(
            new HostingEnvironment { ContentRootPath = NewDirectory("netjes-app-"), EnvironmentName = "Production" });
        services.AddLogging();
        services.AddNetjes();

        var context = services.BuildServiceProvider().GetRequiredService<NetjesContext>();

        Assert.Null(context.Command);
    }

    [Fact]
    public void Command_FromTheProcessEnvironment_IsRead()
    {
        var variables = new Dictionary<string, string> { ["NETJES_COMMAND"] = "generate", ["NETJES_OUTPUT"] = "x.ts", ["NETJES_CHECK"] = "1" };

        var context = new NetjesContext(
            Options.Create(new NetjesOptions()),
            new HostingEnvironment { ContentRootPath = NewDirectory("netjes-app-"), EnvironmentName = "Production" },
            name => variables.GetValueOrDefault(name));

        Assert.Equal("generate", context.Command?.Name);
        Assert.Equal("x.ts", context.Command?.Output);
        Assert.True(context.Command?.Check);
    }

    [Theory]
    [InlineData("https://cms.example; script-src *")]
    [InlineData("https://cms.example https://other.example")]
    [InlineData("'unsafe-inline'")]
    [InlineData("")]
    public void DraftFrameAncestors_ThatAreNotOneSourceEach_AreRejected(string source)
    {
        var options = new NetjesOptions();
        options.SecurityHeaders.DraftFrameAncestors = ["https://cms.example", source];

        var ex = Assert.Throws<InvalidOperationException>(() => new NetjesContext(
            Options.Create(options), new HostingEnvironment { ContentRootPath = AppContext.BaseDirectory, EnvironmentName = "Production" }));

        Assert.Contains("DraftFrameAncestors", ex.Message);
    }
}
