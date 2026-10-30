using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Netjes.Rendering;
using Netjes.Routing;

namespace Netjes;

/// <summary>Results for page loaders and actions, mirroring Next.js <c>getServerSideProps</c> return values.</summary>
public static partial class Page
{
    /// <summary>
    /// Render the endpoint's page with these props. The result is typed by the props, so a loader's inferred return
    /// type names them (for generated TypeScript types and for unit tests of loaders).
    /// </summary>
    public static PageResult<TProps> Props<TProps>(TProps props) => new(new(PageResult.Kind.Props, props: props));

    /// <summary>Render the endpoint's page without props.</summary>
    public static PageResult<NoProps> Props() => new(new(PageResult.Kind.Props, props: NoProps.Instance));

    /// <summary>Render the page for <paramref name="pattern"/> (e.g. <c>/blog/[slug]</c>) from any endpoint.</summary>
    public static PageResult Render(string pattern, object? props = null) =>
        new(PageResult.Kind.Props, props: props, pattern: pattern);

    /// <summary>Render the nearest not-found page (<c>not-found.tsx</c>, else the root one) with status 404.</summary>
    public static PageResult NotFound() => new(PageResult.Kind.NotFound);

    /// <summary>Render the nearest <c>forbidden.tsx</c> (else the error page) with status 403.</summary>
    public static PageResult Forbidden() => new(PageResult.Kind.Status, statusCode: StatusCodes.Status403Forbidden);

