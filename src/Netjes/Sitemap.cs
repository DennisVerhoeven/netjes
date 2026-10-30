using System.Globalization;
using System.Text;
using System.Xml;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netjes.Export;
using Netjes.Routing;

namespace Netjes;

internal sealed class ExcludeFromSitemapMetadata;

/// <summary>
/// What <c>MapSitemap</c> keeps between requests: the page paths (so <c>StaticParams</c> runs once per lifetime) and the
/// XML built from them for a few base paths. A client can choose the base path through <c>X-Forwarded-Prefix</c>, so
/// the XML is stored for at most <see cref="MaxBasePaths"/> of them; any other base path gets its XML built from the
/// stored paths on each request. A base path longer than <see cref="MaxBasePathLength"/> gets none.
/// </summary>
internal sealed class SitemapCache(string root, TimeSpan lifetime)
{
    internal const int MaxBasePaths = 8;

    /// <summary>
    /// The longest base path, as the XML carries it (<see cref="LengthInXml"/>), that a sitemap is built for. The XML
    /// repeats it in every URL, so a client that chose a longer one would get, for one small request, a large answer
    /// built anew each time.
    /// </summary>
    internal const int MaxBasePathLength = 256;

    /// <summary>
    /// The length of a percent-encoded base path in the XML: a URL path may hold <c>&amp;</c> as it is, and the XML
    /// writes each one as <c>&amp;amp;</c>. Percent-encoding leaves no other character the XML escapes.
    /// </summary>
    internal static int LengthInXml(string basePath) => basePath.Length + 4 * basePath.Count(c => c == '&');

    private sealed record Snapshot(List<string> Paths, DateTimeOffset At)
    {
        public Dictionary<string, byte[]> Xml { get; } = new(StringComparer.Ordinal);
    }

    private readonly object gate = new();
    private Snapshot? current;

    /// <summary>How many base paths have their XML stored.</summary>
    internal int Count
    {
        get
        {
            lock (gate)
            {
                return current?.Xml.Count ?? 0;
            }
        }
    }

    /// <summary>The XML for <paramref name="basePath"/>, or null, without building anything, for one too long.</summary>
    public async Task<byte[]?> GetAsync(string basePath, DateTimeOffset now, Func<Task<List<string>>> buildPaths)
    {
        if (LengthInXml(basePath) > MaxBasePathLength)
        {
            return null;
        }

        Snapshot? snapshot;
        lock (gate)
        {
            snapshot = current is { } s && now - s.At < lifetime ? s : null;
        }

        if (snapshot is null)
        {
            var fresh = new Snapshot(await buildPaths(), now);
            lock (gate)
            {
                snapshot = current is { } s && now - s.At < lifetime ? s : current = fresh;
            }
        }

        lock (gate)
        {
            if (snapshot.Xml.TryGetValue(basePath, out var xml))
            {
                return xml;
            }
        }

        var built = SitemapEndpointRouteBuilderExtensions.Xml(root + basePath, snapshot.Paths);
        lock (gate)
        {
            if (snapshot.Xml.Count < MaxBasePaths)
            {
                snapshot.Xml.TryAdd(basePath, built);
            }
        }

        return built;
    }
}

