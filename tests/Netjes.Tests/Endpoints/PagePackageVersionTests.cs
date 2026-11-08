using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;

namespace Netjes.Tests.Endpoints;

/// <summary>
/// The versions of a mounted page package: its npm half and its NuGet half are one version, and its
/// <c>peerDependencies.netjes</c> holds the running Netjes. A package that fails either stops the app in one line.
/// </summary>
public class PagePackageVersionTests
{
    private static readonly VersionRanges Ranges = LoadRanges();

    /// <summary>The cases of <c>packages/netjes/test/fixtures/version-ranges.json</c>, which <c>cli-info.test.ts</c> reads too.</summary>
    public static TheoryData<string, string, bool> RangeCases()
    {
        var data = new TheoryData<string, string, bool>();
        foreach (var range in Ranges.Ranges)
        {
            foreach (var version in range.In)
            {
                data.Add(range.Range, version, true);
            }

            foreach (var version in range.Out)
            {
                data.Add(range.Range, version, false);
            }
        }

        return data;
    }

    public static TheoryData<string> InvalidRanges() => [.. Ranges.Invalid];

    public static TheoryData<string> InvalidVersions() => [.. Ranges.InvalidVersions];

    [Theory]
    [MemberData(nameof(RangeCases))]
    public void Satisfies_ReadsARangeAsNpmDoes(string range, string version, bool expected) =>
        Assert.Equal(expected, PagePackageVersions.Satisfies(version, range));

    [Theory]
    [MemberData(nameof(InvalidRanges))]
    public void Satisfies_ARangeThatIsNone_HoldsNoVersion(string range) =>
        Assert.False(PagePackageVersions.Satisfies("1.2.3", range));

    [Theory]
    [MemberData(nameof(InvalidVersions))]
    public void Satisfies_AVersionThatIsNone_IsInNoRange(string version) =>
        // A range that would hold each of them, read loosely.
        Assert.False(PagePackageVersions.Satisfies(version, "* || >=1.2.3-0"));

    [Theory]
    [InlineData("Development", null, "Netjes.Tests")]
    [InlineData("Development", "Acme.HelpCenter", "Acme.HelpCenter")]
    [InlineData("Production", null, "Netjes.Tests")]
    [InlineData("Production", "Acme.HelpCenter", "Acme.HelpCenter")]
    public async Task HalvesOfTwoVersions_StopStartup_NamingBoth(string environment, string? nuget, string named)
    {
        var ex = await Assert.ThrowsAsync<NetjesStartupException>(() => StartAsync(environment, version: "9.9.9", nuget: nuget));

        Assert.Equal($"{Help} is 9.9.9 on npm and {named} is {Descriptor.Version} on NuGet: install one version of both", ex.Message);
    }

    private const string Help = TestPagePackage.Name;

    private static PagePackage Descriptor => TestPagePackage.Descriptor;

    /// <summary>
    /// The app with the package mounted at <c>/help</c>, its npm half at <paramref name="version"/> (the NuGet half's by
    /// default) with <paramref name="netjes"/> as its peer range: installed in Development, in the client build's record
    /// in Production.
    /// </summary>
    private static Task<TestApp> StartAsync(string environment, string? version = null, string? netjes = null, string? nuget = null) =>
        TestApp.StartAsync(
            environment == "Production" ? [] : ["app/page.tsx"],
            environment: environment,
            prepareRoot: root =>
            {
                if (environment == "Production")
                {
                    TestPagePackage.WriteBuild(root, ["app/page.tsx"], [new(Help, ["page.tsx"]) { Version = version, Netjes = netjes, Nuget = nuget }]);
                }
                else
                {
                    var field = new JsonObject { ["pages"] = "app" };
                    if (nuget is not null)
                    {
                        field["nuget"] = nuget;
                    }

                    TestPagePackage.Install(root, ["page.tsx"], netjes: field, version: version, peerRange: netjes);
                }
            },
            map: a => a.MapPagePackage(Descriptor, "/help"));

    private static VersionRanges LoadRanges()
    {
        var path = Path.Combine(RepoRoot(), "packages", "netjes", "test", "fixtures", "version-ranges.json");
        return JsonSerializer.Deserialize<VersionRanges>(File.ReadAllText(path), JsonSerializerOptions.Web)!;
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

    private sealed record VersionRanges(IReadOnlyList<VersionRange> Ranges, IReadOnlyList<string> Invalid, IReadOnlyList<string> InvalidVersions);

    private sealed record VersionRange(string Range, IReadOnlyList<string> In, IReadOnlyList<string> Out);
}
