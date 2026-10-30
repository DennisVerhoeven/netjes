using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Netjes.Images;

namespace Netjes;

/// <summary>The endpoints Netjes itself serves next to the pages: the image optimizer, and optionally the web vitals beacon.</summary>
internal sealed class BuiltInEndpoints
{
    // MapPages() may be called more than once.
    private bool _mapped;

    public static void MapOnce(IEndpointRouteBuilder endpoints)
    {
        var registration = endpoints.ServiceProvider.GetRequiredService<BuiltInEndpoints>();
        if (registration._mapped)
        {
            return;
        }

        registration._mapped = true;
        ImageEndpoint.Map(endpoints);
        if (endpoints.ServiceProvider.GetRequiredService<NetjesContext>().Options.CollectWebVitals)
        {
            WebVitalsEndpoint.Map(endpoints);
        }
    }
}
