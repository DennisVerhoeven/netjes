using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Netjes.Routing;

namespace Netjes.Rendering;

/// <summary>
/// The output-cache rules of pages, whichever policy caches them: <c>CachePage</c>, ASP.NET Core's <c>CacheOutput</c> in
/// any form, an <c>[OutputCache]</c> attribute or a base policy of the app. <see cref="CachePaths"/> keys an entry on
/// the path and query string as sent and keeps failed form posts out; <see cref="CachedSegments"/> keeps documents and payloads apart
/// and keys a payload by the layouts the client holds. Without them, a document request could be served a stored
/// payload, and a client a payload that leaves out a layout it does not have. Every entry is also keyed by the client
/// build (<see cref="IAssetResolver.Version"/>), for instances on different builds sharing a store during a rolling
/// deploy: a document names its build's assets, which an instance on another build may not have, and a payload from
/// another build makes the client load the page again. A base policy, so it runs before the endpoint's own; none of
/// the rules depends on running last. It applies to page endpoints and to endpoints <c>CachePage</c> caches (a minimal
/// API returning <c>Page.Render</c>).
/// </summary>
internal sealed class PageCachePolicy : IOutputCachePolicy
{
    private const string AppliedKey = "Netjes.PageCacheRules";
    private const string BeforeRoutingKey = "Netjes.PageCacheBeforeRouting";

    /// <summary>The page rules keyed this request, so a page rendered for it can be stored.</summary>
    public static bool Applied(HttpContext http) => http.Items.ContainsKey(AppliedKey);

    /// <summary>The output cache ran before routing chose the endpoint (<c>UseOutputCache()</c> before <c>UseNetjes()</c>).</summary>
    public static bool RanBeforeRouting(HttpContext http) => http.Items.ContainsKey(BeforeRoutingKey);

    public async ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        var http = context.HttpContext;
        if (http.GetEndpoint() is not { } endpoint)
        {
            http.Items[BeforeRoutingKey] = true;
            return;
        }

        if (endpoint.Metadata.GetMetadata<PageEndpointMetadata>() is null && endpoint.Metadata.GetMetadata<CachedPageMetadata>() is null)
        {
            return;
        }

        http.Items[AppliedKey] = true;
        context.CacheVaryByRules.VaryByValues["netjes-build"] = http.RequestServices.GetRequiredService<IAssetResolver>().Version;
        await Paths(http).CacheRequestAsync(context, cancellation);
        await Segments(http).CacheRequestAsync(context, cancellation);
    }

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellation) => ValueTask.CompletedTask;

    public async ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        var http = context.HttpContext;
        if (!Applied(http))
        {
            return;
        }

        await Paths(http).ServeResponseAsync(context, cancellation);
        await Segments(http).ServeResponseAsync(context, cancellation);
    }

    private static CachePaths Paths(HttpContext http) => http.RequestServices.GetRequiredService<CachePaths>();

    private static CachedSegments Segments(HttpContext http) => http.RequestServices.GetRequiredService<CachedSegments>();
}

/// <summary>Adds <see cref="PageCachePolicy"/> to every output-cached request.</summary>
internal sealed class PageCacheBasePolicySetup : Microsoft.Extensions.Options.IConfigureOptions<OutputCacheOptions>
{
    public void Configure(OutputCacheOptions options) => options.AddBasePolicy(new PageCachePolicy());
}
