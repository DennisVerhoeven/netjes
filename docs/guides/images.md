# Images

This guide is for app developers. It covers the `<Image>` component and the `/_netjes/image` endpoint behind it, the counterparts of `next/image` and `/_next/image`: where images may come from, the widths and qualities served, the formats, the limits that keep the endpoint safe under load, and how images are resized, through an `IImageProcessor` such as the optional SkiaSharp package ([ADR 0008](../decisions/0008-pluggable-image-processing.md)).

## Showing an image

```tsx
import { Image } from 'netjes';

{/* A fixed size: a 1x and a 2x copy. */}
<Image src="/img/avatar.png" width={64} height={64} alt="Ada Lovelace" />

{/* Responsive: every configured width, and the browser picks by `sizes`. Above the fold, so `priority`. */}
<Image
  src="/hero.png"
  width={1600}
  height={800}
  sizes="(max-width: 64rem) 100vw, 64rem"
  priority
  alt="Gradient in the blog's accent colours"
  className="h-auto w-full"
/>
```

`<Image>` renders an `<img>` with its `width` and `height`, so the page keeps the space and does not shift as it loads. It loads lazily and decodes asynchronously, and its `srcset` points at resized copies from the endpoint:

- Without `sizes`, a fixed-size image gets two candidates: the smallest configured width that covers `width`, as `1x`, and the one that covers twice that, as `2x`.
- With `sizes`, it gets every configured width as a `w` candidate, and the browser picks one by the layout.
- `priority` loads it eagerly with high fetch priority and preloads it from the document head. Use it for the image a visitor sees first.
- `quality` (1–100, default 75) is rounded to the nearest quality the server accepts.
- `unoptimized` serves `src` as it is. SVGs, `data:` URLs and `blob:` URLs are always served as they are.

