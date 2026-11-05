using Microsoft.AspNetCore.OutputCaching;

namespace Netjes.Rendering;

/// <summary>
/// Requests that the output cache must neither answer from a stored entry nor store under the page's key. Each gets a
/// key value of its own under its reason, so no lookup finds a stored entry, and the output cache's lock (it holds back
/// the requests for a key while one of them renders) never makes two such requests wait for each other. The policy that
/// applies a reason also refuses storage once the response is known, so nothing is ever stored under such a key. A
/// reason must never reuse a key of the page rules (<c>netjes-build</c>, <c>netjes-path</c>, <c>netjes-query</c>,
/// <c>netjes-data</c>, <c>netjes-segments</c>, <c>netjes-segment-secret</c>): it would replace that part of the key.
/// </summary>
internal static class CacheBypass
{
    /// <summary>A request in draft mode (<see cref="DraftModeCachePolicy"/>).</summary>
    public const string Draft = "netjes-draft";

    /// <summary>The page rendered in place of a failed form post, with the visitor's errors and input (<see cref="CachePaths"/>).</summary>
    public const string FailedFormPost = "netjes-action-state";

    /// <summary>Gives the request a key value of its own under <paramref name="reason"/>.</summary>
    public static void Apply(OutputCacheContext context, string reason) =>
        context.CacheVaryByRules.VaryByValues[reason] = Guid.NewGuid().ToString("N");

    /// <summary>Whether the request has a key value of its own under <paramref name="reason"/>.</summary>
    public static bool Applied(OutputCacheContext context, string reason) =>
        context.CacheVaryByRules.VaryByValues.ContainsKey(reason);
}
