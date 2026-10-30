using System.Threading.RateLimiting;

namespace Netjes.Images;

/// <summary>
/// The image endpoint's slots, each pool with <see cref="NetjesImageOptions.MaxQueue"/> requests waiting, oldest first.
/// </summary>
internal sealed class ImageSlots(NetjesContext context) : IDisposable
{
    /// <summary>Reading and processing an image: <see cref="NetjesImageOptions.MaxConcurrency"/> at once.</summary>
    public ConcurrencyLimiter Processing { get; } = Create(context.Options.Images.MaxConcurrency, context.Options.Images);

    /// <summary>
    /// Fetching a remote image, held until it is processed: <see cref="NetjesImageOptions.MaxRemoteFetches"/> at once.
    /// A slow host fills these, not the ones local images need, and fetched sources waiting to be processed stay bounded too.
    /// </summary>
    public ConcurrencyLimiter Fetching { get; } = Create(context.Options.Images.MaxRemoteFetches, context.Options.Images);

    private static ConcurrencyLimiter Create(int permits, NetjesImageOptions options) => new(new ConcurrencyLimiterOptions
    {
        PermitLimit = Math.Max(1, permits),
        QueueLimit = Math.Max(0, options.MaxQueue),
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
    });

    public void Dispose()
    {
        Processing.Dispose();
        Fetching.Dispose();
    }
}
