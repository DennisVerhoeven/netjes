using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Netjes.Routing;
using Netjes.Sidecar;

namespace Netjes.Rendering;

/// <summary>
/// Answers a page request: a JSON payload for the client router (<see cref="NetjesHeaders.Data"/> requests),
/// or a server-rendered HTML document with the same payload embedded for hydration.
/// </summary>
internal sealed class PageRenderer(
    NetjesContext context,
    IRouteTableProvider routes,
    IAssetResolver assets,
    ISsrRenderer ssr,
    IOptions<JsonOptions> jsonOptions,
    TimeProvider time,
    LayoutLoaderRegistry layoutLoaders,
    IConfiguration configuration,
    RouteSources sources,
    ILogger<PageRenderer> logger)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _warnedNonceCaching = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _warnedUnkeyedCaching = new(StringComparer.Ordinal);
    // The Content-Security-Policies documents went out under, by their text without nonces (CspChecks.WithoutNonces).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _checkedPolicies = new(StringComparer.Ordinal);
    private int _checkedPolicyCount;
    // An app can write another policy for each request; the rest are not checked.
    private const int MaxCheckedPolicies = 100;

    private static readonly IReadOnlyDictionary<string, object?> NoParams = new Dictionary<string, object?>();
    private static readonly IReadOnlyDictionary<string, object?> NoProps = new Dictionary<string, object?>();

    private readonly Lazy<System.Text.RegularExpressions.Regex> crawlers = new(() => new System.Text.RegularExpressions.Regex(
        context.Options.CrawlerUserAgents,
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50)));

    // Set while a layout loader runs: if it throws, the error boundary has to sit above that segment.
    private const string FailedSegmentKey = "Netjes.FailedSegment";
    // Layout loader results of this request, so a boundary rendered after a failure does not run them twice.
    private const string LoadedSegmentsKey = "Netjes.LoadedSegments";
    // Set when a 404 renders on a page package's mount: the boundaries rendered after it, for a loader on that chain
    // that fails or stops, stay on the chain.
    private const string MountChainKey = "Netjes.MountChain";

    public static bool IsDataRequest(HttpContext http) => http.Request.Headers.ContainsKey(NetjesHeaders.Data);

    public async Task RenderRouteAsync(
        HttpContext http, string pattern, object? props, int status, IReadOnlyList<PageQuery>? queries = null, PageMetadata? metadata = null)
    {
        var route = routes.Current.Find(pattern)
            ?? throw new InvalidOperationException(
                $"No page file matches '{pattern}'. Expected {ExpectedFiles(pattern)}.");
        var loaded = await LoadSegmentsAsync(http, route.Segments, route);
        if (loaded.Stop is { } stop)
        {
            await StopAsync(http, stop, loaded.StoppedAt);
            return;
        }

        await RenderAsync(http, route.Pattern, route.File, null, loaded.Segments, props, status, ParamsFor(http, route), queries, metadata);
    }

    private sealed record LoadedSegments(List<SegmentPayload> Segments, PageResult? Stop, int StoppedAt);

    /// <summary>
    /// Runs the layout loaders of <paramref name="segments"/>, outermost first, stopping at the first that does not
    /// return props. Segments the client already holds (their keys are in the segments header) are marked reused.
    /// </summary>
    private async Task<LoadedSegments> LoadSegmentsAsync(HttpContext http, IReadOnlyList<RouteSegment> segments, PageRoute? route = null)
    {
        var reusable = ReusableKeys(http);
        if (http.Items[LoadedSegmentsKey] is not Dictionary<string, PageResult> results)
        {
            http.Items[LoadedSegmentsKey] = results = new Dictionary<string, PageResult>(StringComparer.Ordinal);
        }

        var loaded = new List<SegmentPayload>(segments.Count);
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var payload = SegmentPayload.From(segment, route);
            if (segment.Layout is not null)
            {
                payload = payload with { Depth = DepthOf(http, segment) };
            }

            if (route is not null && segment.Slots.Count > 0)
            {
                payload = payload with { SlotParams = SlotParamsFor(http, segment, route) };
            }

            if (layoutLoaders.Find(segment.Id) is { } loader)
            {
                var key = layoutLoaders.KeyFor(segment, http, assets.Version);
                if (reusable.Contains(key))
                {
                    payload = payload with { Key = key, Reused = true };
                    CachedSegments.Reused(http, segment.Id);
                }
                else
                {
                    if (!results.TryGetValue(segment.Id, out var result))
                    {
                        http.Items[FailedSegmentKey] = i;
                        result = await loader.InvokeAsync(http);
                        http.Items.Remove(FailedSegmentKey);
                        results[segment.Id] = result;
                    }

                    if (result.ResultKind != PageResult.Kind.Props)
                    {
                        return new LoadedSegments(loaded, result, i);
                    }

                    payload = payload with { Props = result.ResultProps ?? NoProps, Key = key };
                }
            }

            loaded.Add(payload);
        }

        return new LoadedSegments(loaded, null, -1);
    }

    /// <summary>The queries the layout loaders of these (freshly loaded, not reused) segments seeded.</summary>
    private static IEnumerable<PageQuery> LayoutQueries(HttpContext http, IReadOnlyList<SegmentPayload> segments) =>
        http.Items[LoadedSegmentsKey] is Dictionary<string, PageResult> results
            ? segments
                .Where(s => !s.Reused && s.Props is not null)
                .SelectMany(s => results.TryGetValue(s.Id, out var result) ? result.ResultQueries : [])
            : [];

    /// <summary>
    /// The client sends one 22-character key per layout segment of its current page; a header longer than about 85 of
    /// them is not from the client, and is not read.
    /// </summary>
    internal const int MaxSegmentsHeaderLength = 2048;

    internal static HashSet<string> ReusableKeys(HttpContext http)
    {
        // Only client navigations reuse segments; documents, form posts and refreshes load everything.
        if (!IsDataRequest(http) || !(HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)))
        {
            return [];
        }

        var header = http.Request.Headers[NetjesHeaders.Segments];
        if (header.Sum(v => v?.Length ?? 0) > MaxSegmentsHeaderLength)
        {
            return [];
        }

        return [.. header.SelectMany(v => (v ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))];
    }

    /// <summary>A layout loader at <paramref name="index"/> did not return props: render what it asked for above it.</summary>
    private Task StopAsync(HttpContext http, PageResult stop, int index) => stop.ResultKind switch
    {
        PageResult.Kind.NotFound => RenderBoundaryAsync(http, Boundary.NotFound, StatusCodes.Status404NotFound, null, index),
        PageResult.Kind.Status when stop.StatusCode is { } status => status switch
        {
            StatusCodes.Status403Forbidden => RenderBoundaryAsync(http, Boundary.Forbidden, status, ErrorPropsFor(status, null), index),
            StatusCodes.Status401Unauthorized => RenderBoundaryAsync(http, Boundary.Unauthorized, status, ErrorPropsFor(status, null), index),
            _ => RenderBoundaryAsync(http, Boundary.Error, status, ErrorPropsFor(status, null), index),
        },
        _ => stop.ExecuteAsync(http),
    };

    /// <summary>The nearest not-found boundary of the request's page, else the root one, else the built-in page.</summary>
    public Task RenderNotFoundAsync(HttpContext http) =>
        RenderBoundaryAsync(http, Boundary.NotFound, StatusCodes.Status404NotFound, props: null);

    public Task RenderErrorAsync(HttpContext http, Exception exception)
    {
        var status = exception is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError;
        return RenderBoundaryAsync(http, Boundary.Error, status, ErrorPropsFor(status, exception));
    }

    /// <summary>
    /// Renders the page for a status some other component set without writing a body, or a loader asked for:
    /// the nearest not-found, forbidden or unauthorized boundary, or the error page.
    /// </summary>
    /// <param name="denied">
    /// The status came from authorization (an empty 401/403). The first section whose layout loader's authorization
    /// this user fails, or that holds a page package's files whose mount's conventions the user fails, is where the page
    /// stops: its loader never runs, and its own boundary (or the nearest above it) renders inside the layouts above the
    /// section.
    /// </param>
    public async Task RenderStatusAsync(HttpContext http, int status, bool denied = false)
    {
        var limit = denied ? await FirstDeniedSegmentAsync(http, RouteOf(http)?.Segments ?? []) : null;
        var boundary = status switch
        {
            StatusCodes.Status404NotFound => Boundary.NotFound,
            StatusCodes.Status403Forbidden => Boundary.Forbidden,
            StatusCodes.Status401Unauthorized => Boundary.Unauthorized,
            _ => Boundary.Error,
        };
        var props = boundary == Boundary.NotFound ? null : ErrorPropsFor(status, null);
        await RenderBoundaryAsync(http, boundary, status, props, limit, includeLimit: limit is not null);
    }

    /// <summary>
    /// The index of the first of <paramref name="segments"/> whose layout loader's authorization this user fails, or that
    /// holds a file of a page package whose mount's conventions the user fails; null when none. A layout loader mapped
    /// through a mount carries only its own conventions, and its props would otherwise reach a visitor the mount refuses.
    /// </summary>
    private async Task<int?> FirstDeniedSegmentAsync(HttpContext http, IReadOnlyList<RouteSegment> segments)
    {
        for (var i = 0; i < segments.Count; i++)
        {
            if (layoutLoaders.Find(segments[i].Id) is { } loader && !await loader.AuthorizesAsync(http))
            {
                return i;
            }

            foreach (var mounted in PackagesOf(segments[i]))
            {
                if (!await ConventionAuthorization.AuthorizesAsync(http, http.RequestServices, mounted.Mount.Conventions, mounted.Mount.FinallyConventions))
                {
                    return i;
                }
            }
        }

        return null;
    }

    /// <summary>The mounted page packages whose files <paramref name="segment"/> holds.</summary>
    private IEnumerable<MountedPackage> PackagesOf(RouteSegment segment) =>
        new[] { segment.Layout, segment.Template, segment.Loading, segment.Error, segment.NotFound, segment.Forbidden, segment.Unauthorized }
            .Select(sources.OwnerOf)
            .OfType<MountedPackage>()
            .Distinct();

    /// <summary><c>app/global-error.tsx</c>, without any layout: the last resort when an error page fails to render.</summary>
    public async Task<bool> TryRenderGlobalErrorAsync(HttpContext http, int status, Exception? exception)
    {
        if (routes.Current.GlobalError is not { } globalError)
        {
            return false;
        }

        await RenderAsync(http, RouteOf(http)?.Pattern, globalError.File, "error", [], ErrorPropsFor(status, exception), status, NoParams);
        return true;
    }

    private enum Boundary { NotFound, Error, Forbidden, Unauthorized }

    /// <summary>
    /// An exception's message and stack with error details, else the status's reason phrase, marked as withheld when it
    /// stands in for an exception's message. A status alone (a 403 from a loader) has only its reason phrase to show.
    /// </summary>
    private ErrorProps ErrorPropsFor(int status, Exception? exception) =>
        context.IncludeErrorDetails && exception is not null
            ? new ErrorProps(status, exception.Message, exception is SsrRenderException ssrError ? ssrError.JsStack : exception.ToString())
            : new ErrorProps(status, ReasonPhrases.GetReasonPhrase(status), null, Withheld: exception is not null);

    private PageRoute? RouteOf(HttpContext http) =>
        http.GetEndpoint()?.Metadata.GetMetadata<PageEndpointMetadata>()?.Pattern is { } pattern ? routes.Current.Find(pattern) : null;

    /// <param name="limit">Only segments before this index may render the boundary (a layout's own boundaries sit inside it), inside the layouts above it.</param>
    /// <param name="includeLimit">Also the limit segment's own boundary, still without its layout.</param>
    private async Task RenderBoundaryAsync(
        HttpContext http, Boundary boundary, int status, object? props, int? limit = null, bool includeLimit = false)
    {
        var table = routes.Current;
        var route = RouteOf(http);
        var parameters = route is null ? NoParams : ParamsFor(http, route);
        var segments = route?.Segments ?? [];
        // The page whose parallel route slots the segments show; on a mount's chain there is none, and they show their defaults.
        var slotsOf = route;
        var mount = http.Items[MountChainKey] as MountChain
            ?? (boundary == Boundary.NotFound && limit is null ? await MountChainAsync(http, table, route) : null);
        if (mount is not null)
        {
            (segments, parameters, slotsOf) = (mount.Segments, mount.Parameters, null);
            if (mount.Denied is { } denied && denied < (limit ?? int.MaxValue))
            {
                (limit, includeLimit) = (denied, false);
            }
        }

        var kind = boundary switch
        {
            Boundary.NotFound => "notFound",
            Boundary.Forbidden => "forbidden",
            Boundary.Unauthorized => "unauthorized",
            _ => "error",
        };
        var max = limit ?? int.MaxValue;
        if (boundary == Boundary.Error && http.Items[FailedSegmentKey] is int failed)
        {
            max = Math.Min(max, failed);
        }

        // The nearest folder with a matching file renders it in place of its children, inside the layouts above.
        var own = includeLimit && max < segments.Count;
        for (var i = own ? max : Math.Min(segments.Count, max) - 1; i >= 0; i--)
        {
            var file = boundary switch
            {
                Boundary.NotFound => segments[i].NotFound,
                Boundary.Forbidden => segments[i].Forbidden,
                Boundary.Unauthorized => segments[i].Unauthorized,
                _ => segments[i].Error,
            };
            if (file is null)
            {
                continue;
            }

            // The limit segment's own boundary renders in place of the whole segment, layout included.
            var loaded = await LoadSegmentsAsync(http, [.. segments.Take(i < max ? i + 1 : i)], slotsOf);
            if (loaded.Stop is { } stop)
            {
                await StopAsync(http, stop, loaded.StoppedAt);
                return;
            }

            var chain = loaded.Segments;
            if (boundary == Boundary.Error && i < max)
            {
                // An error boundary catches errors below its segment, so it is not wrapped in itself.
                chain[^1] = chain[^1] with { Error = null };
            }

            await RenderAsync(http, route?.Pattern, file, kind, chain, props, status, parameters);
            return;
        }

        if (boundary is Boundary.Forbidden or Boundary.Unauthorized)
        {
            await RenderBoundaryAsync(http, Boundary.Error, status, props, limit, includeLimit);
            return;
        }

        var special = boundary == Boundary.NotFound ? table.NotFound : table.Error;
        var specialSegments = (special?.Segments ?? table.RootSegments).Take(max).ToList();
        var root = await LoadSegmentsAsync(http, specialSegments, slotsOf);
        if (root.Stop is { } rootStop)
        {
            await StopAsync(http, rootStop, root.StoppedAt);
            return;
        }

        await RenderAsync(http, route?.Pattern, special?.File, kind, root.Segments, props, status, parameters);
    }

    /// <summary>
    /// A 404 on a page package's mount: its segments, the route values of the payload, and the index of the first segment
    /// whose layout loader's authorization this user fails, from which down nothing loads or renders.
    /// </summary>
    private sealed record MountChain(IReadOnlyList<RouteSegment> Segments, IReadOnlyDictionary<string, object?> Parameters, int? Denied);

    /// <summary>
    /// The chain a 404 renders on when its URL is below a page package's mount that the request's page does not reach
    /// (the fallback took it, or an app catch-all returned <c>Page.NotFound()</c>): the folders down to the deepest such
    /// mount, from every source, so the package's not-found renders inside its layouts. The prefix's values go into the
    /// route values, for the layout loaders and the payload's mounts. The fallback endpoint carries no layout loader's
    /// authorization and no mount's conventions, so each loader on the chain, and each segment with a package's files
    /// under that package's mount's conventions, is authorized here before any of them runs. Null elsewhere.
    /// </summary>
    private async Task<MountChain?> MountChainAsync(HttpContext http, RouteTable table, PageRoute? route)
    {
        var path = http.Request.Path.Value ?? "";
        var (mounted, values) = sources.Mounts
            .Select(m => (Mounted: m, Values: m.Mount.Prefix.Match(path)))
            .Where(m => m.Values is not null)
            .OrderByDescending(m => PartsOf(m.Mounted.Mount.Pattern).Length)
            .ThenByDescending(m => m.Mounted.Mount.Prefix.Segments.Count)
            .FirstOrDefault();
        if (mounted is null || values is null)
        {
            return null;
        }

        // A page inside the mount has every segment of the chain, and its own not-found is as near as the mount's or nearer.
        var segments = table.SegmentsAt(mounted.Mount.Folder);
        var own = route?.Segments ?? [];
        if (segments.Count <= own.Count && segments.Zip(own).All(pair => pair.First.Id == pair.Second.Id))
        {
            return null;
        }

        // Nor does a page outside it whose own nearest not-found is at the mount's URL or below it (app/(x)/help, or
        // app/blog under a mount at /(site)); the root's is at the root URL, so a mount there leaves unknown URLs to it.
        if ((route?.Segments ?? table.RootSegments).LastOrDefault(s => s.NotFound is not null) is { } nearest
            && mounted.Mount.Prefix.Below(nearest.Pattern) is not null)
        {
            return null;
        }

        var parameters = route is null ? new Dictionary<string, object?>(StringComparer.Ordinal) : ParamsFor(http, route);
        foreach (var (name, value) in values)
        {
            http.Request.RouteValues[name] = value;
            parameters[name] = value;
        }

        var chain = new MountChain(segments, parameters, await FirstDeniedSegmentAsync(http, segments));
        http.Items[MountChainKey] = chain;
        return chain;
    }

    public Task RedirectAsync(HttpContext http, string location, bool permanent) =>
        RedirectToAsync(http, UnderBasePath(http, location), permanent);

    /// <summary>
    /// Ends a page request whose visitor left (its loader, the render or a deferred value stopped on
    /// <c>RequestAborted</c>) without an error, and keeps what it sent out of the output cache. The output cache has
    /// identical requests wait for the render in progress and hands them its result, a failure included: an error here
    /// would fail every one of them, and a stored copy would be cut off where the visitor left. With neither, each
    /// renders the page for itself.
    /// </summary>
    internal void EndAborted(HttpContext http, OperationCanceledException exception)
    {
        if (http.Features.Get<IOutputCacheFeature>() is { } outputCache)
        {
            outputCache.Context.AllowCacheStorage = false;
        }

        logger.LogDebug(exception, "The request for {Path} was aborted before its page was sent", http.Request.Path);
    }

    /// <summary>
    /// <see cref="RedirectAsync"/> to a location that is complete as it is, its base path included: one built from the
    /// request target, whose base path may be spelled in a way <see cref="UnderBasePath(HttpContext, string)"/> does not
    /// recognise (<c>/b%61se</c> for <c>/base</c>).
    /// </summary>
    internal async Task RedirectToAsync(HttpContext http, string location, bool permanent)
    {
        http.Response.Headers.Append(HeaderNames.Vary, new StringValues([NetjesHeaders.Data, NetjesHeaders.Segments]));
        if (http.RequestServices.GetService<IDraftMode>()?.IsEnabled ?? false)
        {
            // Where a draft leads (an unpublished slug, say) is draft content too: kept nowhere, like a draft page.
            NoStore.Force(http, "no-store, private");
        }

        if (IsDataRequest(http))
        {
            KeepUnkeyedPagesOutOfTheOutputCache(http, StatusCodes.Status200OK);
            await WriteJsonAsync(http, StatusCodes.Status200OK, new RedirectPayload(location, assets.Version));
            return;
        }

        var isRead = HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method);
        http.Response.StatusCode = !isRead
            ? StatusCodes.Status303SeeOther
            : permanent ? StatusCodes.Status308PermanentRedirect : StatusCodes.Status307TemporaryRedirect;
        http.Response.Headers.Location = location;
        if (context.Command is { Name: "export" })
        {
            // Tells the export this redirect is the page's own (Page.Redirect), not a login or HTTPS redirect.
            http.Response.Headers[ExportRedirectHeader] = "1";
        }
    }

    internal const string ExportRedirectHeader = "X-Netjes-Export-Redirect";

    private async Task RenderAsync(
        HttpContext http,
        string? route,
        string? page,
        string? pageKind,
        IReadOnlyList<SegmentPayload> segments,
        object? props,
        int status,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyList<PageQuery>? queries = null,
        PageMetadata? metadata = null)
    {
        var isData = IsDataRequest(http);
        var clientRender = !isData && RendersInTheBrowser();
        using var telemetry = NetjesTelemetry.StartRender(http, route, page, sources.OwnerOf(page)?.Name, isData);
        var actionState = http.Items[ServerActionResult.StateKey] as ActionStatePayload;
        if (actionState is not null && status == StatusCodes.Status200OK)
        {
            // The page is rendered in place of a failed form post, with its errors.
            status = StatusCodes.Status422UnprocessableEntity;
        }

        var renderedAt = time.GetUtcNow().ToUnixTimeMilliseconds();
        var draftMode = http.RequestServices.GetService<IDraftMode>()?.IsEnabled ?? false;
        // Never stored: a preview of unpublished content, a visitor's form errors and input, and a shell for the browser
        // to render (the output cache would go on serving it after the sidecars are back). Refused before the payload
        // is built, since whether the cache may store it decides what it carries (the antiforgery token).
        if ((draftMode || actionState is not null || clientRender) && http.Features.Get<IOutputCacheFeature>() is { } outputCache)
        {
            outputCache.Context.AllowCacheStorage = false;
        }

        KeepUnkeyedPagesOutOfTheOutputCache(http, status);

        // Before AntiforgeryFor (below): antiforgery adds X-Frame-Options: SAMEORIGIN when it stores a new cookie and
        // finds none, which would give a visitor's first document another framing header than the rest.
        var frameAncestors = isData ? null : SecurityHeaders(http, context, draftMode);
        // With AddNetjesLocalization, the request culture (from the [lang] segment) is the locale and the page's lang.
        var locale = http.RequestServices.GetService<LocalizationMarker>() is null ? null : System.Globalization.CultureInfo.CurrentUICulture.Name;
        IReadOnlyList<string> modules = [.. segments.SelectMany(s => s.Modules()), .. page is null ? Array.Empty<string>() : [page]];
        var documentAssets = isData ? null : UnderBasePath(http, assets.Resolve(modules));
        var errorPage = routes.Current.Error;
        var payload = new PagePayload(
            Route: route,
            Page: page,
            PageKind: pageKind,
            Segments: segments,
            Props: props ?? new Dictionary<string, object?>(),
            Params: parameters,
            Shared: context.Options.SharedProps is { } shared ? await shared(http) : null,
            Status: status,
            Url: http.Request.PathBase + http.Request.Path + http.Request.QueryString,
            Version: assets.Version,
            RenderedAt: renderedAt,
            Antiforgery: null,
            Queries: QueriesFor([.. LayoutQueries(http, segments), .. queries ?? []], renderedAt),
            ActionState: actionState,
            Metadata: metadata,
            // Only client navigations can be intercepted; a document always shows the real page.
            Intercepts: isData && pageKind is null && route is not null && routes.Current.InterceptsFor(route) is { Count: > 0 } intercepts
                ? [.. intercepts.Select(i => new InterceptPayload(i.Segment, i.Slot, i.Page, ParamsOrNull(SlotPattern.ParamsFor(PartsOf(i.Pattern), UrlSegments(http)))))]
                : null,
            DraftMode: draftMode,
            BasePath: http.Request.PathBase.HasValue ? http.Request.PathBase.Value : null,
            Locale: locale,
            Mounts: MountsFor(http),
            Document: documentAssets is null
                ? null
                : new DocumentPayload(
                    locale ?? context.Options.Lang,
                    documentAssets,
                    errorPage is null ? null : new SpecialPagePayload(errorPage.File, [.. errorPage.Segments.Select(s => SegmentPayload.From(s))]),
                    ImagesFor(http, context.Options.Images),
                    PublicConfig.From(configuration, jsonOptions.Value.SerializerOptions.PropertyNamingPolicy),
                    ContentSecurityPolicyFor(http, frameAncestors, documentAssets.Scripts),
                    context.Options.CollectWebVitals ? UnderBasePath(http, WebVitalsEndpoint.Path) : null,
                    clientRender));
        // After the document part, which keeps a page with a CSP nonce out of the output cache. A static export has no
        // server to check tokens, and must not bake one into every visitor's HTML.
        payload = payload with { Antiforgery = context.Command is { Name: "export" } ? null : AntiforgeryFor(http) };

        http.Response.Headers.Append(HeaderNames.Vary, new StringValues([NetjesHeaders.Data, NetjesHeaders.Segments]));
        if (draftMode)
        {
            // A preview of unpublished content: never kept anywhere (and not stored by the output cache, above).
            NoStore.Force(http, "no-store, private");
        }

        if (clientRender)
        {
            // A degraded page that no cache may go on serving after the sidecars are back (the output cache does not
            // store it, above), whatever the page's own headers allow.
            NoStore.Force(http, "no-store");
        }

        DefaultCacheControl(http, status);
        var collector = new DeferredCollector();
        DeferredCollector.Current = collector;
        string json;
        try
        {
            json = JsonSerializer.Serialize(payload, jsonOptions.Value.SerializerOptions);
        }
        finally
        {
            DeferredCollector.Current = null;
        }

        if (isData)
        {
            http.Response.StatusCode = status;
            if (collector.Values.Count == 0)
            {
                http.Response.ContentType = "application/json; charset=utf-8";
                await http.Response.WriteAsync(json, http.RequestAborted);
                return;
            }

            // The payload first, then each deferred value as a line of its own once it completes, for at most
            // StreamTimeout: what has not arrived by then is reported as failed, so no boundary waits forever.
            http.Response.ContentType = "application/x-ndjson; charset=utf-8";
            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await WriteLineAsync(http, json);
            using var streamTimeout = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            streamTimeout.CancelAfter(context.Options.StreamTimeout);
            await ResolveDeferredAsync(http, collector, streamTimeout.Token, (id, valueJson, error) =>
                WriteLineAsync(http, DeferredLine(id, valueJson, error)));
            return;
        }

        if (clientRender)
        {
            logger.LogWarning(
                "No Node sidecar was ready to render {Route} ({Path}); the page was sent for the browser to render.",
                route ?? "(no route)", http.Request.Path.Value);
            NetjesTelemetry.SidecarFallbacks.Add(1, new KeyValuePair<string, object?>("netjes.route", route));
            await WriteClientShellAsync(http, status, payload.Document!, metadata, json, collector);
            return;
        }

        // A static export writes files, which gain nothing from streaming: a streamed file would keep the fallbacks,
        // with the deferred content hidden after them for an inline script to reveal, so it showed only with JavaScript.
        var waitForAll = !context.Options.StreamDocuments || context.Command is { Name: "export" } || IsCrawler(http);
        var renderId = collector.Values.Count > 0 ? Convert.ToHexString(RandomNumberGenerator.GetBytes(12)) : null;
        // Both deadlines run on the app's clock (TimeProvider), as the request's other times do.
        var limit = context.Options.StreamTimeout;
        using var stopAt = new CancellationTokenSource(DeferredDeadline(limit), time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, stopAt.Token);
        // Deferred values go to the sidecar as they complete; it may need them before the shell (waitForAll). They are
        // sent until the document is done or the sidecar's own deadline passes: the failures for values that ran out of
        // time come after `stop`, and a sidecar that stops answering (a crash) must not hold the request open.
        using var deliveryEnds = new CancellationTokenSource(limit, time);
        using var delivery = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, deliveryEnds.Token);
        // The render is asked for first, so it has a sidecar process before any value is delivered (the sidecar keeps
        // values that arrive before the render reads them).
        var rendering = ssr.RenderStreamAsync(HtmlSafeJson.Escape(json), new SsrStreamOptions(waitForAll, renderId), http.RequestAborted);
        var resolving = renderId is null
            ? Task.CompletedTask
            : ResolveDeferredAsync(http, collector, stop.Token, (id, valueJson, error) => ssr.ResolveAsync(
                renderId, id, valueJson, error, delivery.Token));
        try
        {
            // Throws before anything is sent if the shell fails, so the error page can still take over.
            await using var html = await rendering;
            http.Response.StatusCode = status;
            http.Response.ContentType = "text/html; charset=utf-8";
            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await html.ReadAsync(buffer, http.RequestAborted)) > 0)
            {
                await http.Response.Body.WriteAsync(buffer.AsMemory(0, read), http.RequestAborted);
                await http.Response.Body.FlushAsync(http.RequestAborted);
            }
        }
        finally
        {
            // The document is complete (or failed): values the page never read are not waited for, nor delivered.
            await stop.CancelAsync();
            await delivery.CancelAsync();
            try
            {
                await resolving;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // A sidecar that went away mid-delivery: the render has failed or finished already.
                logger.LogWarning(ex, "Delivering deferred values for {Path} failed: {Message}", http.Request.Path, ex.Message);
            }
        }
    }

    /// <summary>
    /// Whether this document goes to the browser to render: no sidecar process is ready at this moment (they have all
    /// exited, or stopped taking connections, and are restarting). A render that started on a process and fails after
    /// that keeps its error, as it may be the page that brought the process down. Production only: in development the
    /// sidecar also runs the Vite dev server the client loads from. Never for a command: an export writes what the
    /// server rendered, and must fail without it.
    /// </summary>
    private bool RendersInTheBrowser() =>
        context.Options.ClientRenderFallback
        && context.Mode == NetjesMode.Production
        && context.Command is null
        && ssr is SidecarPool { ReadyCount: 0 };

    /// <summary>
    /// The document without the page (<see cref="ClientRenderShell"/>), for the client entry to render. Deferred values
    /// follow it as the scripts a server-rendered document carries, each once it completes, for at most StreamTimeout,
    /// so the page shows its Suspense fallbacks at once, as it would if the sidecar had rendered it.
    /// </summary>
    private async Task WriteClientShellAsync(
        HttpContext http, int status, DocumentPayload document, PageMetadata? metadata, string json, DeferredCollector collector)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "text/html; charset=utf-8";
        var shell = ClientRenderShell.Start(document, metadata, HtmlSafeJson.Escape(json));
        if (collector.Values.Count == 0)
        {
            await http.Response.WriteAsync(shell + ClientRenderShell.End, http.RequestAborted);
            return;
        }

        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await WriteFlushedAsync(http, shell);
        using var streamTimeout = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        streamTimeout.CancelAfter(context.Options.StreamTimeout);
        await ResolveDeferredAsync(http, collector, streamTimeout.Token, (id, valueJson, error) =>
            WriteFlushedAsync(http, ClientRenderShell.Value(id, valueJson, error, document.Nonce)));
        await http.Response.WriteAsync(ClientRenderShell.End, http.RequestAborted);
    }

    private static async Task WriteFlushedAsync(HttpContext http, string text)
    {
        await http.Response.WriteAsync(text, http.RequestAborted);
        await http.Response.Body.FlushAsync(http.RequestAborted);
    }

    /// <summary>
    /// The error sent for a deferred value whose message is withheld (no error details): empty, so the client can tell
    /// it from Netjes's own messages and put a stand-in marked as withheld in its place.
    /// </summary>
    private const string WithheldError = "";

    /// <summary>One NDJSON line: <c>{"id":"d0","value":...}</c>, the value already JSON, or <c>{"id":"d0","error":"..."}</c>.</summary>
    private static string DeferredLine(string id, string? valueJson, string? error) =>
        error is null
            ? $"{{\"id\":{JsonSerializer.Serialize(id)},\"value\":{valueJson ?? "null"}}}"
            : $"{{\"id\":{JsonSerializer.Serialize(id)},\"error\":{JsonSerializer.Serialize(error)}}}";

    private static async Task WriteLineAsync(HttpContext http, string json)
    {
        await http.Response.WriteAsync(json + "\n", http.RequestAborted);
        await http.Response.Body.FlushAsync(http.RequestAborted);
    }

    /// <summary>
    /// Starts each deferred value and hands it (as JSON) or its failure to <paramref name="deliver"/> in the order they
    /// complete. When <paramref name="cancellation"/> fires, what is still pending is reported as failed (the
    /// delivery itself may then fail too, if the request is gone).
    /// </summary>
    private async Task ResolveDeferredAsync(
        HttpContext http, DeferredCollector collector, CancellationToken cancellation, Func<string, string?, string?, Task> deliver)
    {
        var scopes = http.RequestServices.GetRequiredService<IServiceScopeFactory>();
        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Netjes.Deferred");
        var pending = collector.Values.ToDictionary(v => v.Start(scopes), v => v.Id);
        try
        {
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending.Keys).WaitAsync(cancellation);
                var id = pending[done];
                pending.Remove(done);
                if (done.IsCompletedSuccessfully)
                {
                    string json;
                    try
                    {
                        json = JsonSerializer.Serialize(done.Result, jsonOptions.Value.SerializerOptions);
                    }
                    catch (Exception ex)
                    {
                        // A value that cannot be written (a cycle, a System.Type, a getter on a disposed DbContext, one
                        // that throws OperationCanceledException: serializing takes no token) fails its own boundary only.
                        logger.LogWarning(ex, "Deferred value {Id} for {Path} could not be serialized: {Message}", id, http.Request.Path, ex.Message);
                        await deliver(id, null, context.IncludeErrorDetails ? ex.Message : WithheldError);
                        continue;
                    }

                    await deliver(id, json, null);
                    continue;
                }

                if (done.IsCanceled)
                {
                    logger.LogWarning("Deferred value {Id} for {Path} was cancelled", id, http.Request.Path);
                    await deliver(id, null, "The value was cancelled");
                    continue;
                }

                var error = done.Exception?.GetBaseException();
                logger.LogWarning(error, "Deferred value {Id} for {Path} failed: {Message}", id, http.Request.Path, error?.Message);
                await deliver(id, null, context.IncludeErrorDetails && error is not null ? error.Message : WithheldError);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested && !http.RequestAborted.IsCancellationRequested)
        {
            foreach (var (task, id) in pending)
            {
                // Still observe them, so a late failure is logged rather than lost.
                _ = task.ContinueWith(
                    t => logger.LogWarning(t.Exception?.GetBaseException(), "Deferred value {Id} for {Path} failed after the response ended", id, http.Request.Path),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                try
                {
                    await deliver(id, null, "The value did not arrive in time");
                }
                catch (Exception) when (http.RequestAborted.IsCancellationRequested || cancellation.IsCancellationRequested)
                {
                    // The render already ended; nobody is waiting for it.
                }
            }
        }
    }

    private bool IsCrawler(HttpContext http)
    {
        var userAgent = http.Request.Headers.UserAgent.ToString();
        return userAgent.Length > 0 && crawlers.Value.IsMatch(userAgent);
    }

    private async Task WriteJsonAsync<T>(HttpContext http, int status, T payload)
    {
        var json = JsonSerializer.Serialize(payload, jsonOptions.Value.SerializerOptions);
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json; charset=utf-8";
        await http.Response.WriteAsync(json, http.RequestAborted);
    }

    /// <summary>
    /// When a document's deferred values that have not arrived are reported as failed: a little before the sidecar's
    /// own <see cref="NetjesOptions.StreamTimeout"/> ends the render (a tenth of it, at most a second), so the
    /// document shows those boundaries as failed instead of being cut off.
    /// </summary>
    internal static TimeSpan DeferredDeadline(TimeSpan streamTimeout) =>
        streamTimeout - TimeSpan.FromTicks(Math.Min(TimeSpan.TicksPerSecond, streamTimeout.Ticks / 10));

    /// <summary>
    /// nosniff, a referrer policy and framing headers on documents, each unless the app set it. Unless the app sent
    /// X-Frame-Options, framing follows the <c>frame-ancestors</c> in the policies that go out (the app's own header
    /// or, when it sent none, <see cref="NetjesOptions.ContentSecurityPolicy"/>): the X-Frame-Options they imply, and
    /// no <c>frame-ancestors</c> of Netjes's own. Without any, it is same-origin: X-Frame-Options SAMEORIGIN, and the
    /// sources this returns for Netjes's own Content-Security-Policy (null for none). Runs before the payload is built,
    /// and so before <c>AntiforgeryFor</c>: antiforgery adds its own X-Frame-Options when it stores a cookie, unless
    /// one is already there.
    /// </summary>
    internal static string? SecurityHeaders(HttpContext http, NetjesContext context, bool draftMode)
    {
        if (!context.Options.SecurityHeaders.Enabled)
        {
            return null;
        }

        var headers = http.Response.Headers;
        headers.TryAdd(HeaderNames.XContentTypeOptions, "nosniff");
        headers.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");
        if (headers.ContainsKey(HeaderNames.XFrameOptions))
        {
            // The app chose who may frame its pages. Netjes adds nothing that says otherwise: browsers obey
            // frame-ancestors over X-Frame-Options, so a 'self' would undo an app's DENY. Antiforgery's SAMEORIGIN, from
            // app code that stored a token before the render, is kept the same way.
            return null;
        }

        // The option's policy goes out only when the app sent none (ContentSecurityPolicyFor), so only then does it count.
        var ownPolicy = headers.ContainsKey(HeaderNames.ContentSecurityPolicy);
        if (XFrameOptionsFor(ownPolicy ? headers.ContentSecurityPolicy : context.Options.ContentSecurityPolicy) is { } implied)
        {
            // The app chose who may frame its pages (no one, say, or a partner site too). Browsers that know only
            // X-Frame-Options get the closest it can say, and antiforgery, which comes later, adds none of its own.
            headers.XFrameOptions = implied;
            return null;
        }

        // In draft mode too: next to the sites' frame-ancestors below, browsers obey those and ignore this (CSP 3,
        // the HTML standard), and it still holds if a later middleware rewrites the policy without them.
        headers.TryAdd(HeaderNames.XFrameOptions, "SAMEORIGIN");
        if (draftMode && context.DraftFrameAncestors is { } others && !ownPolicy)
        {
            // A CMS showing the preview in its frame: only frame-ancestors can name another site.
            var ancestors = $"'self' {others}";
            if (context.Options.ContentSecurityPolicy is null)
            {
                headers.ContentSecurityPolicy = $"frame-ancestors {ancestors}";
            }

            return ancestors;
        }

        return "'self'";
    }

    /// <summary>
    /// The X-Frame-Options that the <c>frame-ancestors</c> of these policies imply, null when none has the directive.
    /// Browsers enforce every policy, so the strictest decides. A list that could let the app's own pages frame it
    /// (<c>'self'</c>, <c>*</c> or a scheme source such as <c>https:</c>) gets SAMEORIGIN, the closest X-Frame-Options
    /// comes, since it cannot name other sites; any other list (<c>'none'</c>, an empty one, or other sites only) gets
    /// DENY. A host source never counts, even one naming the app's own origin: behind a proxy, the request's host is
    /// not always the one the browser sees.
    /// </summary>
    private static string? XFrameOptionsFor(StringValues policies)
    {
        string? implied = null;
        foreach (var policy in CspDirectives.Policies(policies))
        {
            if (CspDirectives.Sources(policy, "frame-ancestors") is not { } sources)
            {
                continue;
            }

            if (!sources.Any(MayMatchTheAppsOwnPages))
            {
                return "DENY";
            }

            implied = "SAMEORIGIN";
        }

        return implied;
    }

    private static bool MayMatchTheAppsOwnPages(string source) =>
        source == "*"
        || source.Equals("'self'", StringComparison.OrdinalIgnoreCase)
        // The scheme sources an http(s) page can match, as CspChecks counts them; data:, blob: and the rest match none.
        || source.Equals("http:", StringComparison.OrdinalIgnoreCase)
        || source.Equals("https:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The nonce for this document: Netjes's own policy gets one (and its header) when it has a <c>{nonce}</c> for it,
    /// and so does any app that asked for <c>GetCspNonce()</c> to write its own policy. Either way the document is kept
    /// out of the output cache. A policy without <c>{nonce}</c> (hash-based, as cached pages need) gets none. The
    /// policy that goes out, as written, is checked against what the document needs (<see cref="WarnAboutThePolicy"/>).
    /// </summary>
    private string? ContentSecurityPolicyFor(HttpContext http, string? frameAncestors, IReadOnlyList<string> scripts)
    {
        var policy = context.Options.ContentSecurityPolicy;
        var nonce = policy?.Contains("{nonce}", StringComparison.Ordinal) == true ? http.GetCspNonce() : http.PeekCspNonce();
        if (policy is not null && !http.Response.Headers.ContainsKey(HeaderNames.ContentSecurityPolicy))
        {
            policy = policy.Replace("{nonce}", nonce, StringComparison.Ordinal);
            http.Response.Headers.ContentSecurityPolicy = frameAncestors is null ? policy : WithFrameAncestors(policy, frameAncestors);
        }

        if (nonce is not null)
        {
            KeepNonceOutOfTheOutputCache(http);
        }

        WarnAboutThePolicy(http, scripts, nonce);
        return nonce;
    }

    /// <summary>
    /// Warns about what the Content-Security-Policy on this document refuses of what it needs (<see cref="CspChecks"/>):
    /// the app's own header, or else the option's, against the document's scripts and <paramref name="nonce"/>. Netjes
    /// sends it as written, in Development too, and warns once per policy. A nonce changes with every document, so
    /// policies are told apart by their text with the document's nonce, and any other, each written as one value
    /// (<see cref="CspChecks.WithoutNonces"/>), and once there have been <see cref="MaxCheckedPolicies"/> of them, a new
    /// one is not checked. Not for a command: the static export writes files, which go out under the static host's
    /// policy.
    /// </summary>
    private void WarnAboutThePolicy(HttpContext http, IReadOnlyList<string> scripts, string? nonce)
    {
        var policies = http.Response.Headers.ContentSecurityPolicy;
        if (context.Command is not null || StringValues.IsNullOrEmpty(policies))
        {
            return;
        }

        var key = CspChecks.WithoutNonces(policies.ToString(), nonce);
        if (_checkedPolicies.ContainsKey(key) || Volatile.Read(ref _checkedPolicyCount) >= MaxCheckedPolicies || !_checkedPolicies.TryAdd(key, true))
        {
            return;
        }

        Interlocked.Increment(ref _checkedPolicyCount);
        var document = new CspDocument(scripts, http.Request.Scheme, http.Request.Host.Value ?? "", nonce, assets.DevServerOrigin);
        foreach (var finding in CspChecks.Check(policies, document))
        {
            CspChecks.Warn(logger, finding);
        }
    }

    /// <summary>The policy with a <c>frame-ancestors</c> directive added, unless it has one of its own.</summary>
    private static string WithFrameAncestors(string policy, string sources)
    {
        if (HasFrameAncestors(policy))
        {
            return policy;
        }

        var rest = policy.TrimEnd(';', ' ', '\t').Trim();
        return rest.Length == 0 ? $"frame-ancestors {sources}" : $"{rest}; frame-ancestors {sources}";
    }

    private static bool HasFrameAncestors(string policy) =>
        CspDirectives.Policies(policy).Any(p => CspDirectives.Sources(p, "frame-ancestors") is not null);

    /// <summary>
    /// A stored copy would hand every visitor the same nonce, which anyone can read and reuse: the nonce stops
    /// protecting anything. So a page with a nonce is never stored, and the app hears about it once per page.
    /// </summary>
    private void KeepNonceOutOfTheOutputCache(HttpContext http)
    {
        if (http.Features.Get<IOutputCacheFeature>() is not { Context.EnableOutputCaching: true } outputCache)
        {
            return;
        }

        outputCache.Context.AllowCacheStorage = false;
        var page = http.GetEndpoint()?.DisplayName ?? http.Request.Path.Value ?? "";
        if (_warnedNonceCaching.TryAdd(page, true))
        {
            logger.LogWarning(
                "{Page} is output-cached, but its documents carry a CSP nonce (from NetjesOptions.ContentSecurityPolicy or " +
                "GetCspNonce()), which a stored copy would give every visitor, so it is rendered each time instead. " +
                "For cached pages, use a hash-based Content-Security-Policy instead of a nonce.",
                page);
        }
    }

    /// <summary>
    /// A page the output cache would store under a key the page rules did not make (an endpoint that is no page, such
    /// as a minimal API returning <c>Page.Render</c>, cached by <c>CacheOutput</c> or a base policy) could be served
    /// for a document request when it is a payload, or the other way round. It is rendered each time instead, and the
    /// app hears about it once per endpoint: <c>CachePage</c> keys such an endpoint by the page rules. Only about a 200,
    /// the one status ASP.NET Core's default policy stores: a not-found page was never going to be kept. A query string
    /// outside the page's <c>CacheQueryKeys</c> has no key of the page rules either, as the page asked, so it is rendered
    /// each time without a warning.
    /// </summary>
    private void KeepUnkeyedPagesOutOfTheOutputCache(HttpContext http, int status)
    {
        if (http.Features.Get<IOutputCacheFeature>() is not { Context: { EnableOutputCaching: true, AllowCacheStorage: true } } outputCache)
        {
            return;
        }

        if (CachePaths.LeavesQueryUncached(http))
        {
            outputCache.Context.AllowCacheStorage = false;
            return;
        }

        if (PageCachePolicy.Applied(http))
        {
            return;
        }

        outputCache.Context.AllowCacheStorage = false;
        var endpoint = http.GetEndpoint()?.DisplayName ?? http.Request.Path.Value ?? "";
        if (status != StatusCodes.Status200OK || !_warnedUnkeyedCaching.TryAdd(endpoint, true))
        {
            return;
        }

        if (PageCachePolicy.RanBeforeRouting(http))
        {
            logger.LogWarning(
                "{Endpoint} renders a page and is output-cached, but the output cache runs before routing (UseOutputCache() " +
                "is called before UseNetjes()), so no policy of the endpoint applies and nothing would keep its documents " +
                "and client payloads apart in the cache. The page is not stored and renders each time. Call " +
                "UseOutputCache() after UseNetjes().",
                endpoint);
            return;
        }

        logger.LogWarning(
            "{Endpoint} renders a page and is output-cached, but not with CachePage, so nothing would keep its documents " +
            "and client payloads apart in the cache. The page is not stored and renders each time. Cache the endpoint " +
            "with .CachePage(duration) instead.",
            endpoint);
    }

    /// <summary>
    /// Pages carry per-user data (shared props, antiforgery tokens), so no cache keeps them, as Next.js sends its
    /// dynamic pages: browsers show a kept page again on back and forward without revalidating it, <c>no-cache</c> or
    /// not, so after signing out Back would show the signed-out user's page. A visitor's page is no exception: its
    /// document stays when the client router signs the visitor in, and then shows their pages (ADR 0014). Pages that
    /// chose their own header keep theirs, and so do responses the output cache stores. A render of an output-cached
    /// page that is not stored (a signed-in user, an Authorization header, a nonce, a query string outside its
    /// <c>CacheQueryKeys</c>) is one visitor's page, so it gets
    /// the default too, and so does an output-cached page in the Development environment, which renders for every
    /// request and is never stored (ADR 0023): a browser that kept it would show it from before an edit on Back.
    /// </summary>
    private static void DefaultCacheControl(HttpContext http, int status)
    {
        if (http.Response.Headers.ContainsKey(HeaderNames.CacheControl)
            || (IsStoredByOutputCache(http, status) && !PageCachePolicy.RendersEachTimeInDevelopment(http)))
        {
            return;
        }

        http.Response.Headers.CacheControl = "no-store, private";
    }

    /// <summary>
    /// The endpoint is output-cached and this response will be stored: storage is still allowed, and it is what
    /// ASP.NET Core's default policy keeps (a 200 without cookies).
    /// </summary>
    private static bool IsStoredByOutputCache(HttpContext http, int status)
    {
        var metadata = http.GetEndpoint()?.Metadata;
        return (metadata?.GetMetadata<IOutputCachePolicy>() is not null || metadata?.GetMetadata<OutputCacheAttribute>() is not null)
            && http.Features.Get<IOutputCacheFeature>() is { Context: { EnableOutputCaching: true, AllowCacheStorage: true } }
            && status == StatusCodes.Status200OK
            && StringValues.IsNullOrEmpty(http.Response.Headers.SetCookie);
    }

    private static ImageConfigPayload ImagesFor(HttpContext http, NetjesImageOptions images) =>
        new(
            UnderBasePath(http, images.Path),
            [.. images.Widths.Where(w => w > 0).Distinct().Order()],
            Images.ImageEndpoint.QualitiesOf(images),
            http.Request.PathBase.HasValue ? http.Request.PathBase.Value : null);

    /// <summary>
    /// Root-relative URLs (<c>/x</c>, not <c>//host</c> or absolute ones) moved under <c>UsePathBase</c>'s base path,
    /// like Next.js's <c>basePath</c>: the app writes paths as if mounted at the root.
    /// </summary>
    internal static string UnderBasePath(HttpContext http, string url)
    {
        if (!http.Request.PathBase.HasValue || !url.StartsWith('/') || url.StartsWith("//", StringComparison.Ordinal)
            || url.StartsWith("/\\", StringComparison.Ordinal))
        {
            return url;
        }

        // Already under the base (built with LinkGenerator or Url.Content("~/...")): not prefixed twice.
        var basePath = http.Request.PathBase.Value!;
        var alreadyThere = url == basePath || (url.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) && url.Length > basePath.Length
            && url[basePath.Length] is '/' or '?' or '#');
        return alreadyThere ? url : basePath + url;
    }

    private static DocumentAssets UnderBasePath(HttpContext http, DocumentAssets assets) =>
        http.Request.PathBase.HasValue
            ? new DocumentAssets(
                [.. assets.Stylesheets.Select(url => UnderBasePath(http, url))],
                [.. assets.ModulePreloads.Select(url => UnderBasePath(http, url))],
                [.. assets.Scripts.Select(url => UnderBasePath(http, url))])
            : assets;

    private static List<QueryPayload>? QueriesFor(IReadOnlyList<PageQuery>? queries, long updatedAt) =>
        queries is { Count: > 0 } ? [.. queries.Select(q => new QueryPayload(q.Key, q.Data, updatedAt))] : null;

    /// <summary>
    /// The visitor's antiforgery token, or none: not for <c>CachePage</c> pages, and not in a response the output cache
    /// may store (under any policy), which would hand the visitor's token to everyone it serves. Pages without one fetch
    /// a token before a post.
    /// </summary>
    private static AntiforgeryPayload? AntiforgeryFor(HttpContext http)
    {
        if (http.GetEndpoint()?.Metadata.GetMetadata<OmitAntiforgeryTokenMetadata>() is not null
            || http.Features.Get<IOutputCacheFeature>() is { Context: { EnableOutputCaching: true, AllowCacheStorage: true } }
            || http.RequestServices.GetService<IAntiforgery>() is not { } antiforgery)
        {
            return null;
        }

        // GetTokens has no side effects. Only a first visit needs the cookie stored (ASP.NET Core then sends that one
        // response its own no-cache, no-store); later pages reuse the browser's cookie and set none.
        var tokens = antiforgery.GetTokens(http);
        if (tokens.CookieToken is not null)
        {
            tokens = antiforgery.GetAndStoreTokens(http);
        }

        return new AntiforgeryPayload(tokens.FormFieldName, tokens.HeaderName, tokens.RequestToken);
    }

    /// <summary>The route values of each slot page by name, from its own pattern (parameter names can differ from the page's).</summary>
    private static Dictionary<string, IReadOnlyDictionary<string, object?>>? SlotParamsFor(HttpContext http, RouteSegment segment, PageRoute route)
    {
        var below = UrlSegments(http).Skip(DepthOf(http, segment)).ToList();
        var result = new SortedDictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var slot in segment.Slots.Keys)
        {
            if (segment.SlotPageFor(slot, route).Pattern is { } pattern && ParamsOrNull(SlotPattern.ParamsFor(PartsOf(pattern), below)) is { } values)
            {
                result[slot] = values;
            }
        }

        return result.Count > 0 ? new Dictionary<string, IReadOnlyDictionary<string, object?>>(result, StringComparer.Ordinal) : null;
    }

    /// <summary>
    /// Each mounted page package's URL for this request, by npm name, which its links in the browser go below: null for
    /// a prefix whose value the request has none for (or an empty one, <c>.</c> or <c>..</c>). Null when nothing is
    /// mounted, which leaves the payload as it was.
    /// </summary>
    private JsonObject? MountsFor(HttpContext http)
    {
        if (sources.Mounts is not { Count: > 0 } mounts)
        {
            return null;
        }

        var result = new JsonObject();
        foreach (var mounted in mounts)
        {
            result[mounted.Name] = mounted.Mount.Prefix.Url(http.Request.RouteValues, out _);
        }

        return result;
    }

    private static IReadOnlyDictionary<string, object?>? ParamsOrNull(Dictionary<string, object?>? values) => values is { Count: > 0 } ? values : null;

    private static string[] PartsOf(string pattern) => pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// The request's path segments below the base path, as routing sees them: ASP.NET Core has already decoded the
    /// path, so decoding again would give slots different values than the page (and could put a slash in a segment).
    /// </summary>
    private static string[] UrlSegments(HttpContext http) => (http.Request.Path.Value ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// How many URL segments a layout's folder covers for this request, what <c>useSelectedLayoutSegment(s)</c> count
    /// from: a catch-all counts the segments it matched.
    /// </summary>
    private static int DepthOf(HttpContext http, RouteSegment segment) =>
        segment.Pattern.Split('/', StringSplitOptions.RemoveEmptyEntries).Sum(part =>
            part.StartsWith("[...", StringComparison.Ordinal) || part.StartsWith("[[...", StringComparison.Ordinal)
                ? (http.Request.RouteValues.TryGetValue(part.Trim('[', ']')[3..], out var raw) ? raw?.ToString() ?? "" : "")
                    .Split('/', StringSplitOptions.RemoveEmptyEntries).Length
                : 1);

    private static Dictionary<string, object?> ParamsFor(HttpContext http, PageRoute route)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var parameter in route.Parameters)
        {
            var value = http.Request.RouteValues.TryGetValue(parameter.Name, out var raw) ? raw?.ToString() : null;
            result[parameter.Name] = parameter.Kind == RouteParameterKind.Single
                ? value
                : (value ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        }

        return result;
    }

    internal static string ExpectedFile(NetjesContext context, string pattern)
    {
        var path = pattern.Trim('/');
        return path is "" ? $"{context.AppDirectory}/page.tsx" : $"{context.AppDirectory}/{path}/page.tsx";
    }

    /// <summary>
    /// The files that would serve <paramref name="pattern"/>: below a page package's mount, the deepest, the package's file
    /// and the app's; elsewhere the app's.
    /// </summary>
    private string ExpectedFiles(string pattern) =>
        sources.Mounts
            .Select(mounted => (Mounted: mounted, Below: mounted.Mount.Prefix.Below(pattern)))
            .Where(m => m.Below is not null)
            .OrderByDescending(m => m.Mounted.Mount.Prefix.Segments.Count)
            .FirstOrDefault() is ({ } mounted, { } below)
            ? $"{ExpectedFile(mounted, below)}, the page of '{mounted.Name}' mounted at {mounted.Mount.Prefix}, or {ExpectedFile(context, pattern)}"
            : ExpectedFile(context, pattern);

    /// <summary>The page package's file for <paramref name="below"/>, a URL pattern below its mount: <c>@acme/help-center/app/contact/page.tsx</c>.</summary>
    internal static string ExpectedFile(MountedPackage mounted, string below)
    {
        var path = below.Trim('/');
        return path is "" ? $"{mounted.Name}/{mounted.Install.Pages}/page.tsx" : $"{mounted.Name}/{mounted.Install.Pages}/{path}/page.tsx";
    }
}
