namespace Netjes.Rendering;

/// <summary>Loads the production client manifest once, failing with instructions when the app has not been built.</summary>
internal sealed class ViteManifestProvider(NetjesContext context)
{
    private readonly Lazy<ViteManifest> _manifest = new(() =>
    {
        if (!File.Exists(context.ManifestPath))
        {
            // Most often a new app started outside Development, so that comes first.
            throw new NetjesStartupException(
                $"No production build found at '{context.ManifestPath}'. To develop, run `npm run dev`, or start the app " +
                $"with ASPNETCORE_ENVIRONMENT=Development. To run in production, build first: `npm run build` in '{context.Root}'.");
        }

        return ViteManifest.Parse(File.ReadAllText(context.ManifestPath));
    });

    public ViteManifest Manifest => _manifest.Value;
}
