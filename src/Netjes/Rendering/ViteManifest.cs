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

    private ViteManifest(Dictionary<string, Chunk> chunks, Chunk entry, string version, string basePath)
    {
        _chunks = chunks;
        _entry = entry;
        _base = basePath;
        Version = version;
        SourceFiles = chunks.Values.Select(c => c.Src).OfType<string>().ToList();
    }

    public IReadOnlyList<string> SourceFiles { get; }

    /// <summary>Identifies the build; the client hard-reloads when a navigation response comes from another build.</summary>
    public string Version { get; }

    public static ViteManifest Parse(string json, string basePath = "/")
    {
        var chunks = JsonSerializer.Deserialize<Dictionary<string, Chunk>>(json)
            ?? throw new InvalidOperationException("The Vite manifest is empty.");
        var entry = chunks.Values.SingleOrDefault(c => c.IsEntry)
            ?? throw new InvalidOperationException(
                "The Vite manifest has no entry chunk. Was the client built with the netjes() Vite plugin?");
        var version = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..12];
        return new ViteManifest(chunks, entry, version, basePath.EndsWith('/') ? basePath : basePath + "/");
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
            Visit(module, stylesheets, preloads, visited);
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
