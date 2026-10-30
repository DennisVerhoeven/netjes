using Microsoft.AspNetCore.Http;

namespace Netjes.Rendering;

/// <summary>
/// A response no cache may keep, whatever the page or the app set: a draft, the client-rendered fallback, the page for a
/// failed form post, a server action's answer, an antiforgery token. Its <c>Cache-Control</c> is replaced, and the
/// headers a CDN or proxy reads ahead of <c>Cache-Control</c> are removed, since one that reads them would keep the
/// response all the same.
/// </summary>
internal static class NoStore
{
    private static readonly object Key = new();

    /// <summary>
    /// Besides every header whose name ends in <c>-Cache-Control</c> (RFC 9213's <c>CDN-Cache-Control</c>, and a CDN's
    /// own, such as <c>Cloudflare-CDN-Cache-Control</c>): <c>Surrogate-Control</c> (Fastly, Varnish), Akamai's
    /// <c>Edge-Control</c> and nginx's <c>X-Accel-Expires</c>.
    /// </summary>
    private static readonly string[] Names = ["Surrogate-Control", "Edge-Control", "X-Accel-Expires"];

    private const string TargetedSuffix = "-Cache-Control";

    /// <summary>Sends this response with <paramref name="cacheControl"/> and none of the CDN headers, now and as it starts.</summary>
    public static void Force(HttpContext http, string cacheControl)
    {
        http.Items[Key] = cacheControl;
        Apply(http.Response.Headers, cacheControl);
    }

    /// <summary>
    /// Applies what <see cref="Force"/> asked for once more as the response starts. Callbacks run in the reverse order
    /// of their registration, so this one, registered before routing, comes after those of the page's loaders and of
    /// middleware after <c>UseNetjes</c>, and undoes what they set.
    /// </summary>
    public static Task KeepForced(HttpContext http, RequestDelegate next)
    {
        http.Response.OnStarting(static state =>
        {
            var http = (HttpContext)state;
            if (http.Items.TryGetValue(Key, out var value) && value is string cacheControl)
            {
                Apply(http.Response.Headers, cacheControl);
            }

            return Task.CompletedTask;
        }, http);
        return next(http);
    }

    private static void Apply(IHeaderDictionary headers, string cacheControl)
    {
        headers.CacheControl = cacheControl;
        foreach (var name in Names)
        {
            headers.Remove(name);
        }

        // Header names are case-insensitive, as the dictionary's own lookups are.
        foreach (var name in headers.Keys.Where(n => n.EndsWith(TargetedSuffix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            headers.Remove(name);
        }
    }
}