/// <summary>
/// <c>sitemap.xml</c> and <c>robots.txt</c> from the route table, like Next.js's <c>app/sitemap.ts</c> and
/// <c>app/robots.ts</c>: every public page, dynamic ones through their <c>.StaticParams(...)</c>.
/// </summary>
public static class SitemapEndpointRouteBuilderExtensions
{
    /// <summary>Leaves the page out of <c>MapSitemap</c>'s sitemap.xml.</summary>
    public static TBuilder ExcludeFromSitemap<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpoint => endpoint.Metadata.Add(new ExcludeFromSitemapMetadata()));
        return builder;
    }

    /// <summary>
    /// Serves <c>/sitemap.xml</c>: pages without parameters, and dynamic pages with <c>.StaticParams(...)</c>, as
    /// absolute URLs under <paramref name="siteUrl"/> (and <c>UsePathBase</c>'s base path). Pages that require
    /// authorization (their own, or the fallback policy's unless they are <c>.AllowAnonymous()</c>) or call
    /// <c>.ExcludeFromSitemap()</c> are left out. The result is kept for <paramref name="cacheFor"/> (default an hour),
    /// so <c>StaticParams</c> does not run on every request, and browsers and CDNs may keep it as long. Under a base path
    /// longer than 256 characters as the XML writes it (percent-encoded, with <c>&amp;</c> as <c>&amp;amp;</c>), it is
    /// not found: each URL repeats the base path, which a client can choose through an <c>X-Forwarded-Prefix</c> the app
    /// accepts.
    /// </summary>
    public static IEndpointConventionBuilder MapSitemap(this IEndpointRouteBuilder endpoints, Uri siteUrl, TimeSpan? cacheFor = null)
    {
        var root = siteUrl.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var lifetime = cacheFor ?? TimeSpan.FromHours(1);
        var cache = new SitemapCache(root, lifetime);
        var logger = endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Netjes.Sitemap");
        return endpoints.MapGet("/sitemap.xml", async (HttpContext http, IRouteTableProvider routes, EndpointDataSource all, TimeProvider time) =>
            {
                var basePath = http.Request.PathBase.ToUriComponent();
                var xml = await cache.GetAsync(basePath, time.GetUtcNow(), () => PublicPathsAsync(http, routes, all));
                if (xml is null)
                {
                    logger.LogDebug(
                        "Not serving sitemap.xml under a base path of {Length} characters, over the {Limit} its URLs may carry",
                        SitemapCache.LengthInXml(basePath), SitemapCache.MaxBasePathLength);
                    // A CDN in front does not key its cache by the forwarded prefix: it must not keep this for everyone.
                    http.Response.Headers.CacheControl = "no-store";
                    return Results.NotFound();
                }

                http.Response.Headers.CacheControl = $"public, max-age={(long)lifetime.TotalSeconds}";
                return Results.Bytes(xml, "application/xml; charset=utf-8");
            })
            .AllowAnonymous()
            .WithDisplayName("Netjes sitemap.xml")
            .ExcludeFromDescription();
    }

    private static async Task<List<string>> PublicPathsAsync(HttpContext http, IRouteTableProvider routes, EndpointDataSource all)
    {
        // A fallback policy protects every endpoint without authorization metadata of its own.
        var fallback = http.RequestServices.GetService<IAuthorizationPolicyProvider>() is { } policies
            ? await policies.GetFallbackPolicyAsync()
            : null;
        var pages = all.Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<PageEndpointMetadata>()?.Pattern is not null)
            .Where(e => e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(HttpMethods.Get) ?? true)
            .GroupBy(e => e.Metadata.GetMetadata<PageEndpointMetadata>()!.Pattern!)
            .ToDictionary(g => g.Key, g => g.First());
        var urls = new List<string>();
        foreach (var page in routes.Current.Pages)
        {
            if (!pages.TryGetValue(page.Pattern, out var endpoint)
                || endpoint.Metadata.GetMetadata<ExcludeFromSitemapMetadata>() is not null
                || (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null
                    && (endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null || fallback is not null)))
            {
                continue;
            }

            if (page.Parameters.Count == 0)
            {
                urls.Add(page.Pattern);
            }
            else if (endpoint.Metadata.GetMetadata<StaticParamsMetadata>() is { } staticParams)
            {
                urls.AddRange(staticParams.Parameters(http.RequestServices).Select(values => PathFor(page, new RouteValueDictionary(values))));
            }
        }

        return urls;
    }

    /// <summary>Serves <c>/robots.txt</c>: allow everything except <paramref name="disallow"/>, and point at the sitemap, under <paramref name="siteUrl"/> and <c>UsePathBase</c>'s base path like <see cref="MapSitemap"/>.</summary>
    public static IEndpointConventionBuilder MapRobotsTxt(this IEndpointRouteBuilder endpoints, Uri siteUrl, IReadOnlyList<string>? disallow = null)
    {
        var text = new StringBuilder("User-agent: *\n");
        foreach (var path in disallow ?? [])
        {
            text.Append("Disallow: ").Append(path).Append('\n');
        }

        if (disallow is not { Count: > 0 })
        {
            text.Append("Allow: /\n");
        }

        var head = text.ToString();
        var root = siteUrl.GetLeftPart(UriPartial.Path).TrimEnd('/');
        // The sitemap's URL is built per request from the base path, as MapSitemap does for its pages. ToUriComponent keeps
        // a base path from a forwarded header from adding lines to this line-based file.
        return endpoints.MapGet("/robots.txt", (HttpContext http) =>
                Results.Text($"{head}\nSitemap: {root}{http.Request.PathBase.ToUriComponent()}/sitemap.xml\n", "text/plain", Encoding.UTF8))
            .AllowAnonymous()
            .WithDisplayName("Netjes robots.txt")
            .ExcludeFromDescription();
    }

    internal static byte[] Xml(string root, IEnumerable<string> paths)
    {
        // Through a UTF-8 stream, so the declaration says utf-8 (a StringBuilder would make it utf-16).
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) }))
        {
            writer.WriteStartElement("urlset", "http://www.sitemaps.org/schemas/sitemap/0.9");
            foreach (var path in paths.Distinct(StringComparer.Ordinal))
            {
                writer.WriteStartElement("url");
                writer.WriteElementString("loc", root + (path == "/" ? "/" : path));
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        return output.ToArray();
    }

    /// <summary>The page's path for a set of route values; catch-all values are arrays of segments.</summary>
    internal static string PathFor(PageRoute page, RouteValueDictionary values)
    {
        var parameters = page.Parameters.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var segments = new List<string>();
        foreach (var segment in page.Pattern.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = segment.Trim('[', ']').TrimStart('.');
            if (!segment.StartsWith('[') || !parameters.TryGetValue(name, out var parameter))
            {
                segments.Add(segment);
                continue;
            }

            values.TryGetValue(parameter.Name, out var value);
            var parts = value switch
            {
                null => [],
                string text when parameter.Kind != RouteParameterKind.Single => text.Split('/', StringSplitOptions.RemoveEmptyEntries),
                string text => [text],
                System.Collections.IEnumerable items => items.Cast<object?>().Select(i => Convert.ToString(i, CultureInfo.InvariantCulture) ?? "").ToArray(),
                _ => [Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""],
            };
            segments.AddRange(parts.Select(Uri.EscapeDataString));
        }

        return "/" + string.Join('/', segments);
    }
}
