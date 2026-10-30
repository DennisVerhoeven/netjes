using Microsoft.Extensions.Primitives;

namespace Netjes.Routing;

internal interface IRouteTableProvider
{
    RouteTable Current { get; }

    /// <summary>Fires when <see cref="Current"/> has been replaced (page files added or removed in development).</summary>
    IChangeToken GetChangeToken();
}
