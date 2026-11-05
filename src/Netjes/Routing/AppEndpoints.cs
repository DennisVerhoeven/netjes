using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticAssets;

namespace Netjes.Routing;

/// <summary>
/// On the endpoints Netjes maps for itself that are no pages: the antiforgery token, the vitals beacon and the image
/// optimizer, which <c>MapPages()</c> maps, and the locale redirect (<c>MapLocaleRedirect</c>).
/// <see cref="AppEndpoints"/> leaves them out of the app's endpoints. <c>MapSitemap</c> and <c>MapRobotsTxt</c> map
/// files that the app links to, and carry none.
/// </summary>
internal sealed class NetjesEndpointMetadata
{
    public static readonly NetjesEndpointMetadata Instance = new();
}

/// <summary>
/// The app's own endpoints, which the generated file gives typed URLs (<c>endpointUrl</c>). The route warning for an
/// endpoint that ties with a page makes its own selection (<see cref="RouteDiagnostics"/>): it must see the locale
/// redirect, which ties with <c>app/page.tsx</c>.
/// </summary>
internal static class AppEndpoints
{
    /// <summary>
    /// Whether the endpoint is one the app mapped for a URL of its own: it names an HTTP method, and it is no page, page
    /// action or server action (<see cref="PageEndpointMetadata"/>), no fallback (which has the last order), no static
    /// asset (<c>MapStaticAssets</c>), none of Netjes's own (<see cref="NetjesEndpointMetadata"/>), and no MVC
    /// conventional route, whose actions share one pattern and fill its parameters with their required values.
    /// </summary>
    public static bool IsTheAppsOwn(RouteEndpoint endpoint)
    {
        var metadata = endpoint.Metadata;
        var pattern = endpoint.RoutePattern;
        return metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods is { Count: > 0 }
            && metadata.GetMetadata<PageEndpointMetadata>() is null
            && endpoint.Order != int.MaxValue
            && metadata.GetMetadata<StaticAssetDescriptor>() is null
            && metadata.GetMetadata<NetjesEndpointMetadata>() is null
            && !pattern.RequiredValues.Keys.Any(key => pattern.GetParameter(key) is not null);
    }
}
