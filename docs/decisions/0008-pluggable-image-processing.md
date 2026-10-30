# 0008 – Image processing is pluggable, SkiaSharp optional

Status: accepted

## Context

`<Image>`, the counterpart of `next/image`, needs an endpoint that resizes and re-encodes images. .NET has no built-in cross-platform image codec, so resizing means a native or managed imaging library, each with its own licence and deployment cost.

## Decision

- The `Netjes` package defines `IImageProcessor` and ships a pass-through default: the endpoint (`/_netjes/image`) validates and caches, but serves the original bytes. `<Image>` works with no extra dependency, but saves no bandwidth.
- `Netjes.Images.SkiaSharp` is an optional package (SkiaSharp, MIT) that resizes without upscaling and encodes WebP when the browser accepts it. It references SkiaSharp's Linux `NoDependencies` native assets, so it runs in the slim ASP.NET Core images. `services.AddNetjesSkiaImages()` registers it.
- The endpoint, not the processor, owns the security rules: configured widths and qualities only, `public/` files through a `PhysicalFileProvider`, remote images from allow-listed hosts only and without following redirects, a size cap on sources, limits on concurrent work, a sandboxing Content-Security-Policy on responses, and SVG off by default.

## Alternatives considered

- **ImageSharp or ImageSharp.Web.** Fully managed and pleasant to use, but under the Six Labors Split License: Apache-2.0 only for open-source use, transitive dependencies or companies under $1M in revenue, and a paid licence otherwise. A framework default must not put that question to every user.
- **NetVips** (MIT; libvips is LGPL-2.1). The fastest, with AVIF, but heavier native binaries and an LGPL component to think about. It would make a good second adapter.
- **Magick.NET** (Apache-2.0). Broad format support, but a large native footprint.
- **Resizing at build time, as a static export would.** It misses remote and user-uploaded images.

## Consequences

- No AVIF output: SkiaSharp does not encode it. The endpoint negotiates whatever formats the processor declares, so an adapter for another library can add it.
- Apps without the SkiaSharp package get correct markup and caching, but full-size downloads.
- Resizing runs on request. Results are output-cached when the app uses output caching, and browser-cached for `Netjes:Images:CacheDuration` (seven days by default).
