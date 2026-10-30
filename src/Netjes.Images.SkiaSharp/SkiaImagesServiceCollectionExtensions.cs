using Microsoft.Extensions.DependencyInjection.Extensions;
using Netjes.Images;
using Netjes.Images.SkiaSharp;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the SkiaSharp image processor.</summary>
public static class SkiaImagesServiceCollectionExtensions
{
    /// <summary>
    /// Resizes and WebP-encodes the <c>&lt;Image&gt;</c> endpoint's images with SkiaSharp. Call before or after
    /// <c>AddNetjes()</c>. <paramref name="configure"/> sets the pixel limit and how many images are processed at once.
    /// </summary>
    public static IServiceCollection AddNetjesSkiaImages(this IServiceCollection services, Action<SkiaImageOptions>? configure = null)
    {
        var options = new SkiaImageOptions();
        configure?.Invoke(options);
        services.Replace(ServiceDescriptor.Singleton<IImageProcessor>(new SkiaImageProcessor(options)));
        return services;
    }
}
