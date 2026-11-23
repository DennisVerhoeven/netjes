using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Netjes.Rendering;

namespace Netjes;

public static partial class Page
{
    /// <summary>The form's error after <see cref="TooManyRequests"/>, when the app gives none.</summary>
    internal const string TooManyRequestsMessage = "Too many requests. Try again later.";

    /// <summary>
    /// Answers a request the rate limiter refused with status 429: for the limiter's <c>OnRejected</c>
    /// (<c>limiter.OnRejected = (context, _) =&gt; Page.TooManyRequests(context)</c>), or a policy's. A form keeps what was
    /// typed in it: a page action's post renders its page again from its loader, with and without JavaScript, and
    /// <c>usePageActionState()</c> reports the failure as it reports a failed validation, with status 429 and
    /// <paramref name="message"/> as the form's error (under <c>""</c>), and the fields the action declares, never a
    /// secret such as a password. A server action's form posted without JavaScript renders the page it was posted from
    /// again in the same way, for <c>useAction</c>; a server action called by script gets a failure with status 429 and the
    /// message, which reaches it as a <c>ServerActionError</c>.
    /// </summary>
    /// <param name="context">What the rate limiter hands its <c>OnRejected</c>.</param>
    /// <param name="message">The form's error. Default "Too many requests. Try again later."</param>
    /// <remarks>
    /// The post is handed back only when it passes the checks the action would have made: from this site (or an allowed
    /// origin), and with a valid antiforgery token, unless the action turned them off. Any other request, such as a page's
    /// <c>GET</c>, a post that fails those checks or one that is no action's, gets an empty 429, which Netjes answers with
    /// the error page or, for a script's call, a failure. <c>Retry-After</c> is set from the lease when the limiter says
    /// when to come back, and the answer goes out <c>Cache-Control: no-store</c>.
    /// </remarks>
    public static async ValueTask TooManyRequests(OnRejectedContext context, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var http = context.HttpContext;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            http.Response.Headers.RetryAfter = Math.Max(0, (long)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        NoStore.Force(http, "no-store");
        if (await MayHandBackAsync(http))
        {
            await ServerActionResult.RefuseAsync(http, StatusCodes.Status429TooManyRequests, message ?? TooManyRequestsMessage);
            return;
        }

        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
    }

    /// <summary>
    /// Whether a refused request is an action's post that passes the checks the action's own filters make before anything
    /// of it is written back into a page: the origin (always for a server action, and for a page action that requires a
    /// token) and the antiforgery token (where the action requires one).
    /// </summary>
    private static async Task<bool> MayHandBackAsync(HttpContext http)
    {
        var metadata = http.GetEndpoint()?.Metadata;
        var serverAction = metadata?.GetMetadata<ServerActionMetadata>() is not null;
        if (metadata is null || !HttpMethods.IsPost(http.Request.Method) || (!serverAction && metadata.GetMetadata<PageActionMetadata>() is null))
        {
            return false;
        }

        var requiresToken = metadata.GetMetadata<IAntiforgeryMetadata>() is { RequiresValidation: true };
        if ((serverAction || requiresToken) && NetjesEndpointRouteBuilderExtensions.IsCrossSite(http))
        {
            return false;
        }

        try
        {
            // The token first: ASP.NET Core refuses to read a form whose token the antiforgery middleware found invalid.
            if (requiresToken && !await NetjesEndpointRouteBuilderExtensions.HasValidAntiforgeryTokenAsync(http))
            {
                return false;
            }

            // Read here, so that a form that does not parse gets the empty 429 too.
            if (http.Request.HasFormContentType)
            {
                await http.Request.ReadFormAsync(http.RequestAborted);
            }

            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or BadHttpRequestException)
        {
            return false;
        }
    }
}
