using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Netjes.Rendering;

namespace Netjes;

/// <summary>
/// Revalidates output-cached pages (<c>CachePage</c>) from anywhere: a webhook, a background job, a minimal API.
/// The counterpart of Next.js <c>revalidateTag</c> and <c>revalidatePath</c> outside server actions.
/// </summary>
public interface IPageCache
{
    /// <summary>
    /// Evicts every output-cache entry tagged with <paramref name="tag"/>: pages (<c>.CachePage(duration, tags)</c>), and
    /// endpoints of the app (<c>.CacheOutput(p =&gt; p.Tag(tag))</c>).
    /// </summary>
    ValueTask RevalidateTagAsync(string tag, CancellationToken cancellationToken = default);

    /// <summary>
    /// Evicts the cached page at <paramref name="path"/>, both its document and its payload. It never evicts the entry of
    /// an endpoint that is not a page: evict that by a tag.
    /// </summary>
    ValueTask RevalidatePathAsync(string path, CancellationToken cancellationToken = default);
}

internal sealed class PageCache(IServiceProvider services) : IPageCache
{
    // Output caching is optional; without it there is nothing to evict.
    private IOutputCacheStore? Store => services.GetService<IOutputCacheStore>();

    public ValueTask RevalidateTagAsync(string tag, CancellationToken cancellationToken = default) =>
        Store?.EvictByTagAsync(tag, cancellationToken) ?? ValueTask.CompletedTask;

    public ValueTask RevalidatePathAsync(string path, CancellationToken cancellationToken = default) =>
        Store?.EvictByTagAsync(CachePaths.TagFor(path), cancellationToken) ?? ValueTask.CompletedTask;
}
