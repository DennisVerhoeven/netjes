using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Netjes.Tests.Endpoints;

/// <summary>
/// What a page sends to let every cache keep it for ten minutes: <c>Cache-Control</c>, and the headers a CDN or proxy
/// reads ahead of it. Where Netjes sends a page <c>no-store</c> whatever the page allows (a draft, the client-rendered
/// fallback, a failed form post), none of those may go out, or a CDN that reads one keeps what no cache may.
/// </summary>
internal static class CdnCacheHeaders
{
    public static readonly string[] Names =
    [
        "CDN-Cache-Control",
        "Cloudflare-CDN-Cache-Control",
        "Vercel-CDN-Cache-Control",
        "Akamai-Cache-Control",
        "Surrogate-Control",
        "Edge-Control",
        "X-Accel-Expires",
    ];

    /// <summary>Header names are case-insensitive: a page can write them in any casing.</summary>
    public static readonly string[] Casings = ["as written", "lower", "upper"];

    /// <summary>Where the headers come from: the page's loader, or middleware, either at once or as the response starts.</summary>
    public static readonly string[] Sources =
    [
        "loader",
        "loader, as the response starts",
        "middleware before UseNetjes",
        "middleware after UseNetjes, as the response starts",
    ];

    private static string InCasing(string name, string casing) => casing switch
    {
        "lower" => name.ToLowerInvariant(),
        "upper" => name.ToUpperInvariant(),
        _ => name,
    };

    public static void Set(HttpResponse response, string casing)
    {
        response.Headers[InCasing("Cache-Control", casing)] = "public, max-age=600";
        foreach (var name in Names)
        {
            response.Headers[InCasing(name, casing)] = name == "X-Accel-Expires" ? "600" : "max-age=600";
        }
    }

    /// <summary>For a loader: sets them now, or registers a callback that sets them as the response starts.</summary>
    public static void FromLoader(HttpContext http, string source, string casing)
    {
        if (source == "loader")
        {
            Set(http.Response, casing);
        }
        else if (source == "loader, as the response starts")
        {
            http.Response.OnStarting(() =>
            {
                Set(http.Response, casing);
                return Task.CompletedTask;
            });
        }
    }

    public static Action<WebApplication>? BeforeNetjes(string source, string casing) =>
        source != "middleware before UseNetjes"
            ? null
            : a => a.Use((http, next) =>
            {
                Set(http.Response, casing);
                return next(http);
            });

    public static void AfterNetjes(WebApplication app, string source, string casing)
    {
        if (source != "middleware after UseNetjes, as the response starts")
        {
            return;
        }

        app.Use((http, next) =>
        {
            http.Response.OnStarting(() =>
            {
                Set(http.Response, casing);
                return Task.CompletedTask;
            });
            return next(http);
        });
    }

    /// <summary>The CDN headers the response carries, by the name it sent them under.</summary>
    public static List<string> In(HttpResponseMessage response) =>
        [.. response.Headers.Concat(response.Content.Headers)
            .Select(h => h.Key)
            .Where(key => Names.Contains(key, StringComparer.OrdinalIgnoreCase))];

    public static TheoryData<string, string, bool> SourcesCasingsAndCaching()
    {
        var data = new TheoryData<string, string, bool>();
        foreach (var source in Sources)
        {
            foreach (var casing in Casings)
            {
                data.Add(source, casing, false);
                data.Add(source, casing, true);
            }
        }

        return data;
    }
}
