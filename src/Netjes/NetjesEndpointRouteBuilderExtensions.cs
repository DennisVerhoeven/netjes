using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Netjes;
using Netjes.Rendering;
using Microsoft.Extensions.Logging;
using Netjes.Routing;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Maps pages, page and layout loaders, page actions and server actions, and caches pages.</summary>
public static partial class NetjesEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps every page file without an explicit loader, a fallback that renders the 404 page, the
    /// <c>&lt;Image&gt;</c> optimizer (<c>/_netjes/image</c>), and with <c>CollectWebVitals</c> the vitals beacon.
    /// Conventions added to the result (e.g. <c>RequireAuthorization()</c>) apply to all those pages.
    /// </summary>
    public static IEndpointConventionBuilder MapPages(this IEndpointRouteBuilder endpoints)
    {
        // Fail at startup on a route conflict or a file name the conventions do not allow. Production builds the table
        // from the manifest when it is first read, and the matcher reads the pages' endpoints for every request: left
        // to then, every request would answer 500, /healthz and unknown URLs too.
        _ = endpoints.ServiceProvider.GetRequiredService<IRouteTableProvider>().Current;
        var dataSource = endpoints.ServiceProvider.GetRequiredService<PagesEndpointDataSource>();
        if (!endpoints.DataSources.Contains(dataSource))
        {
            endpoints.DataSources.Add(dataSource);
            MapAntiforgeryTokenEndpoint(endpoints);
        }

        BuiltInEndpoints.MapOnce(endpoints);
        return dataSource;
    }

    /// <summary>
    /// A token for pages that carry none (output-cached ones): the client asks here before a server action or form
    /// post. Same-origin only by the browser's rules (no CORS), never cached.
    /// </summary>
    private static void MapAntiforgeryTokenEndpoint(IEndpointRouteBuilder endpoints)
    {
        if (endpoints.ServiceProvider.GetService<IAntiforgery>() is null)
        {
            return;
        }

        endpoints.MapGet("/_netjes/antiforgery", (HttpContext http, IAntiforgery antiforgery) =>
            {
                var tokens = antiforgery.GetAndStoreTokens(http);
                NoStore.Force(http, "no-store");
                return TypedResults.Json(new AntiforgeryPayload(tokens.FormFieldName, tokens.HeaderName, tokens.RequestToken));
            })
            .CacheOutput(policy => policy.NoCache())
            .WithDisplayName("Netjes antiforgery token")
            .ExcludeFromDescription();
    }

    /// <summary>
    /// Maps the server-side loader for a page, the C# counterpart of <c>getServerSideProps</c>. The delegate binds
    /// like a minimal API GET handler (route values, query, services, no request body) and returns
    /// <see cref="Page.Props{TProps}"/>, <see cref="Page.NotFound"/> or <see cref="Page.Redirect"/>. A parameter
    /// minimal APIs cannot bind throws here, such as a complex type that is not a registered service and has no binding
    /// attribute or <c>BindAsync</c>, and so does one that reads the request body (<c>[FromBody]</c>, <c>[FromForm]</c>,
    /// a form or a file), which a navigation does not have, an <c>IEnumerable&lt;int&gt;</c>, which minimal APIs
    /// would take from services rather than the query string, or a type that adds accepts metadata for itself, with
    /// which routing would match the page only for requests with that Content-Type. Accepts metadata on the result
    /// (<c>Accepts&lt;T&gt;()</c>) would keep navigations from the page too. The props type is recorded for code generation.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="pattern">The page's URL pattern as written in its file name, e.g. <c>/blog/[slug]</c>.</param>
    /// <param name="loader">The loader delegate.</param>
    public static RouteHandlerBuilder MapPageLoader(this IEndpointRouteBuilder endpoints, string pattern, Delegate loader)
    {
        var route = RequireRoute(endpoints, pattern);
        // A GET endpoint, so minimal APIs infer no body for it, as for every loader.
        LoaderBinding.RequireBindable($"The loader of {route.File} ({route.Pattern})", loader.Method, endpoints.ServiceProvider,
            RouteParameters(endpoints, RoutePatternFactory.Parse(route.Template)), routed: true);
        endpoints.ServiceProvider.GetRequiredService<PageLoaderRegistry>().Register(route.Pattern);
        return endpoints
            .MapMethods(route.Template, [HttpMethods.Get, HttpMethods.Head], loader)
            .WithLayoutConventions(endpoints, route)
            .AddEndpointFilter(Traced("netjes.loader", route.Pattern))
            .WithMetadata(EndpointMetadataFor(route.Pattern, loader))
            .WithDisplayName($"Page {route.Pattern}")
            .ExcludeFromDescription();
    }

    /// <summary>
    /// Maps a form POST handler for a page. Return <c>Page.Redirect(...)</c> after a successful change, or
    /// <c>Page.Props(...)</c> to re-render the page (e.g. with validation errors). Works with and without JavaScript.
    /// Always requires a valid antiforgery token (the Form component sends it), whether or not the handler binds
    /// form fields, and a same-origin request like <c>MapAction</c>; place <c>app.UseAntiforgery()</c> after
    /// authentication. <c>.DisableAntiforgery()</c> turns off both, for posts from other sites. The DataAnnotations of its
    /// inputs are checked before it runs, as for <c>MapAction</c>: a failure renders the page again from its loader, with
    /// the errors and the declared fields' values for <c>usePageActionState()</c>. A <c>[FromForm]</c> input is never
    /// <c>null</c> unless declared nullable: a post with none of its fields gets an empty one, checked like any other. A
    /// delegate minimal APIs cannot bind throws here, and so does an input whose DataAnnotations cannot be read.
    /// </summary>
    public static RouteHandlerBuilder MapPageAction(this IEndpointRouteBuilder endpoints, string pattern, Delegate action)
    {
        var route = RequireRoute(endpoints, pattern);
        var name = $"The action of {route.File} ({route.Pattern})";
        RequireBindable(endpoints, name, route.Template, action);
        ActionInputValidation.RequireReadable(name, action.Method);
        return endpoints
            .MapPost(route.Template, action)
            .WithLayoutConventions(endpoints, route)
            .AddEndpointFilter(CookieChanges.Mark)
            .AddEndpointFilter(Traced("netjes.action", route.Pattern))
            .WithMetadata(EndpointMetadataFor(route.Pattern, action))
            .WithMetadata(new PageActionMetadata(route.Pattern), new ActionFormFields(action.Method), new RequireAntiforgeryTokenAttribute())
            .AddEndpointFilter(RejectCrossSitePosts)
            .AddEndpointFilter(RejectInvalidAntiforgeryToken)
            .AddEndpointFilterFactory(EmptyFormInputs.Filter)
            .AddEndpointFilterFactory(ActionInputValidation.Filter)
            .WithDisplayName($"Page action {route.Pattern}")
            .ExcludeFromDescription();
    }

    /// <summary>
    /// Maps the C# loader of a layout (<c>layout.tsx</c>): its props reach the layout component next
    /// to <c>children</c> and <c>params</c>. Binds like a minimal API GET handler, from the route values of the layout's
    /// own URL, the query string and services (a parameter it cannot bind throws here, such as a <c>[FromRoute]</c>
    /// naming another route value, a complex type that is not a registered service, one that reads the request body, or
    /// an <c>IEnumerable&lt;int&gt;</c>, which minimal APIs would take from services),
    /// and returns a <see cref="PageResult"/> (<c>Page.NotFound()</c> renders the parent's not-found page,
    /// <c>Page.Redirect</c> redirects the page). On client navigations within the section the loader is skipped while
    /// those route values and the user stay the same.
    /// Conventions on the result, e.g. <c>RequireAuthorization()</c>, apply to every page below the layout.
    /// </summary>
    /// <param name="segment">The layout's URL, e.g. <c>/dashboard</c> or <c>/[team]</c>, or its folder (<c>app/(shop)</c>) when route groups share a URL.</param>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="loader">The loader delegate.</param>
    public static LayoutLoaderBuilder MapLayoutLoader(this IEndpointRouteBuilder endpoints, string segment, Delegate loader)
    {
        var context = endpoints.ServiceProvider.GetRequiredService<NetjesContext>();
        var table = endpoints.ServiceProvider.GetRequiredService<IRouteTableProvider>().Current;
        var layouts = table.Pages.SelectMany(p => p.Segments).Concat(table.RootSegments)
            .Where(s => s.Layout is not null)
            .DistinctBy(s => s.Id)
            .ToList();
        var normalized = segment.Replace('\\', '/').TrimEnd('/');
        var matches = layouts.Where(s => s.Id == normalized).ToList();
        if (matches.Count == 0)
        {
            var pattern = normalized.Length == 0 ? "/" : normalized;
            matches = [.. layouts.Where(s => s.Pattern == pattern)];
        }

        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"Several layouts serve '{segment}': {string.Join(", ", matches.Select(m => $"\"{m.Id}\""))}. Pass the folder instead.");
        }

        if (matches.Count == 0)
        {
            var path = segment.Trim('/');
            throw new InvalidOperationException(
                $"No layout file serves '{segment}'. Expected {context.AppDirectory}/{(path.Length == 0 ? "" : path + "/")}layout.tsx.");
        }

        if (LayoutLoader.RouteBindingsOutsideItsUrl(matches[0], loader, endpoints.ServiceProvider) is [var (parameter, route), ..])
        {
            throw new InvalidOperationException(
                $"The layout loader of {matches[0].Layout} binds route parameter '{route}' (its parameter '{parameter}'), which is not in the layout's URL {matches[0].Pattern}. " +
                "A layout loader binds only the route values of its own URL, and its props are reused on client navigations while those stay the same. " +
                $"Bind '{route}' in the page's loader, or give the loader to the layout of a folder whose URL has it.");
        }

        LoaderBinding.RequireBindable($"The layout loader of {matches[0].Layout} ({matches[0].Pattern})", loader.Method, endpoints.ServiceProvider, matches[0].Parameters, routed: false);

        var registry = endpoints.ServiceProvider.GetRequiredService<LayoutLoaderRegistry>();
        return new LayoutLoaderBuilder(registry.Register(matches[0], loader, endpoints.ServiceProvider, PagePropsMetadata.For(loader)?.PropsType));
    }

    private static RouteHandlerBuilder WithLayoutConventions(this RouteHandlerBuilder builder, IEndpointRouteBuilder endpoints, PageRoute route)
    {
        var registry = endpoints.ServiceProvider.GetRequiredService<LayoutLoaderRegistry>();
        // Applied when endpoints are built, after every MapLayoutLoader call.
        builder.Add(endpoint => registry.ApplyConventions(route, endpoint));
        return builder;
    }

    /// <summary>A span around the loader or action delegate (see <see cref="NetjesTelemetry"/>).</summary>
    private static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> Traced(string operation, string route) =>
        async (context, next) =>
        {
            using var activity = NetjesTelemetry.StartActivity(operation, route);
            try
            {
                return await next(context);
            }
            catch (Exception ex)
            {
                activity.RecordException(ex);
                throw;
            }
        };

    // The antiforgery middleware only records its verdict (none when it did not judge the request, which happens only
    // when the app suppresses the endpoint security-metadata check); ASP.NET Core acts on it
    // only when a parameter binds form data. Page actions enforce it regardless, so an action without form fields (a
    // delete button) or a text/plain cross-site post is still protected.
    private static async ValueTask<object?> RejectInvalidAntiforgeryToken(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.GetEndpoint()?.Metadata.GetMetadata<IAntiforgeryMetadata>() is { RequiresValidation: true })
        {
            var verdict = http.Features.Get<IAntiforgeryValidationFeature>();
            var valid = verdict?.IsValid
                ?? await http.RequestServices.GetRequiredService<IAntiforgery>().IsRequestValidAsync(http);
            if (!valid)
            {
                // Recorded where the middleware records its own: the action response marks it (ServerActionResponses).
                if (verdict is null)
                {
                    http.Features.Set<IAntiforgeryValidationFeature>(RefusedToken.Instance);
                }

                throw verdict?.Error is { } error
                    ? new BadHttpRequestException("Invalid antiforgery token.", StatusCodes.Status400BadRequest, error)
                    : new BadHttpRequestException("Invalid antiforgery token.", StatusCodes.Status400BadRequest);
            }
        }

        return await next(context);
    }

    private sealed class RefusedToken : IAntiforgeryValidationFeature
    {
        public static readonly RefusedToken Instance = new();

        public bool IsValid => false;

        public Exception? Error => null;
    }

    /// <summary>
    /// Caches the rendered page (document and client payload separately, payloads by which of the page's layout
    /// segments the client already holds) for <paramref name="duration"/>, like
    /// Next.js incremental static regeneration. Server actions evict it with <c>.Revalidate(tags)</c> or
    /// <c>.RevalidatePath(path)</c>, other code with <see cref="IPageCache"/>. Requires <c>AddOutputCache()</c> and
    /// <c>UseOutputCache()</c>. Cached pages carry no antiforgery token. Draft mode bypasses the cache. How entries are
    /// keyed is the same under any output caching of a page (<c>CacheOutput</c> too); on an endpoint that is no page
    /// (a minimal API returning <c>Page.Render</c>), only with <c>CachePage</c>.
    /// </summary>
    public static TBuilder CachePage<TBuilder>(this TBuilder builder, TimeSpan duration, params string[] tags)
        where TBuilder : IEndpointConventionBuilder =>
        builder
            .CacheOutput(policy => policy.Expire(duration).Tag(tags))
            .WithMetadata(new OmitAntiforgeryTokenMetadata(), new CachedPageMetadata());

    /// <summary>
    /// Maps a server action, the counterpart of a Next.js <c>'use server'</c> function: a POST endpoint at
    /// <c>/_netjes/actions/{name}</c> that binds like any minimal API handler (JSON body, <c>[FromForm]</c> fields,
    /// services) and returns a <see cref="ServerActionResult"/>. Call it from React with <c>serverAction(name)</c> and
    /// <c>useAction</c>; forms keep working without JavaScript. Requires a valid antiforgery token and a same-origin
    /// request; conventions such as <c>RequireAuthorization()</c> work as on any endpoint. The DataAnnotations of its
    /// inputs are checked before it runs. A <c>[FromForm]</c> input is never <c>null</c> unless declared nullable: a post
    /// with none of its fields gets an empty one, checked like any other. A delegate minimal APIs cannot bind throws
    /// here, and so does an input whose DataAnnotations cannot be read.
    /// </summary>
    /// <param name="name">A stable name, e.g. <c>posts.create</c>: letters, digits, <c>.</c>, <c>-</c> and <c>_</c>.</param>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="action">The action delegate.</param>
    public static RouteHandlerBuilder MapAction(this IEndpointRouteBuilder endpoints, string name, Delegate action)
    {
        if (!ActionNameRegex().IsMatch(name))
        {
            throw new ArgumentException(
                $"'{name}' is not a valid action name: use letters, digits, '.', '-' and '_'.", nameof(name));
        }

        var names = endpoints.ServiceProvider.GetRequiredService<ServerActionRegistry>();
        if (!names.Add(name))
        {
            throw new InvalidOperationException($"A server action named '{name}' is already mapped.");
        }

        RequireBindable(endpoints, $"The server action '{name}'", $"/_netjes/actions/{name}", action);
        ActionInputValidation.RequireReadable($"The server action '{name}'", action.Method);
        var builder = endpoints
            .MapPost($"/_netjes/actions/{name}", action)
            .WithMetadata(new ServerActionMetadata(name), new ActionFormFields(action.Method), new PageEndpointMetadata(null), new RequireAntiforgeryTokenAttribute());
        if (ServerActionResultMetadata.For(action) is { } result)
        {
            builder.WithMetadata(result);
        }

        return builder
            .AddEndpointFilter(CookieChanges.Mark)
            .AddEndpointFilter(ActionCallFailures)
            .AddEndpointFilter(RejectCrossSiteCalls)
            .AddEndpointFilter(RejectInvalidAntiforgeryToken)
            .AddEndpointFilterFactory(EmptyFormInputs.Filter)
            .AddEndpointFilterFactory(ActionInputValidation.Filter)
            .WithDisplayName($"Server action {name}")
            .ExcludeFromDescription();
    }

    // For calls from the client router: failures come back as action responses rather than error pages.
    private static async ValueTask<object?> ActionCallFailures(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!PageRenderer.IsDataRequest(http))
        {
            return await next(context);
        }

        try
        {
            return await next(context);
        }
        catch (Exception ex) when (!http.Response.HasStarted && !http.RequestAborted.IsCancellationRequested)
        {
            var status = ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError;
            var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Netjes.ServerActions");
            if (status >= 500) logger.LogError(ex, "Server action {Path} failed", http.Request.Path);
            else logger.LogWarning(ex, "Rejected server action call {Path}: {Message}", http.Request.Path, ex.Message);
            var details = http.RequestServices.GetRequiredService<NetjesContext>().IncludeErrorDetails;
            await ServerActionResponses.WriteAsync(http, status, ServerActionResponses.Failure(status, details || status < 500 ? ex.Message : null));
            return TypedResults.Empty;
        }
    }

    // Like Next.js, compare the caller's origin with the host: a token alone is weaker if a cookie leaks to a sibling site.
    // "null" (sandboxed frames, some redirects) is not this site either, unless the browser says the request is
    // same-origin. AllowedActionOrigins adds others.
    private static ValueTask<object?> RejectCrossSiteCalls(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var request = http.Request;
        var site = request.Headers["Sec-Fetch-Site"].ToString();
        var origin = request.Headers.Origin.ToString();
        var allowed = http.RequestServices.GetRequiredService<NetjesContext>().Options.AllowedActionOrigins;
        // A no-JS form post from a page with Referrer-Policy: no-referrer sends Origin: null; the browser's own
        // Sec-Fetch-Site (which scripts cannot set) still tells whether it came from this origin.
        var nullFromHere = origin == "null" && site == "same-origin";
        var crossSite = site is "cross-site"
            || (origin.Length > 0 && !nullFromHere && !IsSameOrAllowedOrigin(origin, request.Host.Value, allowed));
        if (crossSite)
        {
            throw new BadHttpRequestException("Cross-site posts to actions are not allowed.", StatusCodes.Status403Forbidden);
        }

        return next(context);
    }

    // Page actions too, unless they opt out of antiforgery: a webhook, or a payment or identity provider posting a
    // form back from its own site, needs both off.
    private static ValueTask<object?> RejectCrossSitePosts(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<IAntiforgeryMetadata>() is { RequiresValidation: true }
            ? RejectCrossSiteCalls(context, next)
            : next(context);

    private static bool IsSameOrAllowedOrigin(string origin, string? host, IReadOnlyList<string> allowed) =>
        allowed.Any(a => string.Equals(a.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase))
        || (origin != "null" && Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && string.Equals(uri.Authority, host, StringComparison.OrdinalIgnoreCase));

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9_.-]+$")]
    private static partial System.Text.RegularExpressions.Regex ActionNameRegex();

    /// <summary>
    /// Stops the app when minimal API binding refuses an action mapped at <paramref name="template"/>, as it would when
    /// ASP.NET Core first builds the endpoints: on the first request, failing every request of the app. So does a form
    /// input that form binding could never build (<see cref="FormInputTypes"/>).
    /// </summary>
    private static void RequireBindable(IEndpointRouteBuilder endpoints, string action, string template, Delegate handler)
    {
        var pattern = RoutePatternFactory.Parse(template);
        // The options RouteEndpointDataSource builds a POST handler with: a body may be inferred.
        LoaderBinding.RequireBindable(action, handler.Method, new RequestDelegateFactoryOptions
        {
            ServiceProvider = endpoints.ServiceProvider,
            RouteParameterNames = RouteParameters(endpoints, pattern),
            EndpointBuilder = new RouteEndpointBuilder(null, pattern, 0) { ApplicationServices = endpoints.ServiceProvider },
        });
        FormInputTypes.RequireBuildable(action, handler.Method);
    }

    /// <summary>
    /// The route parameters of an endpoint mapped at <paramref name="pattern"/>: those of the prefixes of the route groups
    /// it is mapped in (<c>app.MapGroup("/{tenant}")</c>), outermost first, which ASP.NET Core puts before the pattern
    /// when it builds the endpoint, and the pattern's own. Null, so that minimal APIs check the names of
    /// <c>[FromRoute]</c> when they build it, when a group's prefix cannot be read: <see cref="RouteGroupBuilder"/>
    /// keeps it in a private field.
    /// </summary>
    private static List<string>? RouteParameters(IEndpointRouteBuilder endpoints, RoutePattern pattern)
    {
        var names = pattern.Parameters.Select(p => p.Name).ToList();
        for (var builder = endpoints; builder is RouteGroupBuilder group;)
        {
            if (GroupPrefix?.GetValue(group) is not RoutePattern prefix || GroupOuter?.GetValue(group) is not IEndpointRouteBuilder outer)
            {
                return null;
            }

            names.InsertRange(0, prefix.Parameters.Select(p => p.Name));
            builder = outer;
        }

        return names;
    }

    private static readonly System.Reflection.FieldInfo? GroupPrefix =
        typeof(RouteGroupBuilder).GetField("_partialPrefix", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private static readonly System.Reflection.FieldInfo? GroupOuter =
        typeof(RouteGroupBuilder).GetField("_outerEndpointRouteBuilder", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    // The page marker, and the props type the loader's inferred return type names (for the type generator).
    private static object[] EndpointMetadataFor(string pattern, Delegate handler) =>
        PagePropsMetadata.For(handler) is { } props ? [new PageEndpointMetadata(pattern), props] : [new PageEndpointMetadata(pattern)];

    private static PageRoute RequireRoute(IEndpointRouteBuilder endpoints, string pattern)
    {
        var context = endpoints.ServiceProvider.GetRequiredService<NetjesContext>();
        var table = endpoints.ServiceProvider.GetRequiredService<IRouteTableProvider>().Current;
        return table.Find(pattern) ?? throw new InvalidOperationException(
            $"No page file matches '{pattern}'. Expected {PageRenderer.ExpectedFile(context, pattern)}. " +
            $"Known pages: {string.Join(", ", table.Pages.Select(p => p.Pattern))}.");
    }
}
