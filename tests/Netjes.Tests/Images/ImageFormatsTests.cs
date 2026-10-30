using Netjes.Images;

namespace Netjes.Tests.Images;

public class ImageFormatsTests
{
    /// <summary>An ISO media file's <c>ftyp</c> box: size, "ftyp", major brand, minor version, compatible brands.</summary>
    private static byte[] Ftyp(string major, params string[] compatible)
    {
        var brands = string.Concat(compatible);
        var size = 16 + brands.Length;
        return [0, 0, 0, (byte)size, .. "ftyp"u8, .. System.Text.Encoding.ASCII.GetBytes(major), 0, 0, 0, 0, .. System.Text.Encoding.ASCII.GetBytes(brands)];
    }

    public static TheoryData<byte[], string?> Files => new()
    {
        { [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13], "image/png" },
        { [0xFF, 0xD8, 0xFF, 0xE0, 0, 16], "image/jpeg" },
        { [.. "GIF87a"u8, 1, 0], "image/gif" },
        { [.. "GIF89a"u8, 1, 0], "image/gif" },
        { [.. "RIFF"u8, 4, 0, 0, 0, .. "WEBPVP8 "u8], "image/webp" },
        { Ftyp("avif", "mif1", "miaf"), "image/avif" },
        { Ftyp("mif1", "avif", "miaf"), "image/avif" },
        { Ftyp("avis", "msf1"), "image/avif" },
        // HEIC and other ISO media files, BMP, ICO, a WAV file, a truncated PNG signature, nothing
        { Ftyp("heic", "mif1", "heic"), null },
        { Ftyp("mp42", "isom"), null },
        { [(byte)'B', (byte)'M', 58, 0, 0, 0], null },
        { [0, 0, 1, 0, 1, 0], null },
        { [.. "RIFF"u8, 4, 0, 0, 0, .. "WAVEfmt "u8], null },
        { [0x89, 0x50, 0x4E, 0x47], null },
        { [0xFF, 0xD8, 0x00, 0xE0], null },
        // "avif" as the minor version, which is a number, not a brand
        { [0, 0, 0, 20, .. "ftyp"u8, .. "heic"u8, .. "avif"u8, .. "mif1"u8], null },
        { [], null },
    };

    [Theory]
    [MemberData(nameof(Files))]
    public void Detect_RecognizesTheServedFormatsByTheirFirstBytes(byte[] content, string? expected)
    {
        Assert.Equal(expected, ImageFormats.Detect(content));
    }
}
