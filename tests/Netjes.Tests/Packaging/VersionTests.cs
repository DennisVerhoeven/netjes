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

    /// <summary>
    /// The example page package, examples/HelpCenter: its NuGet half takes the version in Directory.Build.props, and an
    /// app stops at startup when a page package's halves are two versions, or its peer range leaves out the running
    /// Netjes. So its npm half and its lockfile entry carry the version too, with the peer range ^version.
    /// </summary>
    [Fact]
    public void TheExamplePagePackage_HasTheRepositorysVersion_OnBothHalves_AndItsPeerRange()
    {
        var root = RepoRoot();
        var version = XDocument.Load(Path.Combine(root, "Directory.Build.props")).Descendants("Version").Single().Value;
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "examples", "HelpCenter", "package.json")));
        using var lockfile = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "package-lock.json")));
        var locked = lockfile.RootElement.GetProperty("packages").GetProperty("examples/HelpCenter");

        Assert.Equal(version, manifest.RootElement.GetProperty("version").GetString());
        Assert.Equal($"^{version}", manifest.RootElement.GetProperty("peerDependencies").GetProperty("netjes").GetString());
        Assert.Equal(version, locked.GetProperty("version").GetString());
        Assert.Equal($"^{version}", locked.GetProperty("peerDependencies").GetProperty("netjes").GetString());
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
