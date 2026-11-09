using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Netjes.Tests.Endpoints;

/// <summary>
/// A page package for tests. Its npm half is installed in <c>node_modules/&lt;name&gt;</c>, or linked there from a folder
/// outside <c>node_modules</c>, as a workspace links it (a symbolic link, or a junction on Windows, as npm makes). For
/// Production tests it writes the client build: a manifest with the app's and the packages' files, and the build record.
/// </summary>
internal static class TestPagePackage
{
    public const string Name = "@acme/help-center";

    /// <summary>The package's C# half, versioned like the test assembly.</summary>
    public static PagePackage Descriptor { get; } = new(Name, typeof(TestPagePackage).Assembly);

    /// <summary>Another package, for tests with two.</summary>
    public static PagePackage Other { get; } = new("@acme/status", typeof(TestPagePackage).Assembly);

    /// <summary>The Netjes versions the test packages support: the running Netjes's.</summary>
    public static string NetjesRange => "^" + new PagePackage("netjes", typeof(PagePackage).Assembly).Version;

    /// <summary>Installs the npm half in <c>node_modules</c>, with <paramref name="files"/> in its pages folder.</summary>
    /// <param name="netjes">The <c>netjes</c> field of its package.json; by default <c>{ "pages": pages }</c>.</param>
    /// <param name="version">Its version; by default the C# half's.</param>
    /// <param name="peerRange">Its <c>peerDependencies.netjes</c>; by default <see cref="NetjesRange"/>.</param>
    /// <returns>The package's folder.</returns>
    public static string Install(
        string root,
        IEnumerable<string> files,
        string name = Name,
        string pages = "app",
        JsonObject? netjes = null,
        string? version = null,
        string? peerRange = null)
    {
        var dir = Path.Combine([root, "node_modules", .. name.Split('/')]);
        Write(dir, name, files, pages, netjes, version, peerRange);
        return dir;
    }

    /// <summary>
    /// Writes the npm half to <c>packages/&lt;name&gt;</c> in <paramref name="root"/> and links <c>node_modules/&lt;name&gt;</c>
    /// to it, as an npm workspace does.
    /// </summary>
    /// <returns>The package's folder, where its files are.</returns>
    public static string Link(string root, IEnumerable<string> files, string name = Name, string pages = "app")
    {
        var target = Path.Combine(root, "packages", name.Split('/')[^1]);
        Write(target, name, files, pages, null, null, null);
        var link = Path.Combine([root, "node_modules", .. name.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        if (OperatingSystem.IsWindows())
        {
            // A symbolic link needs developer mode, which CI runners lack; npm makes a junction too.
            using var mklink = Process.Start(new ProcessStartInfo("cmd", ["/c", "mklink", "/J", link, target]) { RedirectStandardOutput = true })!;
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }

        return target;
    }

    private static void Write(
        string dir, string name, IEnumerable<string> files, string pages, JsonObject? netjes, string? version, string? peerRange)
    {
        Directory.CreateDirectory(dir);
        var manifest = new JsonObject
        {
            ["name"] = name,
            ["version"] = version ?? Descriptor.Version,
            ["peerDependencies"] = new JsonObject { ["netjes"] = peerRange ?? NetjesRange },
            ["netjes"] = netjes ?? new JsonObject { ["pages"] = pages },
        };
        File.WriteAllText(Path.Combine(dir, "package.json"), manifest.ToJsonString());
        foreach (var file in files)
        {
            var path = Path.Combine(dir, pages, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "export default function Page() { return null }");
        }
    }

    /// <summary>A package in a production build: its name, its pages folder and its files there.</summary>
    public sealed record Built(string Name, string[] Files, string Pages = "app")
    {
        /// <summary>The npm half's version in the record; by default the C# half's.</summary>
        public string? Version { get; init; }

        /// <summary>Its <c>peerDependencies.netjes</c> in the record; by default <see cref="NetjesRange"/>.</summary>
        public string? Netjes { get; init; }

        /// <summary>The NuGet package its <c>netjes</c> field names, which the record carries when it names one.</summary>
        public string? Nuget { get; init; }

        /// <summary>The id of each file and its manifest key, as the plugin writes them for an installed package.</summary>
        public IEnumerable<(string Id, string Key)> Entries =>
            Files.Select(f => ($"{Name}/{Pages}/{f}", $"node_modules/{Name}/{Pages}/{f}"));
    }

    /// <summary>
    /// The client build: a manifest with <paramref name="appFiles"/> and each package's files, and the build record of
    /// <paramref name="packages"/> (none when there are none, as the plugin writes it).
    /// </summary>
    /// <param name="unbuilt">Ids the record lists whose files are not in the manifest.</param>
    public static void WriteBuild(string root, IEnumerable<string> appFiles, Built[] packages, string[]? unbuilt = null)
    {
        var manifest = new JsonObject
        {
            ["node_modules/netjes/dist/client-entry.js"] = new JsonObject
            {
                ["file"] = "assets/client-entry.js",
                ["src"] = "node_modules/netjes/dist/client-entry.js",
                ["isEntry"] = true,
            },
        };
        var i = 0;
        void Chunk(string key, string? css = null)
        {
            var chunk = new JsonObject { ["file"] = $"assets/module-{i}.js", ["src"] = key, ["isDynamicEntry"] = true };
            if (css is not null)
            {
                chunk["css"] = new JsonArray(css);
            }

            manifest[key] = chunk;
            i++;
        }

        foreach (var file in appFiles)
        {
            Chunk(file);
        }

        var record = new JsonArray();
        foreach (var package in packages)
        {
            var ids = new JsonObject();
            foreach (var (id, key) in package.Entries)
            {
                ids[id] = key;
                if (!(unbuilt ?? []).Contains(id))
                {
                    Chunk(key, $"assets/module-{i}.css");
                }
            }

            var entry = new JsonObject
            {
                ["name"] = package.Name,
                ["version"] = package.Version ?? Descriptor.Version,
                ["netjes"] = package.Netjes ?? NetjesRange,
                ["pages"] = package.Pages,
                ["files"] = ids,
            };
            if (package.Nuget is not null)
            {
                entry["nuget"] = package.Nuget;
            }

            record.Add(entry);
        }

        var vite = Path.Combine(root, "dist", "client", ".vite");
        Directory.CreateDirectory(vite);
        File.WriteAllText(Path.Combine(vite, "manifest.json"), manifest.ToJsonString());
        if (packages.Length > 0)
        {
            File.WriteAllText(Path.Combine(vite, "netjes.json"), new JsonObject { ["packages"] = record }.ToJsonString());
        }
    }
}
