using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Netjes.Rendering;

namespace Netjes.Routing;

/// <summary>
/// Production routes: every page module that made it into the client build. The app's come from the manifest, and each
/// mounted page package's from the build record, which names the manifest key of each of its files.
/// </summary>
internal sealed class ManifestRouteTableProvider(ViteManifestProvider manifest, NetjesContext context, RouteSources sources) : IRouteTableProvider
{
    private readonly Lock _gate = new();
    private Built? _built;

    private sealed record Built(RouteTable Table, int SourcesVersion);

    public RouteTable Current
    {
        get
        {
            if (Volatile.Read(ref _built) is { } built && built.SourcesVersion == sources.Version)
            {
                return built.Table;
            }

            lock (_gate)
            {
                var version = sources.Version;
                if (_built is not { } current || current.SourcesVersion != version)
                {
                    current = new Built(Build(sources.Mounts), version);
                    Volatile.Write(ref _built, current);
                }

                return current.Table;
            }
        }
    }

    public IChangeToken GetChangeToken() => NullChangeToken.Singleton;

    private RouteTable Build(IReadOnlyList<MountedPackage> mounts)
    {
        var built = manifest.Manifest;
        return RouteTable.Build(
            [PageSource.App(built.SourceFiles, context.AppDirectory), .. mounts.Select(m => m.SourceOf(m.Install.Files!.Keys))],
            sources.Overrides);
    }
}
