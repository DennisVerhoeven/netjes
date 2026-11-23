using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.WebUtilities;
using Netjes.Rendering;

namespace Netjes;

public static partial class Page
{
    private static readonly PathString NetjesEndpoints = "/_netjes";

    /// <summary>
    /// Sends a visitor who has to sign in to the sign-in page at <paramref name="path"/>, with the URL to come back to in
    /// the query value <paramref name="returnUrlParameter"/>: for an authentication event, such as cookie authentication's
    /// <c>OnRedirectToLogin</c> (<c>context =&gt; Page.RedirectToSignIn(context.HttpContext, "/sign-in")</c>). A document
    /// gets a redirect (307, or 303 after a form post) and a client navigation a redirect payload, which the client router
    /// follows without a full page load. A call to a server action, or to another of Netjes's own endpoints, has no page to
    /// come back to: it gets an empty 401, which reaches a script's call as a <c>ServerActionError</c>.
    /// </summary>
    /// <param name="context">The request that has to sign in.</param>
    /// <param name="path">The sign-in page: a path on this site without the base path, such as <c>/sign-in</c>, which may have a query.</param>
    /// <param name="returnUrlParameter">The query value that carries the URL to come back to. Default <c>returnUrl</c>.</param>
    /// <remarks>
    /// The URL to come back to is the request's path and query, without the base path, which <see cref="LocalRedirect"/>
    /// puts back. It is left out when it is not a local URL by <see cref="IsLocalUrl"/>. The answer goes out
    /// <c>Cache-Control: no-store, private</c>, since a signed-in visitor gets the page at the same URL.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a local URL, or <paramref name="returnUrlParameter"/> is empty.</exception>
    public static Task RedirectToSignIn(HttpContext context, string path, string returnUrlParameter = "returnUrl")
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!IsLocalUrl(path))
        {
            throw new ArgumentException($"The sign-in page '{path}' is not a path on this site: start it with one '/'.", nameof(path));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(returnUrlParameter);
        // Whoever signs in gets the page at this URL: the answer for this visitor is kept by no cache, the output cache
        // included, which would store a client navigation's redirect payload (a 200) for a cached page.
        NoStore.Force(context, "no-store, private");
        if (context.Features.Get<IOutputCacheFeature>() is { } outputCache)
        {
            outputCache.Context.AllowCacheStorage = false;
        }

        if (context.Request.Path.StartsWithSegments(NetjesEndpoints)
            || context.GetEndpoint()?.Metadata.GetMetadata<ServerActionMetadata>() is not null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        // Encoded again, as the browser sent it: decoded, a '?' or '#' in the path would end it.
        var returnUrl = context.Request.Path.ToUriComponent() is { Length: > 0 } requested ? requested : "/";
        returnUrl += context.Request.QueryString.ToUriComponent();
        var location = IsLocalUrl(returnUrl) ? QueryHelpers.AddQueryString(Normalize(path), returnUrlParameter, returnUrl) : Normalize(path);
        return new PageResult(PageResult.Kind.Redirect, location: location).ExecuteAsync(context);
    }
}
