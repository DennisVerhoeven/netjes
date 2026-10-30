using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Netjes.Routing;

namespace Netjes.Rendering;

/// <summary>
/// Output-cache vary rules for pages (<see cref="PageCachePolicy"/>): a client payload and a document never share an entry, and a
/// payload is keyed by which of the page's layout segments the client holds, one character per layout loader. Never by
/// the raw <see cref="NetjesHeaders.Data"/> or <see cref="NetjesHeaders.Segments"/> values: anyone can make those up,
/// and the cache's size limit counts only bodies, so each made-up value would keep its text in memory for free. A
/// payload that reuses a segment is also keyed by a fingerprint of the secret that signs keys (never the secret), so
/// instances that sign with different secrets never serve each other's on a shared store. Full payloads, which reuse
/// nothing, and documents are shared by all of them.
/// </summary>
internal sealed class CachedSegments(IRouteTableProvider routes, LayoutLoaderRegistry layoutLoaders, IAssetResolver assets) : IOutputCachePolicy
{
    private const string HeldKey = "Netjes.HeldSegments";
    private const string ReusedKey = "Netjes.ReusedSegments";

    /// <summary>The renderer marked this segment reused in the payload: the client keeps its own props for it.</summary>
    public static void Reused(HttpContext http, string segmentId)
    {
        if (http.Items[ReusedKey] is not HashSet<string> reused)
        {
            http.Items[ReusedKey] = reused = new HashSet<string>(StringComparer.Ordinal);
        }

        reused.Add(segmentId);
    }

    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        var http = context.HttpContext;
        var isData = PageRenderer.IsDataRequest(http);
        context.CacheVaryByRules.VaryByValues["netjes-data"] = isData ? "1" : "0";
        if (isData)
        {
            var (held, mask) = HeldSegments(http);
            http.Items[HeldKey] = held;
            context.CacheVaryByRules.VaryByValues["netjes-segments"] = mask;
            if (held.Count > 0)
            {
                // A payload that reuses a segment names the visitor's key, which only instances with this secret
                // accept. Instances with another secret (each makes its own by default) may share the store, and must
                // not serve it to a visitor holding their own key.
                context.CacheVaryByRules.VaryByValues["netjes-segment-secret"] = layoutLoaders.SecretFingerprint;
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// For each layout loader of the endpoint's page, outermost first, whether the client sent the key the renderer
    /// will accept for it: what decides which segments the payload marks reused.
    /// </summary>
    private (HashSet<string> Held, string Mask) HeldSegments(HttpContext http)
    {
        var held = new HashSet<string>(StringComparer.Ordinal);
        var pattern = http.GetEndpoint()?.Metadata.GetMetadata<PageEndpointMetadata>()?.Pattern;
        if (pattern is null || routes.Current.Find(pattern) is not { } route)
        {
            return (held, "");
        }

        var offered = PageRenderer.ReusableKeys(http);
        var mask = new System.Text.StringBuilder();
        foreach (var segment in route.Segments.Where(segment => layoutLoaders.Find(segment.Id) is not null))
        {
            var holds = offered.Contains(layoutLoaders.KeyFor(segment, http, assets.Version));
            mask.Append(holds ? '1' : '0');
            if (holds)
            {
                held.Add(segment.Id);
            }
        }

        return (held, mask.ToString());
    }

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellation) => ValueTask.CompletedTask;

    /// <summary>
    /// Stores the response only if it reused exactly the segments its key says the client holds. Otherwise (the user
    /// changed after the cache ran, the endpoint rendered another route's page) a client without those segments could
    /// be served a payload that leaves their props out.
    /// </summary>
    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        var http = context.HttpContext;
        var held = http.Items[HeldKey] as HashSet<string>;
        var reused = http.Items[ReusedKey] as HashSet<string>;
        if (!(reused ?? []).SetEquals(held ?? []))
        {
            context.AllowCacheStorage = false;
        }

        return ValueTask.CompletedTask;
    }
}
