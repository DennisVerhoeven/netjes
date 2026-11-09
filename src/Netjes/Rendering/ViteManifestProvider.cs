namespace Netjes.Rendering;

/// <summary>
/// Loads the production client manifest once, failing with instructions when the app has not been built, and the build
/// record of the page packages next to it.
/// </summary>
internal sealed class ViteManifestProvider(NetjesContext context)
{
    private readonly Lazy<(ViteManifest Manifest, BuildRecord? Record)> _build = new(() =>
    {
        if (!File.Exists(context.ManifestPath))
        {
            // Most often a new app started outside Development, so that comes first.
            throw new NetjesStartupException(
                $"No production build found at '{context.ManifestPath}'. To develop, run `npm run dev`, or start the app " +
                $"with ASPNETCORE_ENVIRONMENT=Development. To run in production, build first: `npm run build` in '{context.Root}'.");
        }

        var record = File.Exists(context.BuildRecordPath)
            ? BuildRecord.Parse(File.ReadAllText(context.BuildRecordPath), context.BuildRecordPath)
            : null;
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, key) in record?.Packages.SelectMany(p => p.Files) ?? [])
        {
            keys[id] = key;
        }

        return (ViteManifest.Parse(File.ReadAllText(context.ManifestPath), keys: keys), record);
    });

    public ViteManifest Manifest => _build.Value.Manifest;

    /// <summary>The page packages the client build holds; null when it lists none.</summary>
    public BuildRecord? Record => _build.Value.Record;
}
