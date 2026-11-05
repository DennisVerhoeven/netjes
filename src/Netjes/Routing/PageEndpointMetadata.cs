namespace Netjes.Routing;

/// <summary>Marks an endpoint as serving a page. <see cref="Pattern"/> is null for the not-found fallback.</summary>
internal sealed record PageEndpointMetadata(string? Pattern);

/// <summary>Leaves the antiforgery token out of the payload, so the response can be cached and shared.</summary>
internal sealed class OmitAntiforgeryTokenMetadata;

/// <summary>
/// On every endpoint that serves a page: the <c>GET</c> and <c>HEAD</c> endpoint of a page file or of
/// <c>MapPageLoader</c>, and the <c>POST</c> endpoint of <c>MapPageAction</c>. The not-found fallback, server actions
/// and Netjes's other endpoints carry none. Read it from an endpoint's metadata to find an app's pages, as
/// <c>netjes routes</c> does.
/// </summary>
public sealed class PageRouteMetadata
{
    internal PageRouteMetadata(string pattern, bool isLoader)
    {
        Pattern = pattern;
        IsLoader = isLoader;
    }

    /// <summary>
    /// The page's URL as its file names it, such as <c>/blog/[slug]</c>, without the prefix of a route group the
    /// endpoint is mapped in.
    /// </summary>
    public string Pattern { get; }

    /// <summary>True on the endpoint <c>MapPageLoader</c> maps; false on a page file's own endpoint and on a page action's.</summary>
    public bool IsLoader { get; }
}

/// <summary>
/// What <c>CachePage</c> was given, on each endpoint it is put on: a page's, or one that is no page but renders one, such
/// as a minimal API that returns <c>Page.Render</c>. The page cache rules (<c>PageCachePolicy</c>) key such an endpoint
/// even when it is no page endpoint. An endpoint cached by other output caching alone (<c>CacheOutput</c>,
/// <c>[OutputCache]</c>, a base policy) carries none.
/// </summary>
public sealed class CachedPageMetadata
{
    internal CachedPageMetadata()
    {
    }

    /// <summary>How long a rendered page is kept: the <c>duration</c> given to <c>CachePage</c>.</summary>
    public TimeSpan Duration { get; }

    /// <summary>The tags given to <c>CachePage</c>, in their order: what <c>Revalidate</c> and <c>IPageCache</c> evict by.</summary>
    public IReadOnlyList<string> Tags { get; } = [];
}

/// <summary>Patterns that have an explicit loader, so the file-based data source skips them.</summary>
internal sealed class PageLoaderRegistry
{
    private readonly HashSet<string> _patterns = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public int Version { get; private set; }

    public void Register(string pattern)
    {
        lock (_gate)
        {
            if (!_patterns.Add(pattern))
            {
                throw new InvalidOperationException($"A loader for page '{pattern}' is already mapped.");
            }

            Version++;
        }
    }

    public bool Contains(string pattern)
    {
        lock (_gate)
        {
            return _patterns.Contains(pattern);
        }
    }
}

/// <summary>The names of mapped server actions, so a typo'd duplicate fails at startup.</summary>
internal sealed class ServerActionRegistry
{
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public bool Add(string name)
    {
        lock (_gate)
        {
            return _names.Add(name);
        }
    }

    public IReadOnlyList<string> Names
    {
        get
        {
            lock (_gate)
            {
                return [.. _names];
            }
        }
    }
}
