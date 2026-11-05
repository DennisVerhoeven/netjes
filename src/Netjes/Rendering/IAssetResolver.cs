using Netjes.Sidecar;

namespace Netjes.Rendering;

internal interface IAssetResolver
{
    /// <summary>Identifies the client build; a client on another version reloads instead of navigating.</summary>
    string Version { get; }

    /// <summary>The origin of the Vite dev server, which documents load their scripts from in Development; null for a client build.</summary>
    string? DevServerOrigin => null;

    /// <summary>Assets for a document showing the given modules (layouts and page, outermost first).</summary>
    DocumentAssets Resolve(IReadOnlyList<string> modules);
}

/// <summary>Points the browser at the Vite dev server, which compiles modules and injects CSS on demand.</summary>
internal sealed class DevAssetResolver(NetjesContext context, NodeSidecar sidecar) : IAssetResolver
{
    public string Version => "development";

    public string? DevServerOrigin => sidecar.ViteOrigin ?? $"http://localhost:{context.Options.ViteDevPort}";

    public DocumentAssets Resolve(IReadOnlyList<string> modules)
    {
        var origin = DevServerOrigin;
        return new DocumentAssets([], [], [$"{origin}/@vite/client", $"{origin}/@netjes/client-entry"]);
    }
}

internal sealed class ManifestAssetResolver(ViteManifestProvider manifest) : IAssetResolver
{
    public string Version => manifest.Manifest.Version;

    public DocumentAssets Resolve(IReadOnlyList<string> modules) => manifest.Manifest.AssetsFor(modules);
}
