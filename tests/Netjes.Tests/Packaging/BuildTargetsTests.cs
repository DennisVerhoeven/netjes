using System.Diagnostics;
using System.Text.Json;

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

    private List<string> DefaultItems(params string[] types)
    {
        string[] arguments = ["msbuild", "App.csproj", .. (types.Length == 0 ? ["Content", "None", "EmbeddedResource"] : types).Select(t => "-getItem:" + t)];
        var start = new ProcessStartInfo("dotnet", arguments)
        {
            WorkingDirectory = _dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), "msbuild did not finish");
        Assert.True(process.ExitCode == 0, output.Result + error.Result);

        using var json = JsonDocument.Parse(output.Result);
        return json.RootElement.GetProperty("Items").EnumerateObject()
            .SelectMany(group => group.Value.EnumerateArray())
            .Select(item => item.GetProperty("Identity").GetString()!.Replace('\\', '/'))
            .ToList();
    }

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
