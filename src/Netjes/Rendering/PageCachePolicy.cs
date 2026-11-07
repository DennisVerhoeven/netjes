using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netjes.Routing;

namespace Netjes.Rendering;

/// <summary>
/// The output-cache rules of pages, whichever policy caches them: <c>CachePage</c>, ASP.NET Core's <c>CacheOutput</c> in
/// any form, an <c>[OutputCache]</c> attribute or a base policy of the app. <see cref="CachePaths"/> keys an entry on
/// the path and query string as sent and keeps failed form posts and query strings outside <c>CacheQueryKeys</c> out;
/// <see cref="CachedSegments"/> keeps documents and payloads apart
/// and keys a payload by the layouts the client holds. Without them, a document request could be served a stored
/// payload, and a client a payload that leaves out a layout it does not have. Every entry is also keyed by the client
/// build (<see cref="IAssetResolver.Version"/>), for instances on different builds sharing a store during a rolling
/// deploy: a document names its build's assets, which an instance on another build may not have, and a payload from
/// another build makes the client load the page again. A base policy, so it runs before the endpoint's own; none of
/// the rules depends on running last. It applies to page endpoints and to endpoints <c>CachePage</c> caches (a minimal
/// API returning <c>Page.Render</c>).
/// <para>
/// In the Development environment no page is served from the cache or stored in it (ADR 0023). Code is live there
/// (Vite serves the sources, the sidecar imports them again, hot reload swaps loaders), but the client build in the key
/// stays <c>development</c>: a stored page would outlive every edit, and its markup and styles would no longer match
/// the modules the browser loads. The environment decides, not <see cref="NetjesOptions.Mode"/>: tests run Netjes's
/// development mode in other environments to test caching. Everything else is as in production, so the keys are made,
/// a page the cache could store leaves its antiforgery token out, and the renderer's warnings fire; the response goes
/// out <c>no-store</c> instead of being stored.
/// </para>
/// </summary>
internal sealed class PageCachePolicy(bool development, ILogger<PageCachePolicy> logger) : IOutputCachePolicy
{
    private const string AppliedKey = "Netjes.PageCacheRules";
    private const string BeforeRoutingKey = "Netjes.PageCacheBeforeRouting";
    private const string DevelopmentKey = "Netjes.PageCacheDevelopment";

    private int _toldOfDevelopment;

    /// <summary>The page rules keyed this request, so a page rendered for it can be stored.</summary>
    public static bool Applied(HttpContext http) => http.Items.ContainsKey(AppliedKey);

    /// <summary>The output cache ran before routing chose the endpoint (<c>UseOutputCache()</c> before <c>UseNetjes()</c>).</summary>
    public static bool RanBeforeRouting(HttpContext http) => http.Items.ContainsKey(BeforeRoutingKey);

    /// <summary>
    /// The page rules keyed this request in the Development environment, so the page renders for it and is not stored,
    /// whatever the output cache allows while it renders.
    /// </summary>
    public static bool RendersEachTimeInDevelopment(HttpContext http) => http.Items.ContainsKey(DevelopmentKey);

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
        if (development)
        {
            // A key of its own: no lookup finds a stored entry, and no request waits behind another's render. Storage
            // stays allowed while the page renders, as it would be elsewhere (a policy after this one allows it again
            // anyway), since the renderer decides the antiforgery token and its warnings from it; it is refused once
            // the response is known.
            CacheBypass.Apply(context, CacheBypass.Development);
            http.Items[DevelopmentKey] = true;
        }
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
        if (RendersEachTimeInDevelopment(http) && context.AllowCacheStorage)
        {
            context.AllowCacheStorage = false;
            if (Interlocked.Exchange(ref _toldOfDevelopment, 1) == 0)
            {
                logger.LogInformation(
                    "Pages are not output-cached in the Development environment: {Page} and every other page " +
                    "render on each request, nothing is stored, and they go out no-store. Outside Development, " +
                    "CachePage and output caching store them as usual.",
                    http.GetEndpoint()?.DisplayName ?? http.Request.Path.Value);
            }
        }
    }

    private static CachePaths Paths(HttpContext http) => http.RequestServices.GetRequiredService<CachePaths>();

    private static CachedSegments Segments(HttpContext http) => http.RequestServices.GetRequiredService<CachedSegments>();
}

/// <summary>Adds <see cref="PageCachePolicy"/> to every output-cached request.</summary>
internal sealed class PageCacheBasePolicySetup(IHostEnvironment environment, ILogger<PageCachePolicy> logger)
    : Microsoft.Extensions.Options.IConfigureOptions<OutputCacheOptions>
{
    public void Configure(OutputCacheOptions options) =>
        options.AddBasePolicy(new PageCachePolicy(environment.IsDevelopment(), logger));
}
