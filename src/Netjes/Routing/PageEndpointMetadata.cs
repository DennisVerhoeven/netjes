namespace Netjes.Routing;

/// <summary>Marks an endpoint as serving a page. <see cref="Pattern"/> is null for the not-found fallback.</summary>
internal sealed record PageEndpointMetadata(string? Pattern);

/// <summary>Leaves the antiforgery token out of the payload, so the response can be cached and shared.</summary>
internal sealed class OmitAntiforgeryTokenMetadata;

/// <summary>
/// Marks an endpoint <c>CachePage</c> caches: the page cache rules (<c>PageCachePolicy</c>) key it even when it is no
/// page endpoint, such as a minimal API that returns <c>Page.Render</c>.
/// </summary>
internal sealed class CachedPageMetadata;

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
