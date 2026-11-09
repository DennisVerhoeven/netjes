namespace Netjes.Routing;

/// <summary>
/// Marks an endpoint Netjes renders as a page: a page file's, a loader's and a page action's, with the page's pattern,
/// and the not-found fallback and server actions, with none. Netjes decides on it which endpoints get its error pages,
/// cache rules and generated types. Apps read <see cref="PageRouteMetadata"/> instead, which only the endpoints that
/// serve a page carry.
/// </summary>
internal sealed record PageEndpointMetadata(string? Pattern);

/// <summary>
/// The page package whose mount an endpoint is mapped through: its file pages, and its loaders, page actions, server
/// actions and the endpoints mapped through its mount. <c>netjes routes</c> names the package by it, and the generated
/// files tell the package's endpoints from the app's by it.
/// </summary>
/// <param name="Package">The package's npm name.</param>
internal sealed record PagePackageEndpointMetadata(string Package);

/// <summary>Leaves the antiforgery token out of the payload, so the response can be cached and shared.</summary>
internal sealed class OmitAntiforgeryTokenMetadata;

/// <summary>
/// The page an endpoint serves, on every endpoint that serves one: the <c>GET</c> and <c>HEAD</c> endpoint of a page
/// file or of <c>MapPageLoader</c>, and the <c>POST</c> endpoint of <c>MapPageAction</c>. No other endpoint carries it:
/// not the not-found fallback, a server action, a minimal API that returns <c>Page.Render</c>, or Netjes's own
/// endpoints such as <c>/_netjes/image</c>. Read it from the endpoint's metadata
/// (<c>endpoint.Metadata.GetMetadata&lt;PageRouteMetadata&gt;()</c>) to find the app's pages, as <c>netjes routes</c> does.
/// </summary>
public sealed class PageRouteMetadata
{
    internal PageRouteMetadata(string pattern, bool isLoader)
    {
        Pattern = pattern;
        IsLoader = isLoader;
    }

    /// <summary>
    /// The page's URL as its file names it, such as <c>/blog/[slug]</c>, and for a page package's page the URL where the
    /// package is mounted, its prefix included (<c>/help/articles/[slug]</c>): the merged route table's pattern, which
    /// <c>netjes routes</c> lists too. It leaves out the prefix of a route group the endpoint is mapped in, which the
    /// endpoint's route pattern includes.
    /// </summary>
    public string Pattern { get; }

    /// <summary>
    /// True on the endpoint <c>MapPageLoader</c> maps. False on a page file's own endpoint, which renders a page that has
    /// no loader, and on a page action's.
    /// </summary>
    public bool IsLoader { get; }
}

/// <summary>
/// The settings <c>CachePage</c> was given, on every endpoint it is put on: pages, and endpoints that are not, such as a
/// minimal API that returns <c>Page.Render</c> or the endpoints of a route group it is put on. An endpoint output-cached
/// without <c>CachePage</c> (<c>CacheOutput</c>, <c>[OutputCache]</c>, a base policy) carries none. A later
/// <c>CacheOutput</c> on the same endpoint replaces the policy <c>CachePage</c> set, and these settings then no longer
/// describe the caching that applies.
/// </summary>
public sealed class CachedPageMetadata
{
    // Netjes's page cache rules (PageCachePolicy) read it as a marker too: they key an endpoint that carries it even when
    // it is no page endpoint.
    internal CachedPageMetadata(TimeSpan duration, IEnumerable<string> tags)
    {
        Duration = duration;
        Tags = [.. tags];
    }

    /// <summary>How long a rendered page is kept: the <c>duration</c> given to <c>CachePage</c>.</summary>
    public TimeSpan Duration { get; }

    /// <summary>The tags given to <c>CachePage</c>, in their order, by which <c>Revalidate</c> and <c>IPageCache</c> evict the page.</summary>
    public IReadOnlyList<string> Tags { get; }
}

/// <summary>
/// The query keys <c>CacheQueryKeys</c> was given, compared as the query parser compares them, without regard to case.
/// Internal for now, unlike <see cref="CachedPageMetadata"/>: it is a page rule of its own, not a setting of <c>CachePage</c>.
/// </summary>
internal sealed record CacheQueryKeysMetadata(IReadOnlySet<string> Keys);

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

/// <summary>
/// The names of mapped server actions and who mapped each, so a duplicate fails at startup. Names are compared ignoring
/// case: routing matches the literal <c>/_netjes/actions/{name}</c> in any case, so two names that differ only in case
/// would both match every call to either.
/// </summary>
internal sealed class ServerActionRegistry
{
    private readonly Dictionary<string, MappedAction> _actions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <param name="Package">The page package whose mount mapped it; null for the app.</param>
    public sealed record MappedAction(string Name, string? Package);

    /// <summary>Adds an action; when one of that name is mapped already, in any case, returns it instead.</summary>
    public MappedAction? Add(string name, string? package)
    {
        lock (_gate)
        {
            if (_actions.TryGetValue(name, out var existing))
            {
                return existing;
            }

            _actions[name] = new MappedAction(name, package);
            return null;
        }
    }

    public IReadOnlyList<string> Names
    {
        get
        {
            lock (_gate)
            {
                return [.. _actions.Values.Select(a => a.Name)];
            }
        }
    }
}
