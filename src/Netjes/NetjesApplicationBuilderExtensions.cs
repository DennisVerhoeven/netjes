using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;
using Netjes;
using Netjes.Rendering;
using Netjes.Routing;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Adds Netjes to the middleware pipeline.</summary>
public static class NetjesApplicationBuilderExtensions
{
    /// <summary>
    /// Serves static files (public/ in development, the client build in production), adds routing, and renders
    /// the nearest <c>error.tsx</c> for exceptions thrown by pages. Call it before authentication and authorization.
    /// Middleware registered before it does not run again when a failed no-JavaScript form post renders its page.
    /// Every response that passes through it gets <c>X-Content-Type-Options: nosniff</c>, static files, client payloads,
    /// output-cache answers and the app's own endpoints included, unless the response has that header when it starts;
    /// a response that middleware registered before it writes without passing the request on does not.
    /// <c>Netjes:SecurityHeaders:Enabled</c> set to false turns that off (<see cref="NetjesOptions.SecurityHeaders"/>).
    /// Last, after routing, a <c>GET</c> or <c>HEAD</c> for a page, or for the <c>/robots.txt</c> or <c>/sitemap.xml</c>
    /// that <c>MapRobotsTxt</c> and <c>MapSitemap</c> map, in another case of its literal segments gets a 308 to its
    /// spelling (<see cref="NetjesOptions.RedirectLiteralCase"/>), and so does one with a trailing slash under
    /// <see cref="TrailingSlash.Never"/>, before the output cache could store it.
    /// </summary>
    public static IApplicationBuilder UseNetjes(this IApplicationBuilder app)
    {
        var context = app.ApplicationServices.GetRequiredService<NetjesContext>();
        PagesFolder.ThrowIfItHasPageFiles(context);
        var time = app.ApplicationServices.GetRequiredService<TimeProvider>();
        // Lets a failed no-JavaScript form post render the form's page in the same request.
        var reExecute = new ReExecute();
        app.Use((http, next) =>
        {
            http.Features.Set(reExecute);
            return next(http);
        });
        // Ahead of the static files and everything else Netjes adds, so no response that passes through here goes without.
        if (context.Options.SecurityHeaders.Enabled)
        {
            app.Use(NoSniff);
        }

        // Runs outside output caching, so even a cached payload is sent with the current server time.
        app.Use((http, next) =>
        {
            if (http.Request.Headers.ContainsKey(NetjesHeaders.Data))
            {
                http.Response.OnStarting(() =>
                {
                    http.Response.Headers[NetjesHeaders.ServerTime] = time.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
                    return Task.CompletedTask;
                });
            }

            return next(http);
        });
        // Commands like `netjes generate` run before any build, so they need no manifest either.
        if (context.ScansPageFiles)
        {
            if (Directory.Exists(context.PublicDirectory))
            {
                app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(context.PublicDirectory) });
            }
        }
        else
        {
            // Fail at startup, with instructions, rather than on the first request.
            try
            {
                _ = app.ApplicationServices.GetRequiredService<ViteManifestProvider>().Manifest;
            }
            catch (NetjesStartupException failure) when (context.IsTheProgram)
            {
                UnhandledStartupFailure.ExitIfUnhandled(failure, app.ApplicationServices, typeof(ViteManifestProvider).FullName!);
                throw;
            }
            // Vite's build metadata (.vite/manifest.json) lives inside the served folder; the file provider's dot-file
            // filter checks only the file name, not directory segments. Check every segment on both separators: the
            // provider trims leading slashes ("//.vite") and, on Windows, also splits on backslashes.
            app.Use((http, next) =>
            {
                if ((http.Request.Path.Value ?? "").Split('/', '\\').Contains(".vite", StringComparer.OrdinalIgnoreCase))
                {
                    http.Response.StatusCode = 404;
                    return Task.CompletedTask;
                }

                return next(http);
            });
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(context.ClientDist),
                OnPrepareResponse = static file =>
                {
                    // Vite puts content-hashed files under /assets; they never change once built.
                    if (file.Context.Request.Path.StartsWithSegments("/assets"))
                    {
                        file.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                    }
                },
            });
        }

        // Before routing, so the no-store Netjes forces on some responses (NoStore) is applied after the response-start
        // callbacks of loaders, actions and later middleware.
        app.Use(NoStore.KeepForced);
        // A re-executed request starts at routing and goes on only to a page.
        app.Use(reExecute.StartHere);
        app.UseRouting();
        app.Use(ReExecute.PagesOnly);
        app.UseMiddleware<PageErrorMiddleware>();
        // Last: it reads the endpoint routing chose, and answers ahead of the output cache, authentication and the page.
        if (OtherSpellings.AreRedirected(context))
        {
            app.Use(new OtherSpellings(context).InvokeAsync);
        }

        return app;
    }

    /// <summary>
    /// Under a policy such as <c>script-src 'self'</c>, any response from the app's origin can be loaded as a script or
    /// a stylesheet; nosniff makes the browser refuse one whose Content-Type says otherwise. Added as the response
    /// starts, after the response-start callbacks of the endpoint and of later middleware, and only when the header is
    /// not there by then: the app's own value wins, and an output-cache answer, which may carry it among its stored
    /// headers, gets it once.
    /// </summary>
    private static Task NoSniff(HttpContext http, RequestDelegate next)
    {
        http.Response.OnStarting(static state =>
        {
            ((HttpResponse)state).Headers.TryAdd(HeaderNames.XContentTypeOptions, "nosniff");
            return Task.CompletedTask;
        }, http.Response);
        return next(http);
    }
}
