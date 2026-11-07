namespace Netjes.Tests.Packaging;

/// <summary>
/// A new app's C# tests and Playwright specs sit below the app's project, whose SDK compiles everything below it unless
/// it is told not to. The test evaluates the template's project file with the build targets of the package, as
/// <c>netjes new</c> writes them.
/// </summary>
public sealed class TemplateProjectTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("netjes-template-project-").FullName;

    [Fact]
    public void DefaultItems_LeaveOutTheTestProjectAndTheEndToEndTests()
    {
        var targets = Path.Combine(RepoRoot(), "src", "Netjes", "build", "Netjes.targets");
        var template = File.ReadAllText(Path.Combine(RepoRoot(), "packages", "netjes", "template", "__NAME__.csproj"));
        Assert.Contains("<!--NETJES_REFERENCES-->", template);
        Write("App.csproj", template.Replace("<!--NETJES_REFERENCES-->", $"<Import Project=\"{targets}\" />"));
        Write("Features/Home/HomePage.cs", "class HomePage { }");
        // Only the folders at the project's root are left out: a feature may have a folder of that name.
        Write("Features/tests/Nested.cs", "class Nested { }");
        Write("tests/App.Tests/HomePageTests.cs", "class HomePageTests { }");
        Write("tests/App.Tests/obj/project.assets.json", "{}");
        Write("e2e/home.spec.ts", "");
        Write("e2e/fixture.json", "{}");
        Write("e2e/Helper.cs", "class Helper { }");

        var compiled = MsBuildItems.Of(_dir, "App.csproj", "Compile").Select(item => item.Path()).ToList();
        var items = MsBuildItems.Of(_dir, "App.csproj", "Compile", "Content", "None").Select(item => item.Path()).ToList();

        Assert.Contains("Features/Home/HomePage.cs", compiled);
        Assert.Contains("Features/tests/Nested.cs", compiled);
        Assert.DoesNotContain(items, item => item.StartsWith("tests/", StringComparison.Ordinal) || item.StartsWith("e2e/", StringComparison.Ordinal));
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
