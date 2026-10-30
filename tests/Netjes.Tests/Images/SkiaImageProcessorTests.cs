using Microsoft.Extensions.DependencyInjection;
using Netjes.Images;
using Netjes.Images.SkiaSharp;
using SkiaSharp;

namespace Netjes.Tests.Images;

public class SkiaImageProcessorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Encode(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.CornflowerBlue);
            canvas.DrawCircle(width / 2f, height / 2f, height / 3f, new SKPaint { Color = SKColors.OrangeRed });
        }

        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(format, 90).ToArray();
    }

    private static SKCodec Decode(ProcessedImage image) => SKCodec.Create(new SKMemoryStream(image.Content.ToArray()));

    private readonly SkiaImageProcessor _processor = new();

    [Fact]
    public async Task Process_ResizesToTheWidthKeepingTheAspectRatio()
    {
        var result = await _processor.ProcessAsync(
            new ImageSource(Encode(400, 200, SKEncodedImageFormat.Png), "image/png"), new ImageTransform(100, 75, null), Ct);

        Assert.Equal("image/png", result.ContentType);
        using var codec = Decode(result);
        Assert.Equal(new SKSizeI(100, 50), codec.Info.Size);
    }

    [Fact]
    public async Task Process_EncodesWebPWhenAsked()
    {
        var result = await _processor.ProcessAsync(
            new ImageSource(Encode(400, 200, SKEncodedImageFormat.Jpeg), "image/jpeg"), new ImageTransform(200, 75, "image/webp"), Ct);

        Assert.Equal("image/webp", result.ContentType);
        using var codec = Decode(result);
        Assert.Equal(SKEncodedImageFormat.Webp, codec.EncodedFormat);
        Assert.Equal(200, codec.Info.Width);
    }

    [Fact]
    public async Task Process_NeverUpscales()
    {
        var result = await _processor.ProcessAsync(
            new ImageSource(Encode(300, 150, SKEncodedImageFormat.Png), "image/png"), new ImageTransform(640, 75, null), Ct);

        using var codec = Decode(result);
        Assert.Equal(300, codec.Info.Width);
    }

    [Fact]
    public async Task Process_LowerQualityMakesSmallerJpegs()
    {
        var source = new ImageSource(Encode(800, 400, SKEncodedImageFormat.Jpeg), "image/jpeg");

        var high = await _processor.ProcessAsync(source, new ImageTransform(640, 95, null), Ct);
        var low = await _processor.ProcessAsync(source, new ImageTransform(640, 30, null), Ct);

        Assert.Equal("image/jpeg", low.ContentType);
        Assert.True(low.Content.Length < high.Content.Length);
    }

    [Theory]
    [InlineData("image/gif")]
    [InlineData("image/avif")]
    public async Task Process_LeavesImagesItShouldNotOrCannotReencodeAlone(string contentType)
    {
        // A GIF (re-encoding would drop its animation) or an AVIF (SkiaSharp has no decoder for it).
        byte[] content = contentType == "image/gif" ? Encode(10, 10, SKEncodedImageFormat.Png) : [0, 0, 0, 16, .. "ftypavif"u8, 0, 0, 0, 0];

        var result = await _processor.ProcessAsync(new ImageSource(content, contentType), new ImageTransform(16, 75, "image/webp"), Ct);

        Assert.Equal(contentType, result.ContentType);
        Assert.Equal(content, result.Content.ToArray());
    }

    [Theory]
    [InlineData("webp", "image/png")]
    [InlineData("bmp", "image/jpeg")]
    [InlineData("png", "image/webp")]
    public async Task Process_BytesThatDoNotStartLikeTheirType_AreRejectedBeforeAnyCodecIsChosen(string actual, string declared)
    {
        // SkiaSharp picks the codec by the bytes, so bytes of another format would reach that format's codec (BMP, ICO
        // and the rest are native parsers too) before any check of what it found. Called directly, the processor is
        // not behind the endpoint's own sniffing.
        var content = actual switch
        {
            "webp" => Encode(40, 20, SKEncodedImageFormat.Webp),
            "png" => Encode(40, 20, SKEncodedImageFormat.Png),
            _ => Bmp,
        };

        var ex = await Assert.ThrowsAsync<ImageRejectedException>(async () =>
            await _processor.ProcessAsync(new ImageSource(content, declared), new ImageTransform(16, 75, null), Ct));

        Assert.Contains(declared, ex.Message);
        Assert.Contains("signature", ex.Message);
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8 })]
    public async Task Process_AnImageItCannotDecode_IsRejected_NotPassedThrough(byte[] content)
    {
        // Passed through, the bytes would be served from the app's origin as whatever they claimed to be.
        var ex = await Assert.ThrowsAsync<ImageRejectedException>(async () =>
            await _processor.ProcessAsync(new ImageSource(content, "image/png"), new ImageTransform(16, 75, null), Ct));

        Assert.Contains("decode", ex.Message);
    }

    /// <summary>A 1x1 24-bit BMP, a format SkiaSharp's bundled codecs decode.</summary>
    private static readonly byte[] Bmp =
    [
        (byte)'B', (byte)'M', 58, 0, 0, 0, 0, 0, 0, 0, 54, 0, 0, 0,
        40, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 24, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0x13, 0x0B, 0, 0, 0x13, 0x0B, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 255, 0,
    ];

    [Theory]
    [InlineData("image/bmp")]
    [InlineData("image/x-icon")]
    [InlineData("image/vnd.wap.wbmp")]
    public async Task Process_FormatsItDoesNotHandle_AreRejectedBeforeAnyCodecSeesThem(string contentType)
    {
        var ex = await Assert.ThrowsAsync<ImageRejectedException>(async () =>
            await _processor.ProcessAsync(new ImageSource(Bmp, contentType), new ImageTransform(16, 75, null), Ct));

        Assert.Contains(contentType, ex.Message);
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    /// <summary>A small, valid PNG whose header claims other dimensions: what a decompression bomb looks like.</summary>
    private static byte[] PngClaiming(int width, int height)
    {
        var png = Encode(8, 8, SKEncodedImageFormat.Png);
        // Signature (8), IHDR length (4), "IHDR" (4), then width and height, big-endian.
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), height);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(29), Crc32(png.AsSpan(12, 17)));
        return png;
    }

    /// <summary>A JPEG with the left half red and the right half blue, and an EXIF orientation.</summary>
    private static byte[] JpegWithOrientation(int width, int height, ushort orientation)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Blue);
            canvas.DrawRect(0, 0, width / 2f, height, new SKPaint { Color = SKColors.Red });
        }

        using var image = SKImage.FromBitmap(bitmap);
        var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 95).ToArray();
        byte[] tiff =
        [
            (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8, // big-endian TIFF header, first IFD at 8
            0, 1, // one entry
            0x01, 0x12, 0, 3, 0, 0, 0, 1, (byte)(orientation >> 8), (byte)orientation, 0, 0, // Orientation, SHORT, 1 value
            0, 0, 0, 0, // no next IFD
        ];
        byte[] exif = [.. "Exif\0\0"u8, .. tiff];
        var length = (ushort)(exif.Length + 2);
        byte[] app1 = [0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. exif];
        return [.. jpeg.AsSpan(0, 2), .. app1, .. jpeg.AsSpan(2)];
    }

    private static SKBitmap DecodeBitmap(ProcessedImage image) => SKBitmap.Decode(image.Content.ToArray());

    [Fact]
    public async Task Process_ImageWithMorePixelsThanAllowed_IsRejectedBeforeDecoding()
    {
        var processor = new SkiaImageProcessor(new SkiaImageOptions { MaxPixels = 10_000_000 });

        var ex = await Assert.ThrowsAsync<ImageRejectedException>(async () =>
            await processor.ProcessAsync(new ImageSource(PngClaiming(40_000, 40_000), "image/png"), new ImageTransform(16, 75, null), Ct));

        Assert.Contains("40000x40000", ex.Message);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public async Task Process_AppliesTheExifOrientation(ushort orientation)
    {
        var result = await _processor.ProcessAsync(
            new ImageSource(JpegWithOrientation(400, 200, orientation), "image/jpeg"), new ImageTransform(100, 90, "image/webp"), Ct);

        using var bitmap = DecodeBitmap(result);
        Assert.Equal(new SKSizeI(100, 200), new SKSizeI(bitmap.Width, bitmap.Height));
        // The red left half ends up on top when turned clockwise (6), at the bottom when turned the other way (8).
        var top = bitmap.GetPixel(50, 20);
        var bottom = bitmap.GetPixel(50, 180);
        var (red, blue) = orientation == 6 ? (top, bottom) : (bottom, top);
        Assert.True(red.Red > 200 && red.Blue < 60, $"expected red, got {red}");
        Assert.True(blue.Blue > 200 && blue.Red < 60, $"expected blue, got {blue}");
    }

    [Fact]
    public async Task Process_LargeJpeg_IsDecodedSmallerAndResizedExactly()
    {
        var result = await _processor.ProcessAsync(
            new ImageSource(Encode(4000, 2000, SKEncodedImageFormat.Jpeg), "image/jpeg"), new ImageTransform(256, 75, null), Ct);

        using var codec = Decode(result);
        Assert.Equal(new SKSizeI(256, 128), codec.Info.Size);
    }

    [Fact]
    public async Task Process_CanceledWhileWaitingForASlot_Throws()
    {
        var processor = new SkiaImageProcessor(new SkiaImageOptions { MaxConcurrency = 1 });
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await processor.ProcessAsync(new ImageSource(Encode(40, 20, SKEncodedImageFormat.Png), "image/png"), new ImageTransform(16, 75, null), canceled.Token));
    }

    [Fact]
    public void AddNetjesSkiaImages_ReplacesTheDefaultProcessor_WhicheverOrderItIsCalledIn()
    {
        var before = new ServiceCollection().AddNetjesSkiaImages().AddNetjes().BuildServiceProvider();
        var after = new ServiceCollection().AddNetjes().AddNetjesSkiaImages().BuildServiceProvider();

        Assert.IsType<SkiaImageProcessor>(before.GetRequiredService<IImageProcessor>());
        Assert.IsType<SkiaImageProcessor>(after.GetRequiredService<IImageProcessor>());
    }
}
