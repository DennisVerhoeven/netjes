using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netjes.Rendering;

/// <summary>The tags a server-rendered document needs so the browser can load and hydrate the page.</summary>
internal sealed record DocumentAssets(
    IReadOnlyList<string> Stylesheets,
    IReadOnlyList<string> ModulePreloads,
    IReadOnlyList<string> Scripts);

/// <summary>Reads the client build manifest Vite writes to <c>dist/client/.vite/manifest.json</c>.</summary>
internal sealed class ViteManifest
{
    private readonly Dictionary<string, Chunk> _chunks;
    private readonly Chunk _entry;
    private readonly string _base;
    private readonly IReadOnlyDictionary<string, string> _keys;

    private ViteManifest(Dictionary<string, Chunk> chunks, Chunk entry, string version, string basePath, IReadOnlyDictionary<string, string> keys)
    {
        _chunks = chunks;
        _entry = entry;
        _base = basePath;
        _keys = keys;
        Version = version;
        SourceFiles = chunks.Values.Select(c => c.Src).OfType<string>().ToList();
    }

    public IReadOnlyList<string> SourceFiles { get; }

    /// <summary>Identifies the build; the client hard-reloads when a navigation response comes from another build.</summary>
    public string Version { get; }

    /// <summary>Whether the build has a chunk for the source file <paramref name="key"/> (its path from the app's root).</summary>
    public bool Builds(string key) => _chunks.ContainsKey(key);

    /// <param name="json">The manifest.</param>
    /// <param name="basePath">The base path the assets are served under.</param>
    /// <param name="keys">
    /// The manifest key of each page package file by the file's id (<see cref="BuildRecord"/>), whose chunk is listed
    /// under its path from the app's root, not under its id.
    /// </param>
    public static ViteManifest Parse(string json, string basePath = "/", IReadOnlyDictionary<string, string>? keys = null)
    {
        var chunks = JsonSerializer.Deserialize<Dictionary<string, Chunk>>(json)
            ?? throw new InvalidOperationException("The Vite manifest is empty.");
        var entry = chunks.Values.SingleOrDefault(c => c.IsEntry)
            ?? throw new InvalidOperationException(
                "The Vite manifest has no entry chunk. Was the client built with the netjes() Vite plugin?");
        var version = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..12];
        return new ViteManifest(chunks, entry, version, basePath.EndsWith('/') ? basePath : basePath + "/", keys ?? new Dictionary<string, string>());
    }

    /// <summary>Collects the assets for the client entry plus the given modules (layouts and page, outermost first).</summary>
    public DocumentAssets AssetsFor(IEnumerable<string> modules)
    {
        var stylesheets = new List<string>();
        var preloads = new List<string>();
        var visited = new HashSet<Chunk>(ReferenceEqualityComparer.Instance) { _entry };

        AddCss(_entry, stylesheets);
        foreach (var import in _entry.Imports)
        {
            Visit(import, stylesheets, preloads, visited);
        }

        foreach (var module in modules)
        {
            Visit(_keys.GetValueOrDefault(module, module), stylesheets, preloads, visited);
        }

        return new DocumentAssets(stylesheets, preloads, [_base + _entry.File]);
    }

    private void Visit(string key, List<string> stylesheets, List<string> preloads, HashSet<Chunk> visited)
    {
        if (!_chunks.TryGetValue(key, out var chunk) || !visited.Add(chunk))
        {
            return;
        }

        preloads.Add(_base + chunk.File);
        AddCss(chunk, stylesheets);
        foreach (var import in chunk.Imports)
        {
            Visit(import, stylesheets, preloads, visited);
        }
    }

    private void AddCss(Chunk chunk, List<string> stylesheets)
    {
        foreach (var css in chunk.Css)
        {
            var href = _base + css;
            if (!stylesheets.Contains(href))
            {
                stylesheets.Add(href);
            }
        }
    }

    private sealed record Chunk(
        [property: JsonPropertyName("file")] string File,
        [property: JsonPropertyName("src")] string? Src,
        [property: JsonPropertyName("isEntry")] bool IsEntry,
        [property: JsonPropertyName("imports")] IReadOnlyList<string>? ImportList,
        [property: JsonPropertyName("css")] IReadOnlyList<string>? CssList)
    {
        public IReadOnlyList<string> Imports => ImportList ?? [];
        public IReadOnlyList<string> Css => CssList ?? [];
    }
}

/// <summary>
/// What the client build records about the page packages the app lists in <c>netjes({ packages })</c>, next to its
/// manifest: <c>dist/client/.vite/netjes.json</c>. Production has no <c>node_modules</c> to read them from, and a
/// manifest key alone does not say which package a file is of. Written only when packages are listed; never served.
/// </summary>
internal sealed record BuildRecord(
    [property: JsonPropertyName("packages")] IReadOnlyList<BuildRecordPackage> Packages)
{
    public static BuildRecord Parse(string json, string path)
    {
        BuildRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<BuildRecord>(json);
        }
        catch (JsonException ex)
        {
            throw Invalid(path, ex);
        }

        if (record?.Packages is null || record.Packages.Any(p =>
                p is null || string.IsNullOrEmpty(p.Name) || p.Version is null || p.Netjes is null || string.IsNullOrEmpty(p.Pages) || p.Files is null))
        {
            throw Invalid(path, null);
        }

        return record;
    }

    private static NetjesStartupException Invalid(string path, Exception? inner) => new(
        $"The client build's record of page packages, '{path}', is not one the netjes() Vite plugin writes. Build again: `npm run build`.", inner);
}

/// <summary>A page package in the <see cref="BuildRecord"/>.</summary>
/// <param name="Files">The id of each of its convention files, and the file's key in the manifest.</param>
internal sealed record BuildRecordPackage(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("netjes")] string Netjes,
    [property: JsonPropertyName("pages")] string Pages,
    [property: JsonPropertyName("files")] IReadOnlyDictionary<string, string> Files)
{
    public Routing.PagePackageInstall ToInstall() => new(Name, Version, Netjes, Pages, null) { Files = Files };
}
