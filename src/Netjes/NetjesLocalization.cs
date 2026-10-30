using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Localization.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Netjes;
using Netjes.Rendering;

namespace Netjes
{
    /// <summary>Locale routing, like Next.js's i18n: a <c>[lang]</c> folder in the URL decides the request culture.</summary>
    public sealed class NetjesLocalizationOptions
    {
        /// <summary>Culture names, e.g. <c>en</c>, <c>nl</c>. The first is the default.</summary>
        public string[] SupportedCultures { get; set; } = ["en"];

        /// <summary>The route parameter holding the locale: <c>app/[lang]/...</c>.</summary>
        public string RouteParameter { get; set; } = "lang";
    }

    /// <summary>Set when localization is on: lets the renderer send the locale and the document's <c>lang</c>.</summary>
    internal sealed class LocalizationMarker;
}

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Registers route-driven localization.</summary>
    public static class NetjesLocalizationServiceCollectionExtensions
    {
        /// <summary>
        /// Request localization driven by a <c>[lang]</c> route segment only, with <c>CultureInfo.CurrentCulture</c>/
        /// <c>CurrentUICulture</c> set for loaders, <c>useLocale()</c> on the client and the page's <c>&lt;html lang&gt;</c>.
        /// Pages outside the segment get the default culture: their URL names no locale, so taking one from a cookie or
        /// Accept-Language would let an output-cached copy serve the first visitor's language to everyone. (Add those
        /// providers to <c>RequestLocalizationOptions</c> yourself if those pages are not cached, and vary by them.)
        /// Pair with <c>app.UseNetjesLocalization()</c> after <c>UseNetjes()</c>.
        /// </summary>
        public static IServiceCollection AddNetjesLocalization(this IServiceCollection services, Action<NetjesLocalizationOptions>? configure = null)
        {
            var options = services.AddOptions<NetjesLocalizationOptions>();
            if (configure is not null)
            {
                options.Configure(configure);
            }

            services.AddSingleton<LocalizationMarker>();
            services.AddOptions<RequestLocalizationOptions>().Configure<IOptions<NetjesLocalizationOptions>>((localization, netjes) =>
            {
                var cultures = netjes.Value.SupportedCultures.Select(c => new CultureInfo(c)).ToList();
                localization.DefaultRequestCulture = new RequestCulture(cultures[0]);
                localization.SupportedCultures = cultures;
                localization.SupportedUICultures = cultures;
                // Finding a parent creates a culture for each name a client sends (cached by the runtime for good):
                // list nl-BE as well as nl if both should match.
                localization.FallBackToParentCultures = false;
                localization.FallBackToParentUICultures = false;
                localization.RequestCultureProviders =
                [
                    new RouteDataRequestCultureProvider { RouteDataStringKey = netjes.Value.RouteParameter, UIRouteDataStringKey = netjes.Value.RouteParameter },
                ];
            });
            return services;
        }
    }
}

namespace Microsoft.AspNetCore.Builder
{
    /// <summary>Adds route-driven localization to the pipeline, and maps the locale redirect.</summary>
    public static class NetjesLocalizationApplicationBuilderExtensions
    {
        /// <summary>
        /// Sets the request culture from the <c>[lang]</c> segment (it needs routing, so call it after <c>UseNetjes()</c>).
        /// Any endpoint whose route has the locale parameter answers 404 for a locale the app does not support (pages
        /// render the not-found page in the default culture), and gets a supported one in its configured case.
        /// </summary>
        public static IApplicationBuilder UseNetjesLocalization(this IApplicationBuilder app)
        {
            var options = app.ApplicationServices.GetRequiredService<IOptions<NetjesLocalizationOptions>>().Value;
            var defaultCulture = app.ApplicationServices.GetRequiredService<IOptions<RequestLocalizationOptions>>().Value.DefaultRequestCulture;
            app.Use(async (http, next) =>
            {
                if (http.Request.RouteValues.TryGetValue(options.RouteParameter, out var value) && value is string locale)
                {
                    var configured = options.SupportedCultures.FirstOrDefault(c => string.Equals(c, locale, StringComparison.OrdinalIgnoreCase));
                    // Every endpoint that binds the locale, not only pages: request localization would look the raw name
                    // up as a culture, and the runtime keeps each culture it finds for the life of the process.
                    if (configured is null)
                    {
                        if (http.GetEndpoint()?.Metadata.GetMetadata<Netjes.Routing.PageEndpointMetadata>() is not null)
                        {
                            // Request localization never runs for it: render in the app's default culture, not the server's.
                            http.Features.Set<IRequestCultureFeature>(new RequestCultureFeature(defaultCulture, provider: null));
                            CultureInfo.CurrentCulture = defaultCulture.Culture;
                            CultureInfo.CurrentUICulture = defaultCulture.UICulture;
                            await http.RequestServices.GetRequiredService<PageRenderer>().RenderNotFoundAsync(http);
                        }
                        else
                        {
                            http.Response.StatusCode = StatusCodes.Status404NotFound;
                        }

                        return;
                    }

                    // Loaders, params.lang and layout keys get the configured name, whatever case the URL used.
                    http.Request.RouteValues[options.RouteParameter] = configured;
                }

                await next(http);
            });
            return app.UseRequestLocalization();
        }

        /// <summary>Redirects <c>/</c> to the best supported locale for the browser's Accept-Language, e.g. <c>/nl</c>.</summary>
        public static IEndpointConventionBuilder MapLocaleRedirect(this IEndpointRouteBuilder endpoints) =>
            endpoints.MapGet("/", (HttpContext http, IOptions<NetjesLocalizationOptions> options) =>
            {
                var supported = options.Value.SupportedCultures;
                var preferred = http.Request.GetTypedHeaders().AcceptLanguage
                    .OrderByDescending(language => language.Quality ?? 1)
                    .Select(language => language.Value.Value ?? "")
                    .Select(language => supported.FirstOrDefault(culture =>
                        string.Equals(culture, language, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(culture, language.Split('-')[0], StringComparison.OrdinalIgnoreCase)))
                    .FirstOrDefault(culture => culture is not null);
                http.Response.Headers.Vary = HeaderNames.AcceptLanguage;
                return Results.Redirect($"{http.Request.PathBase}/{preferred ?? supported[0]}", permanent: false, preserveMethod: true);
            }).WithDisplayName("Netjes locale redirect");
    }
}
