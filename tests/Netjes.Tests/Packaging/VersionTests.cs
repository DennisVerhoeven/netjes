using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;

namespace Netjes.Tests.Packaging;

public class VersionTests
{
    [Fact]
    public void TheNuGetAndNpmPackages_HaveOneVersion()
    {
        var root = RepoRoot();
        var props = XDocument.Load(Path.Combine(root, "Directory.Build.props"));
        var dotnet = props.Descendants("Version").Single().Value;
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "packages", "netjes", "package.json")));
        var npm = manifest.RootElement.GetProperty("version").GetString();

        Assert.Equal(npm, dotnet);
        Assert.Equal(npm, typeof(Page).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]);
    }

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
