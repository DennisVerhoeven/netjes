using SkiaSharp;

namespace Netjes.Images.SkiaSharp;

/// <summary>Limits for <see cref="SkiaImageProcessor"/>. Binds nothing itself; pass it to <c>AddNetjesSkiaImages</c>.</summary>
public sealed class SkiaImageOptions
{
    /// <summary>
    /// Images whose header declares more pixels are refused before decoding: a few-kilobyte file can claim
    /// 40000×40000 and would otherwise take gigabytes to decode. Default 40 million (about 160 MB decoded).
    /// </summary>
    public long MaxPixels { get; set; } = 40_000_000;

    /// <summary>How many images are decoded and encoded at once; the rest wait. Default: one per processor.</summary>
    public int MaxConcurrency { get; set; } = Environment.ProcessorCount;
}

/// <summary>
/// Resizes to the requested width (never larger than the source) and encodes WebP when the browser accepts it,
/// otherwise the source's own format. Applies the EXIF orientation, decodes large JPEGs at a reduced size, and
/// refuses images with more than <see cref="SkiaImageOptions.MaxPixels"/>. Decodes PNG, JPEG and WebP only, and only
/// bytes that start like the declared format, so no other codec sees them; refuses one it cannot decode. GIFs (possibly
/// animated) and AVIFs pass through unchanged, other formats are refused.
/// </summary>
public sealed class SkiaImageProcessor(SkiaImageOptions options) : IImageProcessor
{
    private static readonly SKSamplingOptions Sampling = new(SKCubicResampler.Mitchell);

    /// <summary>The formats it decodes, by media type.</summary>
    private static readonly Dictionary<string, SKEncodedImageFormat> Decoded = new()
    {
        ["image/png"] = SKEncodedImageFormat.Png,
        ["image/jpeg"] = SKEncodedImageFormat.Jpeg,
        ["image/webp"] = SKEncodedImageFormat.Webp,
    };

    private readonly SkiaImageOptions _options = options;
    private readonly SemaphoreSlim _slots = new(Math.Max(1, options.MaxConcurrency));

    /// <summary>A processor with the default <see cref="SkiaImageOptions"/>.</summary>
    public SkiaImageProcessor()
        : this(new SkiaImageOptions())
    {
    }

    /// <inheritdoc />
    public IReadOnlyList<string> OutputFormats { get; } = ["image/webp"];

    /// <inheritdoc />
    public async ValueTask<ProcessedImage> ProcessAsync(ImageSource source, ImageTransform transform, CancellationToken cancellationToken)
    {
        // Re-encoding a GIF would drop its animation, and there is no AVIF decoder: the browser gets them as they are.
        if (source.ContentType is "image/gif" or "image/avif")
        {
            return new ProcessedImage(source.Content, source.ContentType);
        }

        // Before any codec sees the bytes: every one it bundles (BMP, ICO, WBMP too) is native code parsing them, and
        // SkiaSharp picks one by the bytes, so they must start the way the declared format does.
        if (!Decoded.ContainsKey(source.ContentType))
        {
            throw new ImageRejectedException(
                $"{source.ContentType} images are not processed: PNG, JPEG and WebP are, and GIF and AVIF pass through.");
        }

        if (!StartsLike(source.ContentType, source.Content.Span))
        {
            throw new ImageRejectedException(
                $"The image could not be decoded as {source.ContentType}: it does not start with that format's signature.");
        }

        await _slots.WaitAsync(cancellationToken);
        try
        {
            return Process(source, transform);
        }
        finally
        {
            _slots.Release();
        }
    }

