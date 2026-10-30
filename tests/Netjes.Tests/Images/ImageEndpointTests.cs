using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Netjes.Images;
using Netjes.Tests.Endpoints;

namespace Netjes.Tests.Images;

public class ImageEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
    private static readonly byte[] WebP = [.. "RIFF"u8, 4, 0, 0, 0, .. "WEBPVP8 "u8];

    private static void WritePublicFiles(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "public", "img"));
        File.WriteAllBytes(Path.Combine(root, "public", "img", "hero.png"), Png);
        File.WriteAllText(Path.Combine(root, "public", "img", "logo.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        File.WriteAllText(Path.Combine(root, "public", "notes.txt"), "not an image");
        File.WriteAllText(Path.Combine(root, "public", "img", "text.png"), "not an image either");
        File.WriteAllBytes(Path.Combine(root, "public", "img", "old.bmp"), [(byte)'B', (byte)'M', 58, 0, 0, 0, 0, 0, 0, 0, 54, 0, 0, 0]);
        File.WriteAllBytes(Path.Combine(root, "public", "img", "webp.png"), WebP);
        File.WriteAllText(Path.Combine(root, "secret.png"), "outside public");
    }

    private static Task<TestApp> StartAsync(
        Action<NetjesOptions>? configure = null, Action<IServiceCollection>? services = null, string environment = "Development") =>
        TestApp.StartAsync(["app/page.tsx"], configure: configure, services: services, prepareRoot: WritePublicFiles, environment: environment);

    private static Task<HttpResponseMessage> GetAsync(TestApp app, string query, string? accept = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/_netjes/image?{query}");
        if (accept is not null)
        {
            request.Headers.Accept.ParseAdd(accept);
        }

        return app.Client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task LocalImage_AtAConfiguredWidth_IsServedWithLongCachingAndSafeHeaders()
    {
        await using var app = await StartAsync();

        var response = await GetAsync(app, "url=%2Fimg%2Fhero.png&w=640&q=75");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.True(response.Headers.CacheControl?.Public);
        Assert.Equal(TimeSpan.FromDays(7), response.Headers.CacheControl?.MaxAge);
        Assert.Contains("Accept", response.Headers.Vary);
        Assert.NotNull(response.Headers.ETag);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("sandbox", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task SameETag_AnswersNotModified()
    {
        await using var app = await StartAsync();
        var first = await GetAsync(app, "url=/img/hero.png&w=640");

        var request = new HttpRequestMessage(HttpMethod.Get, "/_netjes/image?url=/img/hero.png&w=640");
        request.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        var second = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    [Theory]
    [InlineData("w=640", "url")]
    [InlineData("url=/img/hero.png", "w")]
    [InlineData("url=/img/hero.png&w=641", "width")]
    [InlineData("url=/img/hero.png&w=640&q=0", "q")]
    [InlineData("url=/img/hero.png&w=640&q=101", "q")]
    [InlineData("url=/notes.txt&w=640", "image")]
    [InlineData("url=/img/logo.svg&w=640", "SVG")]
    [InlineData("url=//evil.example/x.png&w=640", "url")]
    [InlineData("url=ftp://images.example.com/x.png&w=640", "url")]
    [InlineData("url=https://evil.example/x.png&w=640", "not allowed")]
    [InlineData("url=/img/hero.png&w=0640", "w")]
    [InlineData("url=/img/hero.png&w=640&q=075", "q")]
    [InlineData("url=/img/hero.png&w=640&q=50", "quality")]
    [InlineData("url=/img/hero.png%3Fv%3D2&w=640", "query")]
    [InlineData("url=/img/hero.png%23top&w=640", "query")]
    public async Task InvalidRequests_AreRejectedWithTheReason(string query, string reason)
    {
        await using var app = await StartAsync(o => o.Images.RemoteHosts = ["images.example.com"]);

        var response = await GetAsync(app, query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(reason, await response.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("/img/./hero.png")]
    [InlineData("/x/../img/hero.png")]
    [InlineData("/img/a/b/../../hero.png")]
    [InlineData("/img//hero.png")]
    [InlineData("/img\\hero.png")]
    [InlineData("/img/hero.png/")]
    public async Task OtherSpellingsOfALocalPath_AreRefusedWithoutReadingOrProcessing(string url)
    {
        // Each spelling would be its own cache entry and its own decode of the same file.
        var processor = new RecordingProcessor([]);
        await using var app = await StartAsync(services: s => s.AddSingleton<IImageProcessor>(processor));

        var response = await GetAsync(app, $"url={Uri.EscapeDataString(url)}&w=640");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("canonical", await response.Content.ReadAsStringAsync(Ct));
        Assert.Empty(processor.Sources);
    }

    [Theory]
    [InlineData("/img/HERO.png")]
    [InlineData("/IMG/hero.png")]
    [InlineData("/Img/Hero.PNG")]
    [InlineData("/img/HERO~1.PNG")]
    [InlineData("/img./hero.png")]
    [InlineData("/img /hero.png")]
    public async Task SpellingsTheFileSystemResolvesToAnotherStoredName_AreRefusedWithoutReadingOrProcessing(string url)
    {
        // On Windows each of these opens /img/hero.png (on macOS the case variants do), and each would be its own
        // cache entry and ETag: another decode of the same file.
        var processor = new RecordingProcessor([]);
        await using var app = await StartAsync(
            services: s => s.AddSingleton<IImageProcessor>(processor).AddSingleton<LocalImageFiles>(new WindowsLikeFiles()));

        var response = await GetAsync(app, $"url={Uri.EscapeDataString(url)}&w=640");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("stored", await response.Content.ReadAsStringAsync(Ct));
        Assert.Empty(processor.Sources);
    }

    [Theory]
    [InlineData("/img/hero.png")]
    [InlineData("/.well-known/hero.png")]
    public async Task TheStoredSpelling_IsServed_HiddenDirectoriesIncluded(string url)
    {
        await using var app = await StartAsync(services: s => s.AddSingleton<LocalImageFiles>(new WindowsLikeFiles()));
        Directory.CreateDirectory(Path.Combine(app.Root, "public", ".well-known"));
        File.WriteAllBytes(Path.Combine(app.Root, "public", ".well-known", "hero.png"), Png);

        var response = await GetAsync(app, $"url={Uri.EscapeDataString(url)}&w=640");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AStoredSpelling_IsCheckedOnce_NotOnEveryRequest()
    {
        // Listing directories costs more than opening a file, and without the output cache every request would pay it.
        var files = new CountingFiles();
        await using var app = await StartAsync(services: s => s.AddSingleton<LocalImageFiles>(files));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(app, "url=/img/hero.png&w=640")).StatusCode);
        var listed = files.Listings;

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(app, "url=/img/hero.png&w=640")).StatusCode);
        }

        Assert.Equal(2, listed);
        Assert.Equal(listed, files.Listings);
    }

    private sealed class CountingFiles : LocalImageFiles
    {
        private int _listings;

        public int Listings => _listings;

        public override IEnumerable<string> NamesIn(string directory)
        {
            Interlocked.Increment(ref _listings);
            return base.NamesIn(directory);
        }
    }

    /// <summary>
    /// The disk under the root, with names resolved the way Windows resolves them: case-insensitively, without trailing
    /// dots and spaces, and by 8.3 short name (<c>HERO~1.PNG</c> for <c>hero.png</c>). The files keep the names they are
    /// stored under, so this runs the same on Linux.
    /// </summary>
    private sealed class WindowsLikeFiles : LocalImageFiles
    {
        private static readonly Dictionary<string, string> ShortNames = new(StringComparer.OrdinalIgnoreCase) { ["HERO~1.PNG"] = "hero.png" };

        public override IFileProvider Open(string root) => new Resolving(root);

        private sealed class Resolving(string root) : IFileProvider, IDisposable
        {
            private readonly PhysicalFileProvider _disk = new(root);

            public IFileInfo GetFileInfo(string subpath)
            {
                var directory = root;
                var stored = new List<string>();
                foreach (var segment in subpath.TrimStart('/').Split('/'))
                {
                    var name = Directory.Exists(directory)
                        ? Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).FirstOrDefault(name =>
                            string.Equals(name, segment.TrimEnd('.', ' '), StringComparison.OrdinalIgnoreCase)
                            || name == ShortNames.GetValueOrDefault(segment))
                        : null;
                    if (name is null)
                    {
                        return new NotFoundFileInfo(subpath);
                    }

                    stored.Add(name);
                    directory = Path.Join(directory, name);
                }

                return _disk.GetFileInfo("/" + string.Join('/', stored));
            }

            public IDirectoryContents GetDirectoryContents(string subpath) => _disk.GetDirectoryContents(subpath);

            public IChangeToken Watch(string filter) => _disk.Watch(filter);

            public void Dispose() => _disk.Dispose();
        }
    }

    [Fact]
    public async Task MissingFiles_AreNotFound()
    {
        await using var app = await StartAsync();

        var response = await GetAsync(app, "url=/img/missing.png&w=640");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("url=/../secret.png&w=640")]
    [InlineData("url=/img/..%2F..%2Fsecret.png&w=640")]
    public async Task FilesOutsidePublic_AreNeverServed(string query)
    {
        await using var app = await StartAsync();

        var response = await GetAsync(app, query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("outside public", await response.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("/img/text.png")]
    [InlineData("/img/old.bmp")]
    public async Task LocalFiles_WhoseBytesAreNotAServedFormat_AreRefused(string url)
    {
        var processor = new RecordingProcessor([]);
        await using var app = await StartAsync(services: s => s.AddSingleton<IImageProcessor>(processor));

        var response = await GetAsync(app, $"url={url}&w=640");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("WebP", await response.Content.ReadAsStringAsync(Ct));
        Assert.Empty(processor.Sources);
    }

    [Fact]
    public async Task LocalImage_IsServedAsTheFormatItsBytesAre_NotAsItsExtension()
    {
        // A decoder picks its codec by the bytes too, so the processor is told what they are.
        var processor = new RecordingProcessor([]);
        await using var app = await StartAsync(services: s => s.AddSingleton<IImageProcessor>(processor));

        var response = await GetAsync(app, "url=/img/webp.png&w=640");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("image/webp", processor.Sources.Single().ContentType);
    }

    [Fact]
    public async Task Svg_IsServedAsIs_WhenAllowed()
    {
        await using var app = await StartAsync(o => o.Images.AllowSvg = true);

        var response = await GetAsync(app, "url=/img/logo.svg&w=640");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Processor_GetsTheWidthQualityAndAFormatTheBrowserAccepts()
    {
        var processor = new RecordingProcessor(["image/avif", "image/webp"]);
        await using var app = await StartAsync(o => o.Images.Qualities = [60, 75], s => s.AddSingleton<IImageProcessor>(processor));

        var response = await GetAsync(app, "url=/img/hero.png&w=828&q=60", accept: "image/webp,image/*,*/*;q=0.8");

        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
        var transform = Assert.Single(processor.Transforms);
        Assert.Equal(new ImageTransform(828, 60, "image/webp"), transform);
        Assert.Equal("image/png", processor.Sources.Single().ContentType);
    }

    [Fact]
    public async Task Processor_WithoutAnAcceptedFormat_KeepsTheSourceFormat()
    {
        var processor = new RecordingProcessor(["image/avif"]);
        await using var app = await StartAsync(services: s => s.AddSingleton<IImageProcessor>(processor));

        await GetAsync(app, "url=/img/hero.png&w=828", accept: "image/png");

        Assert.Null(Assert.Single(processor.Transforms).Format);
    }

    [Fact]
    public async Task OutputCache_IsKeyedByTheNegotiatedFormat_NotTheRawAcceptHeader()
    {
        var processor = new RecordingProcessor(["image/webp"]);
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            prepareRoot: WritePublicFiles,
            services: s => s.AddOutputCache().AddSingleton<IImageProcessor>(processor),
            configureApp: a => a.UseOutputCache());

        await GetAsync(app, "url=/img/hero.png&w=640", accept: "image/webp,*/*");
        await GetAsync(app, "url=/img/hero.png&w=640", accept: "image/avif,image/webp;q=0.9,image/png;q=0.5");
        var png = await GetAsync(app, "url=/img/hero.png&w=640", accept: "image/png");

        Assert.Equal([ "image/webp", null ], processor.Transforms.Select(t => t.Format));
        Assert.Equal("Accept", string.Join(",", png.Headers.Vary));
    }

    private static Task<TestApp> StartCachedAsync(IImageProcessor processor, Action<WebApplication>? before = null) =>
        TestApp.StartAsync(
            ["app/page.tsx"],
            prepareRoot: WritePublicFiles,
            services: s => s.AddOutputCache().AddSingleton(processor),
            configureApp: a =>
            {
                before?.Invoke(a);
                a.UseOutputCache();
            });

    [Fact]
    public async Task OutputCache_IsNotKeyedByTheHost()
    {
        // The image does not depend on it, and any Host header is accepted unless AllowedHosts says otherwise.
        var processor = new RecordingProcessor([]);
        await using var app = await StartCachedAsync(processor);

        foreach (var host in new[] { "localhost", "a.example", "b.example" })
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/_netjes/image?url=/img/hero.png&w=640");
            request.Headers.Host = host;
            Assert.Equal(HttpStatusCode.OK, (await app.Client.SendAsync(request, Ct)).StatusCode);
        }

        Assert.Single(processor.Sources);
    }

    [Theory]
    [InlineData("authorization header")]
    [InlineData("signed-in user")]
    public async Task OutputCache_KeepsServingImages_ToRequestsWithCredentials(string credentials)
    {
        // ASP.NET Core's default policy skips the cache for both; an image never depends on who asks.
        var processor = new RecordingProcessor([]);
        await using var app = await StartCachedAsync(processor, a => a.Use((http, next) =>
        {
            if (credentials == "signed-in user")
            {
                http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "Test"));
            }

            return next(http);
        }));

        for (var i = 0; i < 3; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/_netjes/image?url=/img/hero.png&w=640");
            if (credentials == "authorization header")
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", $"token-{i}");
            }

            Assert.Equal(HttpStatusCode.OK, (await app.Client.SendAsync(request, Ct)).StatusCode);
        }

        Assert.Single(processor.Sources);
    }

    [Fact]
    public async Task OutputCache_KeepsServingImages_InDraftMode()
    {
        // Draft mode keeps every other response out of the output cache; an image is the same for editors and visitors.
        var processor = new RecordingProcessor([]);
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            prepareRoot: WritePublicFiles,
            map: a => a.MapGet("/api/draft/on", (IDraftMode draft) =>
            {
                draft.Enable();
                return Microsoft.AspNetCore.Http.Results.Ok();
            }),
            services: s => s.AddOutputCache().AddSingleton<IImageProcessor>(processor),
            configureApp: a => a.UseOutputCache());
        var on = await app.Client.GetAsync("/api/draft/on", Ct);
        var draft = on.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(DraftMode.CookieName + "=", StringComparison.Ordinal)).Split(';')[0];

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(app, "url=/img/hero.png&w=640")).StatusCode);
        for (var i = 0; i < 2; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/_netjes/image?url=/img/hero.png&w=640");
            request.Headers.Add("Cookie", draft);
            Assert.Equal(HttpStatusCode.OK, (await app.Client.SendAsync(request, Ct)).StatusCode);
        }

        Assert.Single(processor.Sources);
    }

    [Fact]
    public async Task OutputCache_DoesNotStoreErrors()
    {
        var processor = new RecordingProcessor([]);
        await using var app = await StartCachedAsync(processor);

        var missing = await GetAsync(app, "url=/img/new.png&w=640");
        File.WriteAllBytes(Path.Combine(app.Root, "public", "img", "new.png"), Png);
        var added = await GetAsync(app, "url=/img/new.png&w=640");

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
    }

    [Theory]
    [InlineData("/_netjes/image/")]
    [InlineData("/_NETJES/Image")]
    public async Task OtherSpellingsOfTheEndpointPath_AreRefused(string path)
    {
        // Routing accepts them, and each would be another cache entry (case too, with UseCaseSensitivePaths).
        var processor = new RecordingProcessor([]);
        await using var app = await StartAsync(services: s => s.AddSingleton<IImageProcessor>(processor));

        var response = await app.Client.GetAsync($"{path}?url=/img/hero.png&w=640", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("/_netjes/image", await response.Content.ReadAsStringAsync(Ct));
        Assert.Empty(processor.Sources);
    }

    [Theory]
    [InlineData("img-opt")]
    [InlineData("/img-opt/")]
    public async Task AConfiguredPath_IsServedAndSentToTheClient_WithOneLeadingSlashAndNoTrailingOne(string configured)
    {
        // The endpoint answers one spelling of its path only, so the one it maps, checks and tells <Image> must agree.
        await using var app = await StartAsync(o => o.Images.Path = configured);

        var response = await app.Client.GetAsync("/img-opt?url=/img/hero.png&w=640", Ct);
        await app.GetDocumentAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/img-opt", (string?)app.Renderer.LastPayload["document"]!["images"]!["path"]);
    }

    private sealed class RejectingProcessor : IImageProcessor
    {
        public IReadOnlyList<string> OutputFormats => [];

        public ValueTask<ProcessedImage> ProcessAsync(ImageSource source, ImageTransform transform, CancellationToken cancellationToken) =>
            throw new ImageRejectedException("The image is 40000x40000 pixels.");
    }

    [Fact]
    public async Task ImageTheProcessorRejects_IsABadRequestWithTheReason()
    {
        await using var app = await StartAsync(services: s => s.AddSingleton<IImageProcessor, RejectingProcessor>());

        var response = await GetAsync(app, "url=/img/hero.png&w=640");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("40000x40000", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task ARemoteImageTheProcessorRejects_IsABadGatewayWithTheReason()
    {
        // The image came from upstream, as it was: the request that asked for it was fine.
        var upstream = new FakeUpstream(_ => Image(Png));
        await using var app = await StartAsync(
            o => o.Images.RemoteHosts = ["images.example.com"],
            s => s.AddSingleton<IImageProcessor, RejectingProcessor>()
                .AddHttpClient(ImageEndpoint.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => upstream));

        var response = await GetAsync(app, "url=https://images.example.com/a.png&w=640");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("40000x40000", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task RemoteImage_FromAnAllowedHost_IsFetchedAndServed()
    {
        var upstream = new FakeUpstream(_ => Image(Png));
        await using var app = await StartAsync(
            o => o.Images.RemoteHosts = ["*.example.com"],
            s => s.AddHttpClient(ImageEndpoint.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => upstream));

        var response = await GetAsync(app, "url=https%3A%2F%2Fcdn.example.com%2Fa.png&w=640");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Png, await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal("https://cdn.example.com/a.png", upstream.Requests.Single().ToString());
    }

    [Fact]
    public async Task SpellingsOfOneRemoteUrl_ShareOneCacheEntry()
    {
        var upstream = new FakeUpstream(_ => Image(Png));
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configure: o => o.Images.RemoteHosts = ["cdn.example.com"],
            services: s => s.AddOutputCache().AddHttpClient(ImageEndpoint.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => upstream),
            configureApp: a => a.UseOutputCache());

        foreach (var url in new[]
        {
            "https://cdn.example.com/a.png", "https://CDN.Example.com/a.png", "https://cdn.example.com:443/a.png",
            "https://cdn.example.com/./a.png", "https://cdn.example.com/x/../a.png", "https://cdn.example.com/a.png#top",
        })
        {
            var response = await GetAsync(app, $"url={Uri.EscapeDataString(url)}&w=640");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal("https://cdn.example.com/a.png", upstream.Requests.Single().ToString());
    }

    [Fact]
    public async Task RemoteRedirect_IsNotFollowed()
    {
        var upstream = new FakeUpstream(_ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("http://169.254.169.254/latest/meta-data");
            return redirect;
        });
        await using var app = await StartAsync(
            o => o.Images.RemoteHosts = ["images.example.com"],
            s => s.AddHttpClient(ImageEndpoint.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => upstream));

        var response = await GetAsync(app, "url=https://images.example.com/a.png&w=640");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Single(upstream.Requests);
    }

    [Theory]
    [InlineData("text/html", 100)]
    [InlineData("image/png", 2048)]
    public async Task RemoteResponses_ThatAreNotImagesOrTooLarge_AreRejected(string contentType, int size)
    {
        var upstream = new FakeUpstream(_ => Image(new byte[size], contentType));
        await using var app = await StartAsync(
            o =>
            {
                o.Images.RemoteHosts = ["images.example.com"];
                o.Images.MaxSourceBytes = 1024;
            },
            s => s.AddHttpClient(ImageEndpoint.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => upstream));

        var response = await GetAsync(app, "url=https://images.example.com/a.png&w=640");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Theory]
    [InlineData("image/x-msdownload", "MZ\u0090\u0000 a Windows program")]
    [InlineData("image/png", "<html><body>not an image</body></html>")]
    public async Task RemoteResponses_WhoseBytesAreNoImage_AreRejected(string contentType, string body)
    {
        // Served from this origin they would be a download (or a page) that seems to come from the app.
        var processor = new RecordingProcessor([]);
        var upstream = new FakeUpstream(_ => Image(System.Text.Encoding.Latin1.GetBytes(body), contentType));
        await using var app = await StartAsync(
            o => o.Images.RemoteHosts = ["images.example.com"],
            s => s.AddSingleton<IImageProcessor>(processor)
                .AddHttpClient(ImageEndpoint.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => upstream));

        var response = await GetAsync(app, "url=https://images.example.com/a.png&w=640");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(processor.Sources);
    }

    [Fact]
    public async Task RemoteImage_IsServedAsTheFormatItsBytesAre_NotAsItsDeclaredType()
    {
        var processor = new RecordingProcessor([]);
        var upstream = new FakeUpstream(_ => Image(Png, "image/x-msdownload"));
        await using var app = await StartAsync(
            o => o.Images.RemoteHosts = ["images.example.com"],
            s => s.AddSingleton<IImageProcessor>(processor)
                .AddHttpClient(ImageEndpoint.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => upstream));

        var response = await GetAsync(app, "url=https://images.example.com/a.png&w=640");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("image/png", processor.Sources.Single().ContentType);
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("image/SVG+XML")]
    [InlineData("Image/Svg+Xml")]
    public async Task RemoteSvg_IsRefusedUnlessAllowed_HoweverItsTypeIsSpelled(string contentType)
    {
        var upstream = new FakeUpstream(_ => Image("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray(), contentType));
        await using var app = await StartAsync(
            o => o.Images.RemoteHosts = ["images.example.com"],
            s => s.AddHttpClient(ImageEndpoint.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => upstream));

        var response = await GetAsync(app, "url=https://images.example.com/a.svg&w=640");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Production_ServesImagesFromTheClientBuild()
    {
        await using var app = await TestApp.StartAsync(
            [],
            environment: "Production",
            prepareRoot: root =>
            {
                var client = Path.Combine(root, "dist", "client");
                Directory.CreateDirectory(Path.Combine(client, ".vite"));
                Directory.CreateDirectory(Path.Combine(client, "img"));
                File.WriteAllText(
                    Path.Combine(client, ".vite", "manifest.json"),
                    """{ "node_modules/netjes/dist/client-entry.js": { "file": "assets/entry.js", "isEntry": true } }""");
                File.WriteAllBytes(Path.Combine(client, "img", "hero.png"), Png);
            });

        var response = await GetAsync(app, "url=/img/hero.png&w=640");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DocumentPayload_TellsTheClientTheImagePathWidthsAndQualities()
    {
        await using var app = await StartAsync(o =>
        {
            o.Images.Widths = [320, 640];
            o.Images.Qualities = [90, 50];
        });

        await app.GetDocumentAsync("/");

        var images = app.Renderer.LastPayload["document"]!["images"]!;
        Assert.Equal("/_netjes/image", (string?)images["path"]);
        Assert.Equal("[320,640]", images["widths"]!.ToJsonString());
        // The default quality (75) is always accepted.
        Assert.Equal("[50,75,90]", images["qualities"]!.ToJsonString());
    }

    private static HttpResponseMessage Image(byte[] content, string contentType = "image/png")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return response;
    }

    private sealed class FakeUpstream(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class RecordingProcessor(IReadOnlyList<string> formats) : IImageProcessor
    {
        public List<ImageTransform> Transforms { get; } = [];
        public List<ImageSource> Sources { get; } = [];

        public IReadOnlyList<string> OutputFormats => formats;

        public ValueTask<ProcessedImage> ProcessAsync(ImageSource source, ImageTransform transform, CancellationToken cancellationToken)
        {
            Sources.Add(source);
            Transforms.Add(transform);
            return ValueTask.FromResult(new ProcessedImage(new byte[] { 1 }, transform.Format ?? source.ContentType));
        }
    }
}

public class ImageHostTests
{
    [Theory]
    [InlineData("images.example.com", "images.example.com", true)]
    [InlineData("IMAGES.example.com", "images.example.com", true)]
    [InlineData("cdn.example.com", "*.example.com", true)]
    [InlineData("a.b.example.com", "*.example.com", true)]
    [InlineData("example.com", "*.example.com", false)]
    [InlineData("evilexample.com", "*.example.com", false)]
    [InlineData("example.com.evil.net", "*.example.com", false)]
    [InlineData("images.example.com.evil.net", "images.example.com", false)]
    public void IsAllowedHost_MatchesExactHostsAndSubdomainWildcards(string host, string pattern, bool allowed)
    {
        Assert.Equal(allowed, ImageEndpoint.IsAllowedHost(host, [pattern]));
    }
}
