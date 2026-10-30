using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Netjes.Routing;

namespace Netjes.Rendering;

/// <summary>
/// Turns exceptions thrown while serving a page (in a loader, an action or during SSR) into the nearest <c>error.tsx</c>.
/// If rendering the error page fails too, falls back to a plain HTML document.
/// </summary>
internal sealed class PageErrorMiddleware(RequestDelegate next, NetjesContext context, ILogger<PageErrorMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext http, PageRenderer renderer)
    {
        if (http.GetEndpoint()?.Metadata.GetMetadata<PageEndpointMetadata>() is null)
        {
            await next(http);
            return;
        }

        try
        {
            // Over the body size limit (NetjesOptions.ActionBodySizeLimit, [RequestSizeLimit]): 413 before anything reads
            // the body. Left to the server, the antiforgery check would read a form post's token from the body first and
            // report a bad token (400). A chunked body has no length to compare, so a form post sent that way still gets
            // the 400; a call with the token in its header gets 413 when binding reads past the limit.
            ActionBodySizeLimit.Apply(http, context.Options);
            if (http.Request.ContentLength > http.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize)
            {
                http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            }
            else
            {
                await next(http);
            }

            if (IsEmptyErrorResponse(http.Response))
            {
                var status = http.Response.StatusCode;
                if (IsActionCall(http))
                {
                    // A server action call (a binding failure, authorization): the client expects an action response.
                    await ServerActionResponses.WriteAsync(http, status, ServerActionResponses.Failure(status, ReasonPhrases.GetReasonPhrase(status)));
                    return;
                }

                // An empty 400 from model binding, a 401/403 from authorization: show the error page, not a blank one.
                await RenderOrFallBackAsync(
                    http, renderer, () => renderer.RenderStatusAsync(http, status, denied: status is 401 or 403), null);
            }
        }
        catch (Exception ex) when (!http.Response.HasStarted && !http.RequestAborted.IsCancellationRequested)
        {
            if (ex is BadHttpRequestException)
            {
                logger.LogWarning(ex, "Bad request for page {Path}: {Message}", http.Request.Path, ex.Message);
            }
            else
            {
                logger.LogError(ex, "Unhandled exception serving page {Path}", http.Request.Path);
            }

            http.Response.Clear();
            if (IsActionCall(http))
            {
                // Before the action's own filters ran (binding in Development throws): still an action response.
                var status = ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError;
                var message = context.IncludeErrorDetails || status < 500 ? ex.Message : null;
                await ServerActionResponses.WriteAsync(http, status, ServerActionResponses.Failure(status, message));
                return;
            }

            await RenderOrFallBackAsync(http, renderer, () => renderer.RenderErrorAsync(http, ex), ex);
        }
    }

    private static bool IsActionCall(HttpContext http) =>
        PageRenderer.IsDataRequest(http) && http.GetEndpoint()?.Metadata.GetMetadata<ServerActionMetadata>() is not null;

    private static bool IsEmptyErrorResponse(HttpResponse response) =>
        !response.HasStarted
        && response.StatusCode >= StatusCodes.Status400BadRequest
        && string.IsNullOrEmpty(response.ContentType)
        && response.ContentLength is null or 0;

    private async Task RenderOrFallBackAsync(HttpContext http, PageRenderer renderer, Func<Task> render, Exception? cause)
    {
        var status = http.Response.StatusCode >= 400
            ? http.Response.StatusCode
            : cause is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError;
        try
        {
            await render();
        }
        catch (Exception renderError) when (!http.Response.HasStarted)
        {
            logger.LogError(renderError, "The error page failed to render for {Path}", http.Request.Path);
            var finalStatus = cause is BadHttpRequestException badRequest ? badRequest.StatusCode : status;
            http.Response.Clear();
            try
            {
                if (await renderer.TryRenderGlobalErrorAsync(http, finalStatus, cause ?? renderError))
                {
                    return;
                }
            }
            catch (Exception globalError) when (!http.Response.HasStarted)
            {
                logger.LogError(globalError, "The global error page failed to render for {Path}", http.Request.Path);
            }

            await WritePlainErrorAsync(http, finalStatus, cause ?? renderError);
        }
    }

    private Task WritePlainErrorAsync(HttpContext http, int status, Exception exception)
    {
        var title = $"{status} {ReasonPhrases.GetReasonPhrase(status)}";
        var details = context.IncludeErrorDetails
            ? $"<pre>{HtmlEncoder.Default.Encode(exception.Message)}\n\n{HtmlEncoder.Default.Encode(
                exception is SsrRenderException ssr ? ssr.JsStack ?? "" : exception.ToString())}</pre>"
            : "";
        http.Response.Clear();
        http.Response.StatusCode = status;
        PageRenderer.SecurityHeaders(http, context, draftMode: false);
        http.Response.ContentType = "text/html; charset=utf-8";
        return http.Response.WriteAsync(
            $"<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>{title}</title></head>" +
            $"<body><h1>{title}</h1>{details}</body></html>");
    }
}
