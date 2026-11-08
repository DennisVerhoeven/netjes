using System.Text.Json;
using Netjes.Rendering;

namespace Netjes.Routing;

/// <summary>
/// The npm half of a mounted page package, as the app has it: installed in <c>node_modules</c> (development, and the
/// commands that scan page files), or listed in the client build's record (production).
/// </summary>
/// <param name="Name">The npm name.</param>
/// <param name="Version">Its version.</param>
/// <param name="Netjes">Its <c>peerDependencies.netjes</c>: the Netjes versions it supports.</param>
/// <param name="Pages">Its pages folder, relative to the package.</param>
/// <param name="Nuget">The NuGet package of its C# half, when it names one.</param>
internal sealed record PagePackageInstall(string Name, string Version, string Netjes, string Pages, string? Nuget)
{
    /// <summary>The real path of the package's folder, links resolved; null in production.</summary>
    public string? Root { get; init; }

    /// <summary>Production: the id of each of its convention files, and that file's key in the client build's manifest.</summary>
    public IReadOnlyDictionary<string, string>? Files { get; init; }

    /// <summary>
    /// Reads the <c>package.json</c> of a package installed in <paramref name="directory"/>, refusing one that is no page
    /// package or whose paths would leave it, as the Vite plugin does.
    /// </summary>
    public static PagePackageInstall Read(string name, string directory)
    {
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "package.json")));
        }
        catch (JsonException ex)
        {
            throw new NetjesStartupException($"'{name}' is mounted, but its package.json is not JSON: {ex.Message}", ex);
        }

        using var _ = json;
        var manifest = json.RootElement;
        if (manifest.ValueKind != JsonValueKind.Object || !manifest.TryGetProperty("netjes", out var field) || field.ValueKind != JsonValueKind.Object)
        {
            throw new NetjesStartupException($"'{name}' is mounted, but it is not a page package: its package.json has no \"netjes\" field.");
        }

        string Inside(string key, JsonElement value) =>
            value.ValueKind == JsonValueKind.String && IsInside(value.GetString()!)
                ? value.GetString()!
                : throw new NetjesStartupException(
                    $"'{name}' has \"{key}\": {value.GetRawText()} in the \"netjes\" field of its package.json: a path there is relative " +
                    "and stays inside the package (no \"..\", no leading \"/\", no \"\\\").");

        var pages = field.TryGetProperty("pages", out var pagesValue) ? Inside("pages", pagesValue) : "app";
        if (field.TryGetProperty("sources", out var sources))
        {
            if (sources.ValueKind != JsonValueKind.Array)
            {
                throw new NetjesStartupException(
                    $"'{name}' has \"sources\": {sources.GetRawText()} in the \"netjes\" field of its package.json: it is a list of folders.");
            }

            foreach (var source in sources.EnumerateArray())
            {
                Inside("sources", source);
            }
        }

        if (field.TryGetProperty("css", out var css))
        {
            Inside("css", css);
        }

        // A package published without it (its "files" leave it out) would otherwise bring no pages, without a word.
        if (!Directory.Exists(Path.Combine(directory, pages)))
        {
            throw new NetjesStartupException(pagesValue.ValueKind == JsonValueKind.Undefined
                ? $"'{name}' has no app folder, where a page package's pages are unless \"pages\" in the \"netjes\" field of its package.json names another."
                : $"'{name}' has \"pages\": \"{pages}\" in the \"netjes\" field of its package.json, which is no folder in the package.");
        }

        static string? Text(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        return new PagePackageInstall(
            name,
            Text(manifest, "version") ?? "",
            manifest.TryGetProperty("peerDependencies", out var peers) ? Text(peers, "netjes") ?? "" : "",
            pages,
            Text(field, "nuget"))
        {
            Root = directory,
        };
    }

    /// <summary>A relative path inside the package: no <c>.</c>, <c>..</c> or empty folder, no <c>\</c>, no drive letter.</summary>
    private static bool IsInside(string path) =>
        path.Length > 0 && path.IndexOfAny(['\\', ':']) < 0 && path.Split('/').All(segment => segment is not ("" or "." or ".."));
}

/// <summary>A page package the app mounted, with its npm half.</summary>
internal sealed record MountedPackage(PagePackageMount Mount, PagePackageInstall Install)
{
    public string Name => Mount.Package.Name;

    /// <summary>The package's source for the route table, with its files by id.</summary>
    public PageSource SourceOf(IEnumerable<string> files) => PageSource.Package(Name, Install.Pages, Mount.Prefix, files);
}

/// <summary>
/// What the route table is built from besides the app's own files: the page packages mounted with
/// <c>MapPagePackage</c>. A change gives it another <see cref="Version"/>, and the route table providers build the table
/// again.
/// </summary>
internal sealed class RouteSources(NetjesContext context, ViteManifestProvider manifest)
{
    private readonly Lock _gate = new();
    private IReadOnlyList<MountedPackage> _mounts = [];
    private int _version;

    /// <summary>The mounted packages, in the order they were mounted.</summary>
    public IReadOnlyList<MountedPackage> Mounts => Volatile.Read(ref _mounts);

    public int Version => Volatile.Read(ref _version);

    /// <summary>App-absolute URL patterns where the app's page replaces a package's page.</summary>
    public IReadOnlyCollection<string> Overrides { get; } = [];

    /// <summary>Whether <c>MapPages()</c> has run: the mounts are complete, and a page conflict left pending is one.</summary>
    public bool PagesMapped { get; private set; }

    public void MarkPagesMapped() => PagesMapped = true;

    public MountedPackage? Find(string name) => Mounts.FirstOrDefault(m => m.Name == name);

    /// <summary>The mounted package a file id is of; null for the app's own files.</summary>
    public MountedPackage? OwnerOf(string? file) =>
        file is null ? null : Mounts.FirstOrDefault(m => file.StartsWith(m.Name + "/", StringComparison.Ordinal));

    /// <summary>Mounts a package, finding its npm half first: a package that is not there stops the app in one line.</summary>
    public MountedPackage Add(PagePackageMount mount)
    {
        var mounted = new MountedPackage(mount, Find(mount.Package));
        lock (_gate)
        {
            Volatile.Write(ref _mounts, [.. _mounts, mounted]);
            Interlocked.Increment(ref _version);
        }

        return mounted;
    }

    public void Remove(MountedPackage mounted)
    {
        lock (_gate)
        {
            Volatile.Write(ref _mounts, [.. _mounts.Where(m => m != mounted)]);
            Interlocked.Increment(ref _version);
        }
    }

    private PagePackageInstall Find(PagePackage package)
    {
        if (context.ScansPageFiles)
        {
            return context.FindPackage(package.Name) is { } directory
                ? PagePackageInstall.Read(package.Name, directory)
                : throw new NetjesStartupException($"'{package.Name}' is mounted but not installed: npm install {package.Name}@{package.Version}");
        }

        return manifest.Record?.Packages.FirstOrDefault(p => p.Name == package.Name)?.ToInstall() ?? throw new NetjesStartupException(
            $"'{package.Name}' is mounted but not in the client build: add it to netjes({{ packages }}) in vite.config.ts and build again");
    }
}
