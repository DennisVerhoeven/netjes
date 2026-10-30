using System.Text.Json;
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
    ILogger<PageRenderer> logger)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _warnedNonceCaching = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _warnedUnkeyedCaching = new(StringComparer.Ordinal);

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

    public static bool IsDataRequest(HttpContext http) => http.Request.Headers.ContainsKey(NetjesHeaders.Data);

    public async Task RenderRouteAsync(
        HttpContext http, string pattern, object? props, int status, IReadOnlyList<PageQuery>? queries = null, PageMetadata? metadata = null)
    {
        var route = routes.Current.Find(pattern)
            ?? throw new InvalidOperationException(
                $"No page file matches '{pattern}'. Expected {ExpectedFile(context, pattern)}.");
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
    /// this user fails is where the page stops: its loader never runs, and its own boundary (or the nearest above it)
    /// renders inside the layouts above the section.
    /// </param>
    public async Task RenderStatusAsync(HttpContext http, int status, bool denied = false)
    {
        var limit = denied ? await FirstDeniedSegmentAsync(http) : null;
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

    private async Task<int?> FirstDeniedSegmentAsync(HttpContext http)
    {
        var segments = RouteOf(http)?.Segments ?? [];
        for (var i = 0; i < segments.Count; i++)
        {
            if (layoutLoaders.Find(segments[i].Id) is { } loader && !await loader.AuthorizesAsync(http))
            {
                return i;
            }
        }

        return null;
    }

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

    private ErrorProps ErrorPropsFor(int status, Exception? exception) =>
        context.IncludeErrorDetails && exception is not null
            ? new ErrorProps(status, exception.Message, exception is SsrRenderException ssrError ? ssrError.JsStack : exception.ToString())
            : new ErrorProps(status, ReasonPhrases.GetReasonPhrase(status), null);

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
            var loaded = await LoadSegmentsAsync(http, [.. segments.Take(i < max ? i + 1 : i)], route);
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

            await RenderAsync(http, route!.Pattern, file, kind, chain, props, status, parameters);
            return;
        }

        if (boundary is Boundary.Forbidden or Boundary.Unauthorized)
        {
            await RenderBoundaryAsync(http, Boundary.Error, status, props, limit, includeLimit);
            return;
        }

        var special = boundary == Boundary.NotFound ? table.NotFound : table.Error;
        var specialSegments = (special?.Segments ?? table.RootSegments).Take(max).ToList();
        var root = await LoadSegmentsAsync(http, specialSegments, route);
        if (root.Stop is { } rootStop)
        {
            await StopAsync(http, rootStop, root.StoppedAt);
            return;
        }

        await RenderAsync(http, route?.Pattern, special?.File, kind, root.Segments, props, status, parameters);
    }

    public async Task RedirectAsync(HttpContext http, string location, bool permanent)
    {
        location = UnderBasePath(http, location);
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
        using var telemetry = NetjesTelemetry.StartRender(http, route, page, isData);
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

        var frameAncestors = isData ? null : SecurityHeaders(http, context, draftMode);
        // With AddNetjesLocalization, the request culture (from the [lang] segment) is the locale and the page's lang.
        var locale = http.RequestServices.GetService<LocalizationMarker>() is null ? null : System.Globalization.CultureInfo.CurrentUICulture.Name;
        IReadOnlyList<string> modules = [.. segments.SelectMany(s => s.Modules()), .. page is null ? Array.Empty<string>() : [page]];
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
            Document: isData
                ? null
                : new DocumentPayload(
                    locale ?? context.Options.Lang,
                    UnderBasePath(http, assets.Resolve(modules)),
                    errorPage is null ? null : new SpecialPagePayload(errorPage.File, [.. errorPage.Segments.Select(s => SegmentPayload.From(s))]),
                    ImagesFor(http, context.Options.Images),
                    PublicConfig.From(configuration, jsonOptions.Value.SerializerOptions.PropertyNamingPolicy),
                    ContentSecurityPolicyFor(http, frameAncestors),
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
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        // A little before the sidecar's own StreamTimeout ends the render, so what never arrived is reported in time
        // for the document to show those boundaries as failed instead of being cut off.
        var limit = context.Options.StreamTimeout;
        stop.CancelAfter(limit - TimeSpan.FromTicks(Math.Min(TimeSpan.TicksPerSecond, limit.Ticks / 10)));
        // Deferred values go to the sidecar as they complete; it may need them before the shell (waitForAll). They are
        // sent until the document is done or the sidecar's own deadline passes: the failures for values that ran out of
        // time come after `stop`, and a sidecar that stops answering (a crash) must not hold the request open.
        using var delivery = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        delivery.CancelAfter(limit);
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
                        await deliver(id, null, context.IncludeErrorDetails ? ex.Message : "Something went wrong");
                        continue;
                    }

                    await deliver(id, json, null);
                    continue;
                }

                var error = done.Exception?.GetBaseException();
                logger.LogWarning(error, "Deferred value {Id} for {Path} failed: {Message}", id, http.Request.Path, error?.Message);
                await deliver(id, null, context.IncludeErrorDetails && error is not null ? error.Message : "Something went wrong");
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
    /// nosniff, a referrer policy and same-origin framing on documents, each unless the app set it (framing: unless it
    /// sent X-Frame-Options or a policy with <c>frame-ancestors</c>, its own header or, when it sent none,
    /// <see cref="NetjesOptions.ContentSecurityPolicy"/>). Returns the <c>frame-ancestors</c> sources for
    /// Netjes's own Content-Security-Policy, null for none. Runs before the payload is built: antiforgery adds its own
    /// X-Frame-Options when it stores a cookie, unless one is already there.
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
        // The option's policy goes out only when the app sent none (ContentSecurityPolicyFor), so only then does it count.
        var ownPolicy = headers.ContainsKey(HeaderNames.ContentSecurityPolicy);
        if (headers.ContainsKey(HeaderNames.XFrameOptions)
            || (ownPolicy ? headers.ContentSecurityPolicy.Any(HasFrameAncestors) : HasFrameAncestors(context.Options.ContentSecurityPolicy)))
        {
            // The app chose who may frame its pages (a partner site, say, or no one). Netjes adds nothing that says
            // otherwise: browsers obey frame-ancestors over X-Frame-Options, so a 'self' would undo an app's DENY.
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
    /// The nonce for this document: Netjes's own policy gets one (and its header) when it has a <c>{nonce}</c> for it,
    /// and so does any app that asked for <c>GetCspNonce()</c> to write its own policy. Either way the document is kept
    /// out of the output cache. A policy without <c>{nonce}</c> (hash-based, as cached pages need) gets none.
    /// </summary>
    private string? ContentSecurityPolicyFor(HttpContext http, string? frameAncestors)
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

        return nonce;
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

    private static bool HasFrameAncestors(string? policy) =>
        policy is not null && policy.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(d => d.Split([' ', '\t'], 2)[0].Equals("frame-ancestors", StringComparison.OrdinalIgnoreCase));

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
    /// the one status ASP.NET Core's default policy stores: a not-found page was never going to be kept.
    /// </summary>
    private void KeepUnkeyedPagesOutOfTheOutputCache(HttpContext http, int status)
    {
        if (http.Features.Get<IOutputCacheFeature>() is not { Context: { EnableOutputCaching: true, AllowCacheStorage: true } } outputCache
            || PageCachePolicy.Applied(http))
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
    /// page that is not stored (a signed-in user, an Authorization header, a nonce) is one visitor's page, so it gets
    /// the default too.
    /// </summary>
    private static void DefaultCacheControl(HttpContext http, int status)
    {
        if (http.Response.Headers.ContainsKey(HeaderNames.CacheControl) || IsStoredByOutputCache(http, status))
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
}
