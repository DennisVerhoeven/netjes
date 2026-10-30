using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Netjes.Rendering;

namespace Netjes.Routing;

/// <summary>Production routes: every page module that made it into the client build.</summary>
internal sealed class ManifestRouteTableProvider(ViteManifestProvider manifest, NetjesContext context) : IRouteTableProvider
{
    private readonly Lazy<RouteTable> _table = new(() =>
        RouteTable.Build(manifest.Manifest.SourceFiles, context.AppDirectory));

    public RouteTable Current => _table.Value;

    public IChangeToken GetChangeToken() => NullChangeToken.Singleton;
}
