namespace Netjes.Tests.Packaging;

/// <summary>
/// build/Netjes.targets keeps folders that are build output out of an app's default items, so the next build or publish
/// does not copy them into the output again. The tests evaluate a small Web SDK project that imports the targets file.
/// </summary>
public class BuildTargetsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("netjes-targets-").FullName;

    [Theory]
    [InlineData("publish")]
    [InlineData("dist")]
    [InlineData("node_modules")]
    [InlineData("test-results")]
    [InlineData("playwright-report")]
    public void DefaultItems_LeaveOutBuildOutputFolders(string folder)
    {
        Write("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <Import Project="{Path.Combine(RepoRoot(), "src", "Netjes", "build", "Netjes.targets")}" />
            </Project>
            """);
        Write(Path.Combine(folder, "output.json"), "{}");
        Write(Path.Combine("wwwroot-like", "kept.json"), "{}");

        var items = DefaultItems();

        Assert.Contains("wwwroot-like/kept.json", items);
        Assert.DoesNotContain(items, item => item.StartsWith(folder + "/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Publish")]
    [InlineData("publish")]
    public void SourceFolders_NamedLikeTheOutput_StillCompile(string folder)
    {
        Write("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <Import Project="{Path.Combine(RepoRoot(), "src", "Netjes", "build", "Netjes.targets")}" />
            </Project>
            """);
        Write(Path.Combine(folder, "Feature.cs"), "class Feature { }");

        Assert.Contains(folder + "/Feature.cs", DefaultItems("Compile"));
    }

    [Theory]
    [InlineData("tsconfig.json")]
    [InlineData("tsconfig.app.json")]
    [InlineData("tsconfig.node.json")]
    public void Content_KeepsTheTypeScriptConfigsOutOfTheOutput(string file)
    {
        Write("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <Import Project="{Path.Combine(RepoRoot(), "src", "Netjes", "build", "Netjes.targets")}" />
            </Project>
            """);
        Write(file, "{}");

        var config = Assert.Single(MsBuildItems.Of(_dir, "App.csproj", "Content"), item => item.Path() == file);

        Assert.Equal("Never", config.GetProperty("CopyToOutputDirectory").GetString());
        Assert.Equal("Never", config.GetProperty("CopyToPublishDirectory").GetString());
    }

    [Theory]
    [InlineData("dist/client/.vite/manifest.json")]
    [InlineData("dist/client/.vite/netjes.json")]
    public void Publish_ShipsTheClientBuildsRecords(string file)
    {
        // The manifest, and the record of the page packages the build holds: production routes come from both.
        Write("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <Import Project="{Path.Combine(RepoRoot(), "src", "Netjes", "build", "Netjes.targets")}" />
            </Project>
            """);
        Write(file, "{}");

        var published = MsBuildItems.After(_dir, "App.csproj", "NetjesPublishClient", "ResolvedFileToPublish", "NetjesSkipClientBuild=true");

        Assert.Contains(file, published.Select(item => item.GetProperty("RelativePath").GetString()!.Replace('\\', '/')));
    }

    private List<string> DefaultItems(params string[] types) =>
        MsBuildItems.Of(_dir, "App.csproj", types.Length == 0 ? ["Content", "None", "EmbeddedResource"] : types)
            .Select(item => item.Path())
            .ToList();

    private void Write(string path, string content)
    {
        var full = Path.Combine(_dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

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