`src` is a path in `public/` (`/img/hero.png`) or a URL on an allowed remote host. `alt` is required; give `""` for a decorative image. Every other `<img>` attribute, such as `className` or `style`, passes through. Under a [base path](routing.md#base-path), write `src` as if the app were at the root.

The configured widths, qualities and the endpoint's path reach the component with the first document, so the server's configuration is the only one. A static host has no image endpoint, so pages from `netjes export` need `unoptimized` on their images ([Static export](static-export.md#what-cannot-be-exported)).

## The image endpoint

`app.MapPages()` maps `GET /_netjes/image?url=…&w=…&q=…`. It reads the source, hands it to the registered `IImageProcessor` with the width, quality and an output format the browser accepts, and answers with the result:

- `Cache-Control: public, max-age=…` for `CacheDuration` (7 days), an `ETag` (a matching `If-None-Match` gets 304), and `Vary: Accept`.
- `X-Content-Type-Options: nosniff` and `Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; sandbox;`, so an image opened on its own, an SVG above all, runs no script.

If the app uses output caching (`AddOutputCache()` and `UseOutputCache()`), the endpoint's answers are cached: keyed by the width, the quality, the image as it is fetched and the negotiated format, not by the `Host` header. Requests with an `Authorization` header or a signed-in user are cached too, unlike under ASP.NET Core's default policy, since an image from `public/` or an allowed host is the same for everyone. Editors in draft mode share this cache. Serve private images from an endpoint of your own.

Without an image processor the endpoint serves the original bytes, cached as above. `Netjes:Images:Path` moves the endpoint; it is used with one leading slash and none trailing, and other spellings of it get 400.

## Where images come from

**Local images** come from `public/` in development and from the client build in production, where Vite has copied `public/`. A local `url` must be a plain path:

- no query, no fragment, no `.`, `..` or empty segments, and no backslash;
- spelled as its file is stored, case included. On Windows and macOS, `/img/HERO.png` for `hero.png` gets 400, and so do an 8.3 short name, a segment with a trailing dot or space, and an accented name in another Unicode form.

This keeps one cache entry per image and size. The endpoint lists a path's directories the first time the path is asked for, then remembers it (up to 4096 paths, after which it starts over). A file renamed while the app runs is still served under its old spelling, where the file system still opens it that way, until then or until the app restarts.

**Remote images** come only from the hosts in `Netjes:Images:RemoteHosts`:

```json
{
  "Netjes": {
    "Images": {
      "RemoteHosts": ["images.example.com", "*.cdn.example.com"]
    }
  }
}
```

- `images.example.com` allows that host; `*.example.com` allows its subdomains, not `example.com` itself.
- Over https only, unless `RemoteRequireHttps` is `false`, and on the scheme's default port, unless `RemotePorts` lists another.
- Redirects are never followed: a remote host that redirects gets 502.
- The host's address must be public. Loopback, private, link-local (`169.254.169.254`, where cloud metadata services live), shared and unique-local addresses are refused after DNS resolution, so an allowed name pointed at an internal service is never reached. An IPv6 address that carries an IPv4 one (IPv4-mapped, IPv4-compatible, NAT64 on `64:ff9b::/96` or a `/96` inside `64:ff9b:1::/48`, 6to4 on `2002::/16`) is judged by the IPv4 address inside, and Teredo is refused. A NAT64 prefix of your own network, or another RFC 6052 layout, cannot be recognised from the address: where DNS64 makes those, restrict the app's egress at the network as well.
- Fetches never use a proxy: `HTTP_PROXY` and `HTTPS_PROXY` are ignored, because through a proxy the address check would see the proxy. Where all egress must go through a proxy, remote images fail with 502 or 504; allow the app direct egress to its `RemoteHosts`, or serve those images from `public/`.
- The fetch has `RemoteTimeout` (10 seconds) for its headers and body together.

A remote URL is cached as it is fetched: host lower-cased, default port, dot segments and fragment dropped. Its path and query string are sent upstream as written, so `//a.png`, `/A.png` or a trailing `?` may be one image to the host and still be separate entries here. Allow only hosts you trust.

## Widths, qualities and formats

The endpoint serves only the widths in `Netjes:Images:Widths`, by default Next.js's device and image sizes: 16, 32, 48, 64, 96, 128, 256, 384, 640, 750, 828, 1080, 1200, 1920, 2048 and 3840. It accepts `DefaultQuality` (75) and the qualities in `Qualities` (by default only 75). A width or quality it does not serve, or one written with leading zeros (`w=0640`), gets 400. Configured lists replace the defaults:

```json
{
  "Netjes": {
    "Images": {
      "Widths": [320, 640, 1280, 1920],
      "Qualities": [50, 75, 90]
    }
  }
}
```

The endpoint goes by a source's bytes, not its file extension or `Content-Type`: it serves PNG, JPEG, GIF, WebP and AVIF, and SVG when `AllowSvg` is on (it is off by default, because SVG can carry script; SVGs are served unprocessed). Anything else is refused, 400 for a local file and 502 for a remote one, and an image goes out typed as what its bytes are. The output format is the first of the processor's formats that the request's `Accept` header names, or the source's own format.

## Limits

| Option | Default | What it limits |
|---|---|---|
| `MaxSourceBytes` | 20 MB | The largest source read, local (400) or remote (502). |
| `MaxConcurrency` | one per CPU | Images read and processed at once. A request takes its slot before it reads the source. |
| `MaxRemoteFetches` | 16 | Remote images fetched at once, in slots of their own, so a slow host cannot hold up local images. A fetch keeps its slot until its image is processed. |
| `MaxQueue` | 1000 | Requests that may wait for a slot, in each of the two pools. More get 503 with `Retry-After`. |
| `RemoteTimeout` | 10 s | A remote fetch, headers and body together (then 504). |

Peak memory is about `MaxConcurrency` × (2 × `MaxSourceBytes` + 4 bytes × the processor's `MaxPixels`) + `MaxRemoteFetches` × `MaxSourceBytes`, so lower these in a small container. If most images are remote and their hosts are slow, raise `MaxRemoteFetches` rather than `MaxConcurrency`: a fetch waits on the network, while each processing slot can take a CPU and the memory of a decoded image.

| Status | When |
|---|---|
| 400 | A bad or non-canonical query, a width or quality not served, a host or port not allowed, a local file that is not a served image, is spelled otherwise than stored or is too large, or a local image the processor refuses. The body says which. |
| 404 | No such local file. |
| 502 | A remote image that is refused: a redirect, an error status, a non-public address, not an allowed image, too large, or refused by the processor. |
| 503 | The queue is full; `Retry-After` says when to try again. |
| 504 | A remote fetch took longer than `RemoteTimeout`. |

Under `UsePathBase` the endpoint answers with and without the base path, so each image can have two cache entries. Size the output cache (`SizeLimit`) for the images it holds as well as the pages ([Caching](caching.md)).

## Resizing with SkiaSharp

Without a processor, images are served as they are. The `Netjes.Images.SkiaSharp` package resizes them and encodes WebP:

```bash
dotnet add package Netjes.Images.SkiaSharp
```

```csharp
builder.Services.AddNetjes();
builder.Services.AddNetjesSkiaImages(o => o.MaxPixels = 25_000_000);
```

`AddNetjesSkiaImages` registers `SkiaImageProcessor`, before or after `AddNetjes()`. It:

- resizes to the requested width, never larger than the source, and applies the EXIF orientation;
- encodes WebP when the browser accepts it, and the source's own format otherwise;
- decodes large JPEGs at a reduced size;
- refuses an image whose header declares more than `MaxPixels` pixels (default 40 million, about 160 MB decoded) before decoding it, since a file of a few kilobytes can claim 40000×40000;
- decodes `MaxConcurrency` images at once (default one per CPU). `Netjes:Images:MaxConcurrency` limits the whole endpoint, reading included.

It decodes PNG, JPEG and WebP only, and only bytes that start with the declared format's signature, so no other codec SkiaSharp bundles (BMP, ICO and the rest) parses them, even when your own code calls the processor. GIFs (which may be animated) and AVIFs pass through unchanged. An image it cannot decode is refused, not passed through.

SkiaSharp's native codecs parse whatever a file in `public/` or an allowed host holds, so keep SkiaSharp current. If SkiaSharp ships a security update before `Netjes.Images.SkiaSharp` moves to it, add package references to the newer `SkiaSharp` and `SkiaSharp.NativeAssets.Linux.NoDependencies` yourself.

## Writing an image processor

Register your own `IImageProcessor` to resize with another library:

```csharp
public sealed class MyImageProcessor : IImageProcessor
{
    public IReadOnlyList<string> OutputFormats => ["image/webp", "image/jpeg"];

    public ValueTask<ProcessedImage> ProcessAsync(ImageSource source, ImageTransform transform, CancellationToken cancellationToken)
    {
        // source.Content: the bytes; source.ContentType: what they are (image/png, image/jpeg, image/gif, image/webp, image/avif).
        // transform.Width: a configured width; transform.Quality: 1-100; transform.Format: one of OutputFormats, or null for the source's.
        if (TooLarge(source))
        {
            throw new ImageRejectedException("The image has too many pixels.");
        }

        return ValueTask.FromResult(new ProcessedImage(Resize(source, transform), transform.Format ?? source.ContentType));
    }
}

builder.Services.AddSingleton<IImageProcessor, MyImageProcessor>();
```

- `OutputFormats` lists the formats it can encode, preferred first. The endpoint passes one the browser names in `Accept`, or `null` to keep the source's.
- `source.ContentType` is the format the bytes are, whatever the file's extension or the remote `Content-Type` said.
- Do not upscale past the source.
- Throwing `ImageRejectedException` answers with its message: 400 for a local image, 502 for a remote one.

All of the endpoint's options are listed in [Options](../reference/options.md).