    /// <summary>Render the nearest <c>unauthorized.tsx</c> (else the error page) with status 401.</summary>
    public static PageResult Unauthorized() => new(PageResult.Kind.Status, statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>
    /// Redirect: 307/308 for GET, 303 after a form post. The client router follows it without a full page load.
    /// Only relative and http(s) URLs are allowed. For URLs that come from the request, use <see cref="LocalRedirect"/>.
    /// </summary>
    public static PageResult Redirect(string location, bool permanent = false)
    {
        var normalized = Normalize(location);
        var scheme = SchemeRegex().Match(normalized);
        if (scheme.Success && !scheme.Groups[1].Value.Equals("http", StringComparison.OrdinalIgnoreCase)
            && !scheme.Groups[1].Value.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Refusing to redirect to a '{scheme.Groups[1].Value}:' URL.", nameof(location));
        }

        // A scheme ends at the first ':'; percent-encoding before it (javascript%3A) becomes one wherever the URL is decoded.
        var end = normalized.IndexOfAny(SchemeEnd);
        if (normalized.AsSpan(0, end < 0 ? normalized.Length : end).Contains('%'))
        {
            throw new ArgumentException(
                $"Refusing to redirect to '{location}': a '%' before the first ':' or '/' could hide a scheme. Start a relative URL with '/' or './'.",
                nameof(location));
        }

        return new(PageResult.Kind.Redirect, location: location, permanent: permanent);
    }

    /// <summary>Redirect to a path on this site, e.g. a <c>returnUrl</c> query value. Throws for anything else.</summary>
    public static PageResult LocalRedirect(string location, bool permanent = false)
    {
        if (!IsLocalUrl(location))
        {
            throw new ArgumentException($"'{location}' is not a local URL.", nameof(location));
        }

        return new(PageResult.Kind.Redirect, location: Normalize(location), permanent: permanent);
    }

    /// <summary>
    /// Whether <paramref name="url"/> is a path on this site, by ASP.NET Core's <c>IsLocalUrl</c> rules: <c>/path</c> but
    /// not <c>//host</c> or <c>/\host</c>. Check a <c>returnUrl</c> with it and fall back instead of failing.
    /// </summary>
    public static bool IsLocalUrl([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? url)
    {
        if (url is null)
        {
            return false;
        }

        var normalized = Normalize(url);
        return normalized.Length > 0 && normalized[0] == '/'
            && (normalized.Length == 1 || (normalized[1] != '/' && normalized[1] != '\\'));
    }

    // Browsers drop tabs and newlines anywhere in a URL and leading control characters and spaces.
    internal static string Normalize(string location) =>
        location.Replace("\t", "").Replace("\n", "").Replace("\r", "").TrimStart(ControlAndSpace);

    private static readonly char[] ControlAndSpace = [.. Enumerable.Range(0, 0x21).Select(c => (char)c)];

    // No scheme reaches past these: a URL starting with one of them is relative.
    private static readonly char[] SchemeEnd = [':', '/', '?', '#'];

    [GeneratedRegex("^([A-Za-z][A-Za-z0-9+.-]*):")]
    private static partial Regex SchemeRegex();
}

/// <summary>
/// What a page loader, layout loader or page action returns: render a page, or answer with a redirect, a not-found or a
/// status. Create one with <see cref="Page"/>.
/// </summary>
public sealed class PageResult : IResult
{
    internal enum Kind { Props, NotFound, Redirect, Status }

    private readonly Kind _kind;
    private readonly object? _props;
    private readonly string? _pattern;
    private readonly string? _location;
    private readonly bool _permanent;
    private readonly int? _statusCode;
    private readonly IReadOnlyList<PageQuery> _queries;
    private readonly PageMetadata? _metadata;

    internal object? PropsValue => _kind == Kind.Props ? _props : null;

    internal PageResult(
        Kind kind,
        object? props = null,
        string? pattern = null,
        string? location = null,
        bool permanent = false,
        int? statusCode = null,
        IReadOnlyList<PageQuery>? queries = null,
        PageMetadata? metadata = null)
    {
        _kind = kind;
        _props = props;
        _pattern = pattern;
        _location = location;
        _permanent = permanent;
        _statusCode = statusCode;
        _queries = queries ?? [];
        _metadata = metadata;
    }

    internal Kind ResultKind => _kind;
    internal string? Location => _location;
    internal object? ResultProps => _props;
    internal IReadOnlyList<PageQuery> ResultQueries => _queries;
    internal int? StatusCode => _statusCode;

    /// <summary>Render with another status code, e.g. 422 when an action re-renders a form with validation errors.</summary>
    public PageResult WithStatus(int statusCode) => new(_kind, _props, _pattern, _location, _permanent, statusCode, _queries, _metadata);

    /// <summary>Title, description, Open Graph and other tags from C# (Next.js <c>generateMetadata</c>).</summary>
    public PageResult WithMetadata(PageMetadata metadata) =>
        new(_kind, _props, _pattern, _location, _permanent, _statusCode, _queries, metadata);

    /// <summary>
    /// Seeds the page's TanStack Query cache: components calling <c>useQuery</c> with this key render with
    /// <paramref name="data"/> on the server and in the browser, without fetching it first.
    /// </summary>
    public PageResult WithQuery(IReadOnlyList<object?> queryKey, object? data)
    {
        ArgumentNullException.ThrowIfNull(queryKey);
        if (queryKey.Count == 0)
        {
            throw new ArgumentException("A query key needs at least one element.", nameof(queryKey));
        }

        return new(_kind, _props, _pattern, _location, _permanent, _statusCode, [.. _queries, new PageQuery(queryKey, data)], _metadata);
    }

    /// <summary>Writes the result to the response: renders the page (or the not-found, forbidden or unauthorized page), redirects, or sets the status.</summary>
    public Task ExecuteAsync(HttpContext httpContext)
    {
        var renderer = httpContext.RequestServices.GetRequiredService<PageRenderer>();
        return _kind switch
        {
            Kind.Props => renderer.RenderRouteAsync(
                httpContext, _pattern ?? PatternOf(httpContext), _props, _statusCode ?? StatusCodes.Status200OK, _queries, _metadata),
            Kind.NotFound => renderer.RenderNotFoundAsync(httpContext),
            Kind.Redirect => renderer.RedirectAsync(httpContext, _location!, _permanent),
            Kind.Status => renderer.RenderStatusAsync(httpContext, _statusCode!.Value),
            _ => throw new InvalidOperationException($"Unknown page result kind {_kind}."),
        };
    }

    private static string PatternOf(HttpContext httpContext) =>
        httpContext.GetEndpoint()?.Metadata.GetMetadata<PageEndpointMetadata>()?.Pattern
        ?? throw new InvalidOperationException(
            "Page.Props() only works in endpoints mapped with MapPageLoader or MapPageAction. " +
            "Use Page.Render(pattern, props) to render a page from another endpoint.");
}

/// <summary>
/// A page result typed by the page's props: what <see cref="Page.Props{TProps}"/> returns. Every untyped result
/// (<see cref="Page.NotFound"/>, <see cref="Page.Redirect"/>, ...) converts to it, so one loader can return either.
/// </summary>
public sealed class PageResult<TProps> : IResult, ITypedPageResult
{
    internal PageResult(PageResult inner) => Inner = inner;

    internal PageResult Inner { get; }

    PageResult ITypedPageResult.Inner => Inner;

    /// <summary>The props, when this result renders the page; null for a redirect, not-found or status result.</summary>
    public TProps? Props => Inner.PropsValue is TProps props ? props : default;

    /// <inheritdoc cref="PageResult.WithStatus"/>
    public PageResult<TProps> WithStatus(int statusCode) => new(Inner.WithStatus(statusCode));

    /// <inheritdoc cref="PageResult.WithQuery"/>
    public PageResult<TProps> WithQuery(IReadOnlyList<object?> queryKey, object? data) => new(Inner.WithQuery(queryKey, data));

    /// <inheritdoc cref="PageResult.WithMetadata"/>
    public PageResult<TProps> WithMetadata(PageMetadata metadata) => new(Inner.WithMetadata(metadata));

    /// <inheritdoc cref="PageResult.ExecuteAsync"/>
    public Task ExecuteAsync(HttpContext httpContext) => Inner.ExecuteAsync(httpContext);

    /// <summary>Lets a loader return an untyped result, such as <see cref="Page.NotFound"/>, where a typed one is expected.</summary>
    public static implicit operator PageResult<TProps>(PageResult result) => new(result);
}

/// <summary>Lets Netjes see through a typed result, whatever its props type.</summary>
internal interface ITypedPageResult
{
    PageResult Inner { get; }
}

/// <summary>The props of a page rendered without any; serialized as <c>{}</c>.</summary>
public sealed record NoProps
{
    internal static NoProps Instance { get; } = new();
}

internal sealed record PageQuery(IReadOnlyList<object?> Key, object? Data);
