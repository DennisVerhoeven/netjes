# Netjes.Images.SkiaSharp

Image processing for the [Netjes](https://www.nuget.org/packages/Netjes) `<Image>` endpoint, built on SkiaSharp (MIT). Without it the endpoint serves originals (cached); with it, images are resized to the requested width, never larger than the source, and encoded as WebP.

## Install

```bash
dotnet add package Netjes.Images.SkiaSharp --version 0.0.1
```

```csharp
builder.Services.AddNetjes();
builder.Services.AddNetjesSkiaImages();   // before or after AddNetjes()
```

Options:

```csharp
builder.Services.AddNetjesSkiaImages(o =>
{
    o.MaxPixels = 40_000_000;                    // refuse images whose header declares more (default)
    o.MaxConcurrency = Environment.ProcessorCount; // images decoded and encoded at once (default)
});
```

## What it does

- Decodes PNG, JPEG and WebP only, and only bytes that start with the declared format's signature. Output is WebP when the browser accepts it, otherwise JPEG and WebP sources keep their format and PNG stays PNG.
- Applies the EXIF orientation, and decodes large JPEGs at a reduced size.
- GIFs (possibly animated) and AVIFs pass through unchanged. Every other format (BMP and ICO, for example), bytes that do not match their declared format, and images that fail to decode are refused, not passed through.
- Images whose header declares more than `MaxPixels` are refused before decoding (refused like any other image it cannot process), since a small file can claim a huge size.
- A refused image answers 400 from the endpoint when it is a local file and 502 when it is a remote image (the remote host supplied bad bytes).
- Linux builds use `SkiaSharp.NativeAssets.Linux.NoDependencies`, so it runs in the slim ASP.NET Core images.

## Security

This package decodes bytes that visitors and remote hosts control, with native codecs (libjpeg-turbo, libpng, libwebp and others inside SkiaSharp's Skia build). Native image codecs have a history of memory-safety vulnerabilities, so keep the SkiaSharp packages current: update `Netjes.Images.SkiaSharp` and watch SkiaSharp releases and advisories. The pixel limit and concurrency cap bound resource use but do not protect against a codec bug. Restrict `Netjes:Images:RemoteHosts` to hosts you trust and leave `AllowSvg` off.

Use the same version as `Netjes` (0.0.1 above); the two are released together. The [images guide](https://github.com/DennisVerhoeven/netjes/blob/main/docs/guides/images.md) covers `<Image>`, the endpoint's limits and `IImageProcessor`. MIT licensed.
