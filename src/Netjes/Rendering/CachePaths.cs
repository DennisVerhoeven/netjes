using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netjes.Routing;

namespace Netjes.Rendering;

/// <summary>
/// Output-cache policy for pages: tags each entry with its path (for <c>RevalidatePath</c>), keys it on the path and
/// query string as sent, never serves or stores a page rendered in place of a failed form post or for a query string
/// outside the page's <c>CacheQueryKeys</c>, and warns once per page when a render
/// it stores read <c>Request.Cookies</c>. It works wherever it runs among the request's policies: it only adds to the
/// key in <see cref="CacheRequestAsync"/> (a policy after it, such as ASP.NET Core's default one, sets the lookup and
/// storage flags again), and refuses storage once the response is known.
/// </summary>
internal sealed class CachePaths(ILogger<CachePaths> logger, IHostEnvironment environment) : IOutputCachePolicy
{
    private const string UnreadQueryKey = "Netjes.UnreadQuery";

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _warnedCookies = new(StringComparer.Ordinal);

    public static string TagFor(string path) => "netjes-path:" + Normalize(path);

    private static string Normalize(string path)
    {
        var withoutQuery = path.Split('?', 2)[0];
        var trimmed = withoutQuery.Length > 1 ? withoutQuery.TrimEnd('/') : withoutQuery;
        return trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// The page's <c>CacheQueryKeys</c> leave this request's query string out, so the output cache neither answers it nor
    /// stores what it renders.
    /// </summary>
    public static bool LeavesQueryUncached(HttpContext http) => http.Items.ContainsKey(UnreadQueryKey);

    /// <summary>
    /// Whether every segment of the raw query string (<paramref name="query"/>, empty or starting with <c>?</c>) has a name
    /// in <paramref name="keys"/>. The raw text decides, not <see cref="HttpRequest.Query"/>: the parser drops empty
    /// segments, but <c>?page=2&amp;</c> and <c>?page=2&amp;&amp;</c> are other query strings, each an entry of its own under
    /// the page rules, so anyone could fill the cache with them. A name is decoded as the parser decodes it, <c>+</c> as a
    /// space and then percent-escapes. An entry stays keyed on the query string as sent, so a name read differently from the
    /// parser could only store a page for its own URL or lose a hit, never serve one query string's page for another.
    /// </summary>
    internal static bool ReadsOnlyDeclaredKeys(string query, IReadOnlySet<string> keys)
    {
        if (query.Length == 0)
        {
            return true;
        }

        foreach (var segment in query[1..].Split('&'))
        {
            var valueStart = segment.IndexOf('=');
            var name = valueStart < 0 ? segment : segment[..valueStart];
            if (name.Length == 0 || !keys.Contains(Uri.UnescapeDataString(name.Replace('+', ' '))))
            {
                return false;
            }
        }

        return true;
    }

    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        if (IsFailedFormPost(context))
        {
            // The visitor's errors and input: never served from the cache, and never stored (ServeResponseAsync). The
            // key leaves out the path and query values, so it is never the stored page's; a value of its own for each
            // post also keeps visitors whose posts fail at once from waiting for each other (the output cache holds
            // back the requests for a key while one of them renders).
            CacheBypass.Apply(context, CacheBypass.FailedFormPost);
            return ValueTask.CompletedTask;
        }

        var http = context.HttpContext;
        var request = http.Request;
        if (http.GetEndpoint()?.Metadata.GetMetadata<CacheQueryKeysMetadata>() is { } queryKeys
            && !ReadsOnlyDeclaredKeys(request.QueryString.Value ?? "", queryKeys.Keys))
        {
            // A query string the page does not read: a stored copy would answer this one URL, and every made-up query
            // string would store another. The key value of its own finds no stored entry and lets no request wait for
            // another's render. A policy after this one allows storage again, so the flag refuses it later: in the
            // renderer before it builds the payload, which then keeps its antiforgery token under a policy other than
            // CachePage, and in ServeResponseAsync for a response no renderer wrote. Nothing is stored, so the tag, the
            // keys of the page rules and the watch on cookies are left out.
            CacheBypass.Apply(context, CacheBypass.UnreadQuery);
            http.Items[UnreadQueryKey] = true;
            return ValueTask.CompletedTask;
        }

        context.Tags.Add(TagFor(request.Path));
        // The default key upper-cases the path (unless UseCaseSensitivePaths), but route values reach loaders as sent,
        // and a loader may compare them exactly: /s/Ab and /s/aB can be different pages. It also upper-cases query
        // names and sorts repeated values, and a policy may key on some names only, but the payload repeats the URL as
        // sent (the client shows it in the address bar), so another query string must not be served it.
        context.CacheVaryByRules.VaryByValues["netjes-path"] = request.PathBase.Value + request.Path.Value;
        context.CacheVaryByRules.VaryByValues["netjes-query"] = request.QueryString.Value ?? "";
        WatchCookies(context);
        return ValueTask.CompletedTask;
    }

