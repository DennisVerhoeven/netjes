namespace Netjes.Images;

/// <summary>
/// Resizes and re-encodes images for the <c>&lt;Image&gt;</c> component's endpoint. The default does neither and
/// serves the original; add <c>Netjes.Images.SkiaSharp</c> (or your own) for real optimization.
/// </summary>
public interface IImageProcessor
{
    /// <summary>Formats it can encode, preferred first, e.g. <c>image/webp</c>. The endpoint uses one the browser accepts.</summary>
    IReadOnlyList<string> OutputFormats { get; }

    /// <summary>Resizes and encodes <paramref name="source"/> as <paramref name="transform"/> asks. Throws <see cref="ImageRejectedException"/> to refuse an image.</summary>
    ValueTask<ProcessedImage> ProcessAsync(ImageSource source, ImageTransform transform, CancellationToken cancellationToken);
}

/// <summary>
/// The original image, read from <c>public/</c> or an allowed remote host. <see cref="ContentType"/> is the format its
/// bytes are (<c>image/png</c>, <c>image/jpeg</c>, <c>image/gif</c>, <c>image/webp</c> or <c>image/avif</c>), whatever
/// the file's extension or the remote <c>Content-Type</c> said.
/// </summary>
public sealed record ImageSource(ReadOnlyMemory<byte> Content, string ContentType);

/// <param name="Width">One of the configured widths; processors should not upscale past the source.</param>
/// <param name="Quality">1–100.</param>
/// <param name="Format">One of <see cref="IImageProcessor.OutputFormats"/> the browser accepts, or null to keep the source format.</param>
public sealed record ImageTransform(int Width, int Quality, string? Format);

/// <summary>The image <see cref="IImageProcessor.ProcessAsync"/> produced.</summary>
/// <param name="Content">The bytes to send.</param>
/// <param name="ContentType">Their media type, e.g. <c>image/webp</c>.</param>
public sealed record ProcessedImage(ReadOnlyMemory<byte> Content, string ContentType);

/// <summary>
/// A processor refuses an image (too many pixels, say); the endpoint answers with the message, 400 for a local image and
/// 502 for a remote one.
/// </summary>
public sealed class ImageRejectedException(string message) : Exception(message);

/// <summary>Serves images unchanged (but cached): the zero-dependency default.</summary>
public sealed class PassThroughImageProcessor : IImageProcessor
{
    /// <summary>Empty: it encodes nothing, so the source's format is always kept.</summary>
    public IReadOnlyList<string> OutputFormats => [];

    /// <summary>Returns the source's bytes and content type as they are.</summary>
    public ValueTask<ProcessedImage> ProcessAsync(ImageSource source, ImageTransform transform, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ProcessedImage(source.Content, source.ContentType));
}
