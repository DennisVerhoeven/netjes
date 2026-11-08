using System.Diagnostics;
using System.Text.Json;

namespace Netjes.Tests.Packaging;

/// <summary>The items of a project, as <c>dotnet msbuild -getItem</c> evaluates them.</summary>
internal static class MsBuildItems
{
    /// <summary>The items of the given types in <paramref name="project"/>, a project file in <paramref name="dir"/>.</summary>
    public static List<JsonElement> Of(string dir, string project, params string[] types) => Run(dir, [project, .. types.Select(t => "-getItem:" + t)]);

    /// <summary>The items of type <paramref name="type"/> after <paramref name="target"/> ran, with the given properties.</summary>
    public static List<JsonElement> After(string dir, string project, string target, string type, params string[] properties) =>
        Run(dir, [project, "-t:" + target, .. properties.Select(p => "-p:" + p), "-getItem:" + type]);

    private static List<JsonElement> Run(string dir, string[] msbuild)
    {
        string[] arguments = ["msbuild", .. msbuild];
        var start = new ProcessStartInfo("dotnet", arguments)
        {
            WorkingDirectory = dir,
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
            .Select(item => item.Clone())
            .ToList();
    }

    /// <summary>An item's path relative to the project, with <c>/</c>.</summary>
    public static string Path(this JsonElement item) => item.GetProperty("Identity").GetString()!.Replace('\\', '/');
}