    /// <summary>The page is rendered in place of a failed form post, with its errors.</summary>
    private static bool IsFailedFormPost(OutputCacheContext context) =>
        context.HttpContext.Items.ContainsKey(Netjes.ServerActionResult.StateKey);

    /// <summary>
    /// The entry is shared by every visitor, so a render that reads a cookie (shared props, a loader, authentication
    /// placed after the output cache) is stored and served to everyone anyway, and the app hears about it once per page. Only reads through
    /// <c>Request.Cookies</c> are seen, not the raw <c>Cookie</c> header. In the Development environment nothing is
    /// stored, and the warning says what happens outside it, where the same render is.
    /// </summary>
    private void WatchCookies(OutputCacheContext context)
    {
        var features = context.HttpContext.Features;
        var cookies = features.Get<IRequestCookiesFeature>() ?? new RequestCookiesFeature(features);
        features.Set<IRequestCookiesFeature>(new CachedRenderCookies(cookies, name =>
        {
            // Draft mode decides lookup and storage itself, and its cookie is no per-visitor value to warn about.
            // The output cache removes its feature when it is done: a later read (a middleware outside it, after the
            // response was stored or served) changes nothing that is stored. Whether the render will be stored is
            // read now, not when the watch went in: policies after this one could still change it then.
            if (name == DraftMode.CookieName || !context.EnableOutputCaching || !context.AllowCacheStorage
                || features.Get<IOutputCacheFeature>()?.Context != context)
            {
                return;
            }

            var http = context.HttpContext;
            var page = http.GetEndpoint()?.DisplayName ?? http.Request.Path.Value ?? "";
            if (!_warnedCookies.TryAdd(page, true))
            {
                return;
            }

            var cookie = name is null ? "all the request cookies" : $"the request cookie '{name}'";
            if (environment.IsDevelopment())
            {
                // Pages are not stored there (PageCachePolicy), but the same render would be anywhere else.
                logger.LogWarning(
                    "{Page} is output-cached, and rendering it read {Cookie}. Nothing is stored in Development; " +
                    "outside Development the render is stored and served to every visitor, so one visitor's value " +
                    "reaches all of them. SharedProps and the loaders of cached pages must not depend on cookies: " +
                    "read per-visitor values on the client or do not cache the page. See the CachePage docs.",
                    page, cookie);
                return;
            }

            logger.LogWarning(
                "{Page} is output-cached, and rendering it read {Cookie}. The render was stored and will be served " +
                "to every visitor, so one visitor's value reaches all of them. SharedProps and the loaders of cached " +
                "pages must not depend on cookies: read per-visitor values on the client or do not cache the page. " +
                "See the CachePage docs.",
                page, cookie);
        }));
    }

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellation) => ValueTask.CompletedTask;

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        if (IsFailedFormPost(context) || LeavesQueryUncached(context.HttpContext))
        {
            context.AllowCacheStorage = false;
        }

        return ValueTask.CompletedTask;
    }
}
