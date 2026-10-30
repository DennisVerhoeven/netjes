using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.DependencyInjection;
using Netjes.Routing;

namespace Netjes.Rendering;

/// <summary>
/// Runs the rest of the pipeline again for another URL in the same request, like
/// <c>UseStatusCodePagesWithReExecute</c>: a failed no-JavaScript form post renders the form's page with its errors.
/// </summary>
internal sealed class ReExecute
{
    private static readonly object ReExecuted = new();

    public static Task GetAsync(HttpContext http, string url)
    {
        var queryStart = url.IndexOf('?');
        var path = new PathString(queryStart < 0 ? url : url[..queryStart]);
        var query = queryStart < 0 ? QueryString.Empty : new QueryString(url[queryStart..]);
        // The return URL is what the browser saw, base path included; the pipeline runs below it.
        if (http.Request.PathBase.HasValue && path.StartsWithSegments(http.Request.PathBase, out var rest))
        {
            path = rest;
        }

        return GetAsync(http, path, query);
    }

    /// <summary>The same for a path below the base path, as the server read it from a request (a page action's own).</summary>
    public static async Task GetAsync(HttpContext http, PathString path, QueryString query)
    {
        var reExecute = http.Features.Get<ReExecute>()
            ?? throw new InvalidOperationException("Call app.UseNetjes() before mapping server actions.");
        if (IsShortCircuit(http, path))
        {
            (path, query) = ("/", QueryString.Empty);
        }

        http.Request.Method = HttpMethods.Get;
        http.Request.Path = path;
        http.Request.QueryString = query;
        http.Request.Headers.ContentType = default;
        http.Request.Headers.ContentLength = null;
        http.Request.RouteValues.Clear();
        http.SetEndpoint(null);
        // Caching middleware already ran for the POST and refuses to run twice; the page rendered for a failed post
        // must not be cached anyway.
        http.Features.Set<Microsoft.AspNetCore.OutputCaching.IOutputCacheFeature>(null);
        http.Features.Set<Microsoft.AspNetCore.ResponseCaching.IResponseCachingFeature>(null);
        http.Items[ReExecuted] = true;
        await reExecute.Next(http);
    }

    /// <summary>
    /// Whether a short-circuit endpoint (<c>.ShortCircuit()</c>, <c>MapShortCircuit</c>) may answer the path: routing runs
    /// one itself, before <see cref="PagesOnly"/> could turn it away. Route constraints are not checked, so a page such
    /// an endpoint would shadow falls back too.
    /// </summary>
    private static bool IsShortCircuit(HttpContext http, PathString path) =>
        http.RequestServices.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            // ASP.NET Core's marker for them is internal.
            .Where(e => e.Metadata.Any(m => m.GetType().FullName == "Microsoft.AspNetCore.Routing.ShortCircuit.ShortCircuitMetadata"))
            .Any(e => new TemplateMatcher(new RouteTemplate(e.RoutePattern), new RouteValueDictionary(e.RoutePattern.Defaults))
                .TryMatch(path, []));

    /// <summary>
    /// After routing: a re-executed request goes on only to a page, else it renders the home page, or the error page
    /// (422) where the home is no page either. Middleware before <c>UseNetjes</c> does not run again for it, so another
    /// endpoint (a status page behind an IP allow-list, say) would answer without it.
    /// </summary>
    public static Task PagesOnly(HttpContext http, RequestDelegate next) =>
        !http.Items.ContainsKey(ReExecuted) || http.GetEndpoint()?.Metadata.GetMetadata<PageEndpointMetadata>() is { Pattern: not null }
            ? next(http)
            : http.Request.Path == "/"
                ? http.RequestServices.GetRequiredService<PageRenderer>().RenderStatusAsync(http, StatusCodes.Status422UnprocessableEntity)
                : GetAsync(http, "/");

    /// <summary>Where a re-executed request starts: at routing, so it never reaches the static files.</summary>
    public RequestDelegate Next { get; private set; } = _ => Task.CompletedTask;

    /// <summary>Marks the pipeline's position <see cref="Next"/> starts from.</summary>
    public RequestDelegate StartHere(RequestDelegate next) => Next = next;
}
