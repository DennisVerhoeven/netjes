using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Netjes.Rendering;

namespace Netjes.Routing;

/// <summary>
/// After routing: the client router's request for the URL a redirect sent it to (<see cref="NetjesHeaders.Redirected"/>)
/// goes on to a page only. Where routing chose anything else, a file or another endpoint of the app, the not-found
/// fallback or nothing, it gets <c>{ "kind": "document" }</c> and the router loads the URL as a document. The endpoint
/// runs once, for that load, instead of once for the router's request and again for the document; a signed link that
/// counts its uses, or a download, is not spent on a request the router throws away.
/// </summary>
internal static class RedirectTargets
{
    private const string LoadAsDocument = """{"kind":"document"}""";

    public static Task InvokeAsync(HttpContext http, RequestDelegate next) =>
        IsRedirectTarget(http) && http.GetEndpoint()?.Metadata.GetMetadata<PageEndpointMetadata>() is not { Pattern: not null }
            ? AnswerAsync(http)
            : next(http);

    /// <summary>A GET of the client router's, for the URL a redirect sent it to.</summary>
    private static bool IsRedirectTarget(HttpContext http) =>
        HttpMethods.IsGet(http.Request.Method)
        && PageRenderer.IsDataRequest(http)
        && http.Request.Headers.ContainsKey(NetjesHeaders.Redirected);

    private static async Task AnswerAsync(HttpContext http)
    {
        // The same URL answers a document load with what its endpoint sends: no cache may keep this answer for it.
        if (http.Features.Get<IOutputCacheFeature>() is { } outputCache)
        {
            outputCache.Context.AllowCacheStorage = false;
        }

        NoStore.Force(http, "no-store, private");
        http.Response.Headers.Append(HeaderNames.Vary, new StringValues([NetjesHeaders.Data, NetjesHeaders.Redirected]));
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "application/json; charset=utf-8";
        await http.Response.WriteAsync(LoadAsDocument, http.RequestAborted);
    }
}