    private ProcessedImage Process(ImageSource source, ImageTransform transform)
    {
        using var data = SKData.CreateCopy(source.Content.Span);
        using var codec = SKCodec.Create(data)
            ?? throw new ImageRejectedException($"The image could not be decoded as {source.ContentType}.");

        // A second guard: the codec was chosen by the bytes, and only the declared format's may decode them.
        if (Decoded.TryGetValue(source.ContentType, out var declared) && codec.EncodedFormat != declared)
        {
            throw new ImageRejectedException($"The image is {codec.EncodedFormat}, not {source.ContentType}.");
        }

        var info = codec.Info;
        if ((long)info.Width * info.Height > _options.MaxPixels)
        {
            throw new ImageRejectedException(
                $"The image is {info.Width}x{info.Height} pixels, more than the {_options.MaxPixels} allowed (SkiaImageOptions.MaxPixels).");
        }

        var origin = codec.EncodedOrigin;
        var swapsAxes = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var shownWidth = swapsAxes ? info.Height : info.Width;
        var shownHeight = swapsAxes ? info.Width : info.Height;
        var width = Math.Min(transform.Width, shownWidth);
        var height = Math.Max(1, (int)Math.Round(shownHeight * (width / (double)shownWidth)));

        using var decoded = Decode(codec, width / (double)shownWidth)
            ?? throw new ImageRejectedException($"The image could not be decoded as {source.ContentType}.");

        using var oriented = Orient(decoded, origin);
        var upright = oriented ?? decoded;
        using var resized = upright.Width == width && upright.Height == height ? null : upright.Resize(new SKImageInfo(width, height), Sampling);

        var (format, contentType) = transform.Format == "image/webp"
            ? (SKEncodedImageFormat.Webp, "image/webp")
            : source.ContentType switch
            {
                "image/jpeg" => (SKEncodedImageFormat.Jpeg, "image/jpeg"),
                "image/webp" => (SKEncodedImageFormat.Webp, "image/webp"),
                _ => (SKEncodedImageFormat.Png, "image/png"),
            };
        using var image = SKImage.FromBitmap(resized ?? upright);
        using var encoded = image.Encode(format, transform.Quality);
        return new ProcessedImage(encoded.ToArray(), contentType);
    }

    /// <summary>Whether the bytes begin with the signature of a format in <see cref="Decoded"/>: the same ones SkiaSharp checks.</summary>
    private static bool StartsLike(string contentType, ReadOnlySpan<byte> content) => contentType switch
    {
        "image/png" => content.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        "image/jpeg" => content.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]),
        "image/webp" => content.Length >= 12 && content.StartsWith("RIFF"u8) && content[8..12].SequenceEqual("WEBP"u8),
        _ => false,
    };

    /// <summary>
    /// Decodes, at a reduced size when the codec can (JPEG decodes at 1/2, 1/4 or 1/8 directly), never smaller than
    /// the target so the final resize only shrinks.
    /// </summary>
    private static SKBitmap? Decode(SKCodec codec, double scale)
    {
        var size = scale < 1 ? codec.GetScaledDimensions((float)scale) : codec.Info.Size;
        if (size.Width < codec.Info.Width * scale || size.Height < codec.Info.Height * scale)
        {
            size = codec.Info.Size;
        }

        if (size == codec.Info.Size)
        {
            return SKBitmap.Decode(codec);
        }

        var info = new SKImageInfo(size.Width, size.Height, codec.Info.ColorType, codec.Info.AlphaType, codec.Info.ColorSpace);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
        {
            return bitmap;
        }

        bitmap.Dispose();
        return SKBitmap.Decode(codec);
    }

    /// <summary>Turns the decoded pixels the way the EXIF orientation says; null when they are already upright.</summary>
    private static SKBitmap? Orient(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        float w = bitmap.Width, h = bitmap.Height;
        // x' = scaleX*x + skewX*y + transX, y' = skewY*x + scaleY*y + transY
        var (matrix, swaps) = origin switch
        {
            SKEncodedOrigin.TopRight => (new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1), false),
            SKEncodedOrigin.BottomRight => (new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1), false),
            SKEncodedOrigin.BottomLeft => (new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1), false),
            SKEncodedOrigin.LeftTop => (new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1), true),
            SKEncodedOrigin.RightTop => (new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1), true),
            SKEncodedOrigin.RightBottom => (new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1), true),
            SKEncodedOrigin.LeftBottom => (new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1), true),
            _ => (SKMatrix.Identity, false),
        };
        if (matrix == SKMatrix.Identity)
        {
            return null;
        }

        var result = new SKBitmap(swaps ? bitmap.Height : bitmap.Width, swaps ? bitmap.Width : bitmap.Height, bitmap.ColorType, bitmap.AlphaType);
        using var canvas = new SKCanvas(result);
        canvas.SetMatrix(matrix);
        using var image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image, 0, 0, SKSamplingOptions.Default);
        return result;
    }
}
