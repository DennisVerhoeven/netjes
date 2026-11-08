using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task APeerRangeWithoutTheRunningNetjes_StopsStartup_NamingBoth(string environment)
    {
        var ex = await Assert.ThrowsAsync<NetjesStartupException>(() => StartAsync(environment, netjes: "^99.0.0"));

        Assert.Equal($"{Help} {Descriptor.Version} supports Netjes ^99.0.0; this app runs Netjes {Running}", ex.Message);
    }

    [Fact]
    public void ThePeerRange_HoldsTheRunningNetjesByItsVersion_WithoutTheCommit()
    {
        var install = new Netjes.Routing.PagePackageInstall(Help, Descriptor.Version, "^0.0.4", "app", null);

        PagePackageVersions.Check(Descriptor, install, netjes: "0.0.4+0123abcd");
        var ex = Assert.Throws<NetjesStartupException>(() => PagePackageVersions.Check(Descriptor, install, netjes: "0.0.5+0123abcd"));

        Assert.Equal($"{Help} {Descriptor.Version} supports Netjes ^0.0.4; this app runs Netjes 0.0.5", ex.Message);
    }

    [Fact]
    public async Task AsItsOwnProgram_TheAppSaysSoInOneLine_AndExitsWithCode1()
    {
        // A program of its own (dotnet run, as the netjes CLI runs an app), mounting a package whose halves are two
        // versions. Its C# half is the program's assembly, versioned 1.0.0.
        var root = Directory.CreateTempSubdirectory("netjes-test-").FullName;
        try
        {
            TestPagePackage.Install(root, ["page.tsx"], version: "9.9.9");
            Directory.CreateDirectory(Path.Combine(root, "app"));
            var program = Path.Combine(root, "program", "app.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(program)!);
            await File.WriteAllTextAsync(program, $$"""
                #:sdk Microsoft.NET.Sdk.Web
                #:project {{Path.Combine(RepoRoot(), "src", "Netjes", "Netjes.csproj")}}
                var builder = WebApplication.CreateBuilder(args);
                builder.Services.AddNetjes();
                var app = builder.Build();
                app.UseNetjes();
                app.MapPagePackage(new Netjes.PagePackage("{{Help}}", typeof(Program).Assembly), "/help");
                app.MapPages();
                app.Run();
                """, Ct);

            var (code, output) = await RunAsync(program, root);

            AssertOneLine(output, $"{Help} is 9.9.9 on npm and app is 1.0.0 on NuGet: install one version of both");
            Assert.True(code == 1, $"Exit code {code}:\n{output}");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private const string Help = TestPagePackage.Name;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The running Netjes's version, as NuGet versions it.</summary>
    private static string Running => PagePackage.VersionOf(typeof(PagePackage).Assembly);

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

    /// <summary>
    /// The one error the program logged, at Critical and as the last thing it logged, is <paramref name="message"/>, with
    /// no exception or stack.
    /// </summary>
    private static void AssertOneLine(string output, string message)
    {
        var lines = output.Split('\n');
        var entries = lines.Select((line, i) => (line, i)).Where(l => Regex.IsMatch(l.line, "^(trce|dbug|info|warn|fail|crit): ")).ToList();
        var (header, index) = Assert.Single(entries, e => e.line.StartsWith("fail: ") || e.line.StartsWith("crit: "));
        Assert.StartsWith("crit: ", header);
        Assert.Equal(message, lines[index + 1].Trim());
        Assert.Equal(index, entries[^1].i);
        Assert.DoesNotContain("Unhandled exception", output, StringComparison.Ordinal);
        Assert.DoesNotContain(lines, line => line.TrimStart().StartsWith("at ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Builds and runs a file-based app, with the configuration this assembly was built in so that Netjes is not built
    /// again, and its build output in <paramref name="root"/>, until it exits.
    /// </summary>
    private static async Task<(int Code, string Output)> RunAsync(string program, string root)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet"));
        var configuration = typeof(PagePackageVersionTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Release";
        var start = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "run", "--file", program, "-c", configuration, "--artifacts-path", Path.Combine(root, "artifacts") })
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        start.Environment["Netjes__Root"] = root;
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        var output = new ConcurrentQueue<string>();
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) output.Enqueue(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) output.Enqueue(e.Data);
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(120), Ct);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }

        // WaitForExitAsync waits for the output to end too.
        return (process.ExitCode, string.Join('\n', output));
    }

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
