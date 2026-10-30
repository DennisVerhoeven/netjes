using System.Buffers.Binary;

namespace Netjes.Images;

/// <summary>
/// Recognizes the raster formats the endpoint serves by their first bytes, not by a file name or a Content-Type.
/// A decoder picks its codec the same way, so the bytes, not the declared type, decide which one runs.
/// </summary>
internal static class ImageFormats
{
    /// <summary>For messages.</summary>
    public const string Names = "PNG, JPEG, GIF, WebP or AVIF";

    private static ReadOnlySpan<byte> Png => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> Jpeg => [0xFF, 0xD8, 0xFF];

    /// <summary>The image's media type, or null when it is none of the served formats.</summary>
    public static string? Detect(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith(Png))
        {
            return "image/png";
        }

        if (content.StartsWith(Jpeg))
        {
            return "image/jpeg";
        }

        if (content.StartsWith("GIF87a"u8) || content.StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }

        if (content.Length >= 12 && content.StartsWith("RIFF"u8) && content[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        return IsAvif(content) ? "image/avif" : null;
    }

    /// <summary>An ISO media file whose <c>ftyp</c> box names the <c>avif</c> (still) or <c>avis</c> (sequence) brand.</summary>
    private static bool IsAvif(ReadOnlySpan<byte> content)
    {
        if (content.Length < 16 || !content[4..8].SequenceEqual("ftyp"u8))
        {
            return false;
        }

        var box = content[..(int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(content), (uint)content.Length)];
        // The major brand at 8, the minor version at 12, then the compatible brands.
        for (var i = 8; i + 4 <= box.Length; i += i == 8 ? 8 : 4)
        {
            if (box.Slice(i, 4).SequenceEqual("avif"u8) || box.Slice(i, 4).SequenceEqual("avis"u8))
            {
                return true;
            }
        }

        return false;
    }
}
