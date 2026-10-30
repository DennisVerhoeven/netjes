using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Netjes.Images;

/// <summary>
/// <c>GET /_netjes/image?url=&amp;w=&amp;q=</c>: the <c>&lt;Image&gt;</c> component's optimizer, like Next.js's
/// <c>/_next/image</c>. Serves files from <c>public/</c> (the client build in production) or allow-listed remote
/// hosts, at configured widths only, through the registered <see cref="IImageProcessor"/>.
/// </summary>
internal static class ImageEndpoint
{
    public const string HttpClientName = "Netjes.Images";

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<NetjesContext>().Options.Images;
        endpoints
            .MapGet(options.Path, HandleAsync)
            .WithDisplayName("Netjes image optimizer")
            .WithMetadata(SameForEveryone.Instance)
            .ExcludeFromDescription()
            // Keyed by what changes the output: the canonical query (anything else is a 400), the image as fetched and the
            // negotiated format. The raw Accept header has endless spellings of the same preference, and the Host header
            // (any value, unless AllowedHosts is set) changes nothing.
            .CacheOutput(
                policy => policy
                    .AddPolicy<PublicImageCachePolicy>()
                    .Expire(options.CacheDuration)
                    .SetVaryByHost(false)
                    .SetVaryByQuery("w", "q")
                    .VaryByValue(http => new KeyValuePair<string, string>("url", CacheKeyOf(http.Request.Query["url"].ToString())))
                    .VaryByValue(http => new KeyValuePair<string, string>(
                        "format", NegotiateFormat(http.Request, http.RequestServices.GetRequiredService<IImageProcessor>().OutputFormats) ?? "source")),
                excludeDefaultPolicy: true);
    }

    /// <summary>
    /// Marks the endpoint whose responses depend on nobody's cookies, draft mode's included: editors share its cache.
    /// </summary>
    internal sealed class SameForEveryone
    {
        public static readonly SameForEveryone Instance = new();
    }

    /// <summary>
    /// ASP.NET Core's default output-cache policy without its rule that a request with an <c>Authorization</c> header
    /// or a signed-in user is never cached. An image is the same for everyone (it goes out <c>Cache-Control: public</c>,
    /// and the endpoint sends no credentials upstream), and that rule let any request force a fresh decode.
    /// </summary>
    private sealed class PublicImageCachePolicy : IOutputCachePolicy
    {
        public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellation)
        {
            var method = context.HttpContext.Request.Method;
            context.EnableOutputCaching = true;
            context.AllowCacheLookup = context.AllowCacheStorage = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
            // Concurrent requests for one entry wait for the first instead of each decoding the image.
            context.AllowLocking = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellation) => ValueTask.CompletedTask;

        public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellation)
        {
            var response = context.HttpContext.Response;
            if (response.StatusCode != StatusCodes.Status200OK || !StringValues.IsNullOrEmpty(response.Headers.SetCookie))
            {
                context.AllowCacheStorage = false;
            }

            return ValueTask.CompletedTask;
        }
    }

    private static async Task HandleAsync(
        HttpContext http, NetjesContext context, IImageProcessor processor, IHttpClientFactory clients, ImageSlots slots,
        LocalImageFiles localFiles)
    {
        var options = context.Options.Images;
        if (!string.Equals(http.Request.Path.Value, options.Path, StringComparison.Ordinal))
        {
            await BadRequestAsync(http, $"The image endpoint is {options.Path}, spelled exactly (each spelling would be another cache entry).");
            return;
        }

        var query = http.Request.Query;
        if (string.IsNullOrEmpty(query["url"]))
        {
            await BadRequestAsync(http, "The url parameter is required.");
            return;
        }

        var url = query["url"].ToString();
        var isLocal = IsLocalUrl(url);
        if (isLocal && url.IndexOfAny(['?', '#']) >= 0)
        {
            await BadRequestAsync(http, "A local url must be a plain path: no query or fragment (they would only defeat caching).");
            return;
        }
        if (isLocal && !IsCanonicalPath(url))
        {
            await BadRequestAsync(http, "A local url must be canonical: no '.', '..' or empty segments and no backslashes (each spelling would be another cache entry).");
            return;
        }
        Uri? remote = null;
        if (!isLocal
            && (!Uri.TryCreate(url, UriKind.Absolute, out remote) || remote.Scheme is not ("http" or "https")))
        {
            await BadRequestAsync(http, "The url parameter must be a path on this site (/img/a.png) or an http(s) URL.");
            return;
        }

        if (!int.TryParse(query["w"], NumberStyles.None, CultureInfo.InvariantCulture, out var width)
            || query["w"].ToString() != width.ToString(CultureInfo.InvariantCulture))
        {
            await BadRequestAsync(http, "The w parameter (width) is required, written plainly (640, not 0640).");
            return;
        }

        if (!options.Widths.Contains(width))
        {
            await BadRequestAsync(http, $"w={width} is not a configured width. Allowed: {string.Join(", ", options.Widths)} (Netjes:Images:Widths).");
            return;
        }

        var quality = options.DefaultQuality;
        if (!StringValuesEmpty(query["q"])
            && (!int.TryParse(query["q"], NumberStyles.None, CultureInfo.InvariantCulture, out quality) || quality is < 1 or > 100
                || query["q"].ToString() != quality.ToString(CultureInfo.InvariantCulture)))
        {
            await BadRequestAsync(http, "The q parameter (quality) must be between 1 and 100, written plainly (75, not 075).");
            return;
        }

        if (!StringValuesEmpty(query["q"]) && !QualitiesOf(options).Contains(quality))
        {
            await BadRequestAsync(http, $"q={quality} is not a configured quality. Allowed: {string.Join(", ", QualitiesOf(options))} (Netjes:Images:Qualities).");
            return;
        }

        if (remote is not null && !IsAllowedHost(remote.Host, options.RemoteHosts))
        {
            await BadRequestAsync(http, $"Remote host '{remote.Host}' is not allowed. Add it to Netjes:Images:RemoteHosts.");
            return;
        }

        if (remote is not null && remote.Scheme == Uri.UriSchemeHttp && options.RemoteRequireHttps)
        {
            await BadRequestAsync(http, "Remote images are fetched over https only (Netjes:Images:RemoteRequireHttps).");
            return;
        }

        if (remote is not null && !remote.IsDefaultPort && !options.RemotePorts.Contains(remote.Port))
        {
            await BadRequestAsync(http, $"Remote port {remote.Port} is not allowed. Add it to Netjes:Images:RemotePorts.");
            return;
        }

        var transform = new ImageTransform(width, quality, NegotiateFormat(http.Request, processor.OutputFormats));
        var served = remote is null
            ? await ProcessLocalAsync(http, context, processor, slots, localFiles, url, transform)
            : await ProcessRemoteAsync(http, clients, processor, slots, options, remote, transform);
        if (served is null)
        {
            return;
        }

        // The slot is free again by now: a slow reader must not hold it.
        WriteCacheHeaders(http, options, served.ETag);
        http.Response.ContentType = served.Image.ContentType;
        http.Response.ContentLength = served.Image.Content.Length;
        await http.Response.Body.WriteAsync(served.Image.Content, http.RequestAborted);
    }

    /// <summary>A processed image and its ETag; null from the methods below when they have answered already.</summary>
    private sealed record Served(ProcessedImage Image, string ETag);

    /// <summary>A path on this site: starts with one slash (<c>//host</c> and <c>/\host</c> are other hosts to a browser).</summary>
    private static bool IsLocalUrl(string url) =>
        url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal) && !url.StartsWith("/\\", StringComparison.Ordinal);

    /// <summary>
    /// The url's cache key. A remote URL is keyed as it is requested: lower-case host, no default port, dot segments
    /// resolved, no fragment or user info, so <c>https://CDN.example.com:443/./a.png#x</c> is one entry with
    /// <c>https://cdn.example.com/a.png</c>. Its query stays: it may name another image. A local path is its own key,
    /// since other spellings of it are refused.
    /// </summary>
    internal static string CacheKeyOf(string url) =>
        !IsLocalUrl(url) && Uri.TryCreate(url, UriKind.Absolute, out var remote) && remote.Scheme is "http" or "https"
            ? remote.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped)
            : url;

    private static bool StringValuesEmpty(StringValues value) => string.IsNullOrEmpty(value);

    /// <summary>
    /// A path the file system would not read differently: the one spelling of its file, so it is the one cache key.
    /// <c>/img/./a.png</c>, <c>/x/../img/a.png</c> and <c>/img//a.png</c> all open <c>/img/a.png</c>. Spellings only
    /// some file systems resolve (<c>/img/A.PNG</c> on Windows and macOS) are refused once the file is found
    /// (<see cref="LocalImageFiles.IsStoredSpelling"/>).
    /// </summary>
    internal static bool IsCanonicalPath(string path) =>
        !path.Contains('\\') && path[1..].Split('/').All(segment => segment is not ("" or "." or ".."));

    /// <summary>A file from <c>public/</c> (the client build in production).</summary>
    private static async Task<Served?> ProcessLocalAsync(
        HttpContext http, NetjesContext context, IImageProcessor processor, ImageSlots slots, LocalImageFiles localFiles, string path,
        ImageTransform transform)
    {
        var options = context.Options.Images;
        if (!ContentTypes.TryGetContentType(path, out var contentType) || !contentType.StartsWith("image/", StringComparison.Ordinal))
        {
            await BadRequestAsync(http, $"'{path}' is not an image.");
            return null;
        }

        if (contentType == "image/svg+xml" && !options.AllowSvg)
        {
            await BadRequestAsync(http, "SVG images are not served unless Netjes:Images:AllowSvg is on (SVG can carry script).");
            return null;
        }

        // In production Vite has copied public/ into the client build.
        var root = context.Mode == NetjesMode.Development ? context.PublicDirectory : context.ClientDist;
        if (!Directory.Exists(root))
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return null;
        }

        var files = localFiles.Open(root);
        using var owned = files as IDisposable;
        // Known before the file is read or a slot taken, so a matching If-None-Match costs no work.
        if (await FindAsync(http, localFiles, files, root, path, options) is not { } file
            || IsNotModified(http, options, ETagOf(path, file, transform)))
        {
            return null;
        }

        // Taken before the source is read, so the slots bound the memory sources take too.
        using var slot = await slots.Processing.AcquireAsync(1, http.RequestAborted);
        if (!slot.IsAcquired)
        {
            await BusyAsync(http, "Too many images are waiting to be processed (Netjes:Images:MaxConcurrency, MaxQueue).");
            return null;
        }

        // Looked up again: the file may have changed while this request waited.
        if (await FindAsync(http, localFiles, files, root, path, options) is not { } current)
        {
            return null;
        }

        var content = new byte[current.Length];
        await using (var stream = current.CreateReadStream())
        {
            await stream.ReadExactlyAsync(content, http.RequestAborted);
        }

        // The extension only said image/*; the bytes have to be one of the formats served, and are served as that.
        var detected = contentType == "image/svg+xml" ? contentType : ImageFormats.Detect(content);
        if (detected is null)
        {
            await BadRequestAsync(http, $"'{path}' is not a {ImageFormats.Names} image.");
            return null;
        }

        return await ProcessAsync(
            http, processor, new ImageSource(content, detected), transform, ETagOf(path, current, transform), StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// The file, or null when it is missing (404), spelled other than its directories store it (400) or larger than
    /// <c>MaxSourceBytes</c> (400).
    /// </summary>
    private static async Task<IFileInfo?> FindAsync(
        HttpContext http, LocalImageFiles localFiles, IFileProvider files, string root, string path, NetjesImageOptions options)
    {
        var file = files.GetFileInfo(path);
        if (!file.Exists || file.IsDirectory)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return null;
        }

        if (!localFiles.IsStoredSpelling(root, path))
        {
            await BadRequestAsync(http, $"'{path}' is not spelled as its file is stored, case included (each spelling would be another cache entry).");
            return null;
        }

        if (file.Length > options.MaxSourceBytes)
        {
            await WriteErrorAsync(http, StatusCodes.Status400BadRequest, $"'{path}' is larger than Netjes:Images:MaxSourceBytes.");
            return null;
        }

        return file;
    }

    /// <summary>A local image's version, from its path (canonical: other spellings are refused), size and modification time.</summary>
    private static string ETagOf(string path, IFileInfo file, ImageTransform transform) =>
        Hash(Encoding.UTF8.GetBytes($"{path}|{file.Length}|{file.LastModified.UtcTicks}"), transform);

    /// <summary>An image from an allow-listed host. It carries no version the endpoint can trust, so the ETag is the content's.</summary>
    private static async Task<Served?> ProcessRemoteAsync(
        HttpContext http, IHttpClientFactory clients, IImageProcessor processor, ImageSlots slots, NetjesImageOptions options, Uri remote,
        ImageTransform transform)
    {
        using var fetching = await slots.Fetching.AcquireAsync(1, http.RequestAborted);
        if (!fetching.IsAcquired)
        {
            await BusyAsync(http, "Too many remote images are waiting to be fetched (Netjes:Images:MaxRemoteFetches, MaxQueue).");
            return null;
        }

        if (await ReadRemoteAsync(http, clients, options, remote) is not { } source)
        {
            return null;
        }

        var etag = Hash(source.Content.Span, transform);
        if (IsNotModified(http, options, etag))
        {
            return null;
        }

        using var processing = await slots.Processing.AcquireAsync(1, http.RequestAborted);
        if (!processing.IsAcquired)
        {
            await BusyAsync(http, "Too many images are waiting to be processed (Netjes:Images:MaxConcurrency, MaxQueue).");
            return null;
        }

        // A remote image the processor refuses is the host's answer, like every other bad one: 502, not the client's 400.
        return await ProcessAsync(http, processor, source, transform, etag, StatusCodes.Status502BadGateway);
    }

    /// <summary>
    /// Runs the processor; SVG passes through as it is. An image the processor refuses is answered
    /// <paramref name="rejectedStatus"/>: 400 for a local file, 502 for a remote one.
    /// </summary>
    private static async Task<Served?> ProcessAsync(
        HttpContext http, IImageProcessor processor, ImageSource source, ImageTransform transform, string etag, int rejectedStatus)
    {
        try
        {
            var image = source.ContentType == "image/svg+xml"
                ? new ProcessedImage(source.Content, source.ContentType)
                : await processor.ProcessAsync(source, transform, http.RequestAborted);
            return new Served(image, etag);
        }
        catch (ImageRejectedException rejected)
        {
            await WriteErrorAsync(http, rejectedStatus, rejected.Message);
            return null;
        }
    }

    private static bool IsNotModified(HttpContext http, NetjesImageOptions options, string etag)
    {
        if (!http.Request.Headers.IfNoneMatch.ToString().Contains(etag, StringComparison.Ordinal))
        {
            return false;
        }

        WriteCacheHeaders(http, options, etag);
        http.Response.StatusCode = StatusCodes.Status304NotModified;
        return true;
    }

    private static async Task<ImageSource?> ReadRemoteAsync(HttpContext http, IHttpClientFactory clients, NetjesImageOptions options, Uri remote)
    {
        // One deadline for the headers and the body: a host that sends headers, then drips the body, is given up on.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        deadline.CancelAfter(options.RemoteTimeout);
        try
        {
            return await FetchRemoteAsync(http, clients, options, remote, deadline.Token);
        }
        catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
        {
            await WriteErrorAsync(http, StatusCodes.Status504GatewayTimeout, "The remote image took longer than Netjes:Images:RemoteTimeout.");
            return null;
        }
        catch (HttpRequestException ex) when (FindNonPublic(ex) is { } nonPublic)
        {
            await WriteErrorAsync(http, StatusCodes.Status502BadGateway, nonPublic.Message);
            return null;
        }
        catch (HttpRequestException)
        {
            await WriteErrorAsync(http, StatusCodes.Status502BadGateway, "The remote image could not be fetched.");
            return null;
        }
    }

    private static NonPublicAddressException? FindNonPublic(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            if (exception is NonPublicAddressException nonPublic)
            {
                return nonPublic;
            }
        }

        return null;
    }

    private static async Task<ImageSource?> FetchRemoteAsync(
        HttpContext http, IHttpClientFactory clients, NetjesImageOptions options, Uri remote, CancellationToken cancellationToken)
    {
        var client = clients.CreateClient(HttpClientName);
        using var response = await client.GetAsync(remote, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            await WriteErrorAsync(http, StatusCodes.Status502BadGateway, "The remote image redirected; redirects are not followed.");
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            await WriteErrorAsync(http, StatusCodes.Status502BadGateway, $"The remote image answered {(int)response.StatusCode}.");
            return null;
        }

        // Media types are case-insensitive: image/SVG+XML is SVG.
        var contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
        if (!contentType.StartsWith("image/", StringComparison.Ordinal) || (contentType == "image/svg+xml" && !options.AllowSvg))
        {
            await WriteErrorAsync(http, StatusCodes.Status502BadGateway, $"The remote response is not an allowed image ({contentType}).");
            return null;
        }

        var content = await ReadCappedAsync(response.Content, options.MaxSourceBytes, cancellationToken);
        if (content is null)
        {
            await WriteErrorAsync(http, StatusCodes.Status502BadGateway, "The remote image is larger than Netjes:Images:MaxSourceBytes.");
            return null;
        }

        // Any image/* type gets this far; the bytes have to be one of the formats served, and are served as that.
        var format = contentType == "image/svg+xml" ? contentType : ImageFormats.Detect(content);
        if (format is null)
        {
            await WriteErrorAsync(http, StatusCodes.Status502BadGateway, $"The remote response is not a {ImageFormats.Names} image.");
            return null;
        }

        return new ImageSource(content, format);
    }

    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, long max, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > max)
        {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > max)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>The configured qualities, plus the default one (which a request without q gets).</summary>
    internal static IReadOnlyList<int> QualitiesOf(NetjesImageOptions options) =>
        [.. options.Qualities.Append(options.DefaultQuality).Where(q => q is >= 1 and <= 100).Distinct().Order()];

    /// <summary>
    /// Whether an address is on the public internet: not loopback, private (10/8, 172.16/12, 192.168/16, fc00::/7),
    /// link-local (169.254/16 with cloud metadata services, fe80::/10), shared (100.64/10), unspecified, multicast or
    /// reserved. IPv6 forms that carry an IPv4 address (see <see cref="EmbeddedIPv4"/>) are judged as that address, and
    /// Teredo (2001::/32) is refused: its client address is obscured, and the relay that reaches it is not ours to judge.
    /// </summary>
    internal static bool IsPublicAddress(IPAddress address)
    {
        if (EmbeddedIPv4(address) is { } embedded)
        {
            address = embedded;
        }

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] is 0 or 10 or 127 or >= 224
                || (b[0] == 100 && b[1] is >= 64 and <= 127)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 198 && b[1] is 18 or 19));
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
            && !address.Equals(IPAddress.IPv6Any)
            && !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal && !address.IsIPv6UniqueLocal && !address.IsIPv6Multicast
            && !address.IsIPv6Teredo;
    }

    private static ReadOnlySpan<byte> Nat64Prefix => [0, 0x64, 0xff, 0x9b, 0, 0, 0, 0, 0, 0, 0, 0];
    private static ReadOnlySpan<byte> Nat64LocalPrefix => [0, 0x64, 0xff, 0x9b, 0, 1];
    private static ReadOnlySpan<byte> TranslatedPrefix => [0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xff, 0, 0];

    /// <summary>
    /// The IPv4 address an IPv6 address hands to a translator or tunnel: IPv4-mapped (::ffff:0:0/96), IPv4-translated
    /// (::ffff:0:0:0/96), IPv4-compatible (::/96), NAT64 (64:ff9b::/96, and the local-use 64:ff9b:1::/48 in its usual
    /// /96 form) in the last 32 bits, and 6to4 (2002::/16) in bits 16 to 47. Null for any other address.
    /// </summary>
    private static IPAddress? EmbeddedIPv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return null;
        }

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        var head = bytes[..12];
        if (address.IsIPv4MappedToIPv6 || head.SequenceEqual(TranslatedPrefix) || !head.ContainsAnyExcept((byte)0)
            || head.SequenceEqual(Nat64Prefix) || head.StartsWith(Nat64LocalPrefix))
        {
            return new IPAddress(bytes[12..]);
        }

        return bytes[0] == 0x20 && bytes[1] == 0x02 ? new IPAddress(bytes[2..6]) : null;
    }

    /// <summary>
    /// Connects remote image requests only to public addresses, checked after DNS resolution, so an allow-listed name
    /// pointed (or rebound) at an internal address is never reached.
    /// </summary>
    internal static async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        var allowed = addresses.Where(IsPublicAddress).ToArray();
        if (allowed.Length == 0)
        {
            throw new NonPublicAddressException(context.DnsEndPoint.Host);
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal sealed class NonPublicAddressException(string host)
        : Exception($"The remote host '{host}' does not resolve to a public address; it is not fetched.");

    internal static bool IsAllowedHost(string host, IEnumerable<string> allowed) =>
        allowed.Any(pattern => pattern.StartsWith("*.", StringComparison.Ordinal)
            ? host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase) && host.Length > pattern.Length - 1
            : string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase));

    /// <summary>The first format the processor prefers that the browser names explicitly (<c>image/*</c> says nothing about AVIF).</summary>
    internal static string? NegotiateFormat(HttpRequest request, IReadOnlyList<string> formats)
    {
        if (formats.Count == 0 || !MediaTypeHeaderValue.TryParseList(request.Headers.Accept, out var accepted))
        {
            return null;
        }

        return formats.FirstOrDefault(format => accepted.Any(a =>
            a.MediaType.Equals(format, StringComparison.OrdinalIgnoreCase) && (a.Quality ?? 1) > 0));
    }

    private static string Hash(ReadOnlySpan<byte> content, ImageTransform transform)
    {
        var hash = SHA256.HashData([.. content, .. Encoding.UTF8.GetBytes($"|{transform.Width}|{transform.Quality}|{transform.Format}")]);
        return $"\"{Convert.ToBase64String(hash, 0, 16)}\"";
    }

    private static void WriteCacheHeaders(HttpContext http, NetjesImageOptions options, string etag)
    {
        var headers = http.Response.Headers;
        headers.CacheControl = $"public, max-age={(long)options.CacheDuration.TotalSeconds}";
        headers.Vary = HeaderNames.Accept;
        headers.ETag = etag;
        headers.XContentTypeOptions = "nosniff";
        // An image (an SVG especially) opened directly must not run script or reach anything.
        headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox;";
    }

    private static Task BadRequestAsync(HttpContext http, string message) => WriteErrorAsync(http, StatusCodes.Status400BadRequest, message);

    /// <summary>503: a pool of slots and its queue are full; <paramref name="message"/> names the options that size them.</summary>
    private static Task BusyAsync(HttpContext http, string message)
    {
        http.Response.Headers.RetryAfter = "1";
        return WriteErrorAsync(http, StatusCodes.Status503ServiceUnavailable, message);
    }

    private static Task WriteErrorAsync(HttpContext http, int status, string message)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "text/plain; charset=utf-8";
        return http.Response.WriteAsync(message, http.RequestAborted);
    }
}
