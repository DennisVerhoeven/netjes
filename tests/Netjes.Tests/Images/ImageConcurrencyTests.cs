using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Netjes.Images;
using Netjes.Tests.Endpoints;

namespace Netjes.Tests.Images;

/// <summary>The endpoint's slots: how many images are read and processed at once, and how many requests wait.</summary>
public class ImageConcurrencyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private static Task<TestApp> StartAsync(IImageProcessor processor, int maxConcurrency, int maxQueue) =>
        TestApp.StartAsync(
            ["app/page.tsx"],
            configure: o =>
            {
                o.Images.MaxConcurrency = maxConcurrency;
                o.Images.MaxQueue = maxQueue;
            },
            services: s => s.AddSingleton(processor),
            prepareRoot: root =>
            {
                Directory.CreateDirectory(Path.Combine(root, "public", "img"));
                File.WriteAllBytes(Path.Combine(root, "public", "img", "hero.png"), Png);
                File.WriteAllBytes(Path.Combine(root, "public", "img", "other.png"), Png);
            });

    private static Task<HttpResponseMessage> GetAsync(TestApp app, string query) => app.Client.GetAsync($"/_netjes/image?{query}", Ct);

    [Fact]
    public async Task RequestsBeyondTheSlotsAndTheQueue_AreTurnedAway()
    {
        var processor = new HoldingProcessor(holdWidth: 1080);
        await using var app = await StartAsync(processor, maxConcurrency: 1, maxQueue: 1);
        var holding = GetAsync(app, "url=/img/hero.png&w=1080");
        await processor.Holding.WaitAsync(Ct);

        var second = GetAsync(app, "url=/img/hero.png&w=640");
        var third = GetAsync(app, "url=/img/hero.png&w=750");
        var first = await Task.WhenAny(second, third);
        processor.Release();

        var turnedAway = await first;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, turnedAway.StatusCode);
        Assert.NotNull(turnedAway.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.OK, (await holding).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await (first == second ? third : second)).StatusCode);
    }

    [Fact]
    public async Task AWaitingRequest_ReadsItsSourceOnlyOnceItHasASlot()
    {
        // So the slots bound the memory sources take, not only the decoding.
        var processor = new HoldingProcessor(holdWidth: 1080);
        await using var app = await StartAsync(processor, maxConcurrency: 1, maxQueue: 1);
        var holding = GetAsync(app, "url=/img/hero.png&w=1080");
        await processor.Holding.WaitAsync(Ct);

        var second = GetAsync(app, "url=/img/other.png&w=640");
        var third = GetAsync(app, "url=/img/other.png&w=750");
        // Turned away because the queue is full: the other one is waiting.
        var first = await Task.WhenAny(second, third);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await first).StatusCode);
        byte[] changed = [.. Png, 5, 6, 7, 8];
        File.WriteAllBytes(Path.Combine(app.Root, "public", "img", "other.png"), changed);
        processor.Release();

        Assert.Equal(HttpStatusCode.OK, (await (first == second ? third : second)).StatusCode);
        Assert.Equal(changed, processor.Sources.Last().Content.ToArray());
        Assert.Equal(HttpStatusCode.OK, (await holding).StatusCode);
    }

    [Fact]
    public async Task RevalidatingALocalImage_NeedsNoSlot()
    {
        var processor = new HoldingProcessor(holdWidth: 1080);
        await using var app = await StartAsync(processor, maxConcurrency: 1, maxQueue: 0);
        var etag = (await GetAsync(app, "url=/img/other.png&w=640")).Headers.ETag!;
        var holding = GetAsync(app, "url=/img/hero.png&w=1080");
        await processor.Holding.WaitAsync(Ct);

        var request = new HttpRequestMessage(HttpMethod.Get, "/_netjes/image?url=/img/other.png&w=640");
        request.Headers.IfNoneMatch.Add(etag);
        var revalidated = await app.Client.SendAsync(request, Ct);
        processor.Release();

        Assert.Equal(HttpStatusCode.NotModified, revalidated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await holding).StatusCode);
    }

    [Fact]
    public async Task ASlowRemoteHost_DoesNotHoldTheSlotsLocalImagesNeed()
    {
        var upstream = new HeldUpstream();
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configure: o =>
            {
                o.Images.MaxConcurrency = 1;
                o.Images.MaxQueue = 0;
                o.Images.RemoteHosts = ["images.example.com"];
            },
            services: s => s.AddHttpClient(ImageEndpoint.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => upstream),
            prepareRoot: root =>
            {
                Directory.CreateDirectory(Path.Combine(root, "public", "img"));
                File.WriteAllBytes(Path.Combine(root, "public", "img", "hero.png"), Png);
            });
        var remote = GetAsync(app, "url=https%3A%2F%2Fimages.example.com%2Fa.png&w=640");
        await upstream.Requested.WaitAsync(Ct);

        var local = await GetAsync(app, "url=/img/hero.png&w=640");
        var remoteWasWaiting = !remote.IsCompleted;
        upstream.Release();

        Assert.Equal(HttpStatusCode.OK, local.StatusCode);
        Assert.True(remoteWasWaiting);
        Assert.Equal(HttpStatusCode.OK, (await remote).StatusCode);
    }

    [Fact]
    public async Task RemoteFetches_AreNotLimitedToTheProcessingSlots()
    {
        // A fetch waits on the network, not the CPU: with one processing slot, one slow host must not make every other
        // remote image wait behind it.
        var upstream = new HeldUpstream();
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configure: o =>
            {
                o.Images.MaxConcurrency = 1;
                o.Images.RemoteHosts = ["images.example.com"];
            },
            services: s => s.AddHttpClient(ImageEndpoint.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => upstream));
        var first = GetAsync(app, "url=https%3A%2F%2Fimages.example.com%2Fa.png&w=640");
        var second = GetAsync(app, "url=https%3A%2F%2Fimages.example.com%2Fb.png&w=640");

        var bothFetching = await upstream.HasSeenAsync(2, TimeSpan.FromSeconds(5));
        upstream.Release();

        Assert.True(bothFetching, "Both fetches should have reached the host while it held the first.");
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second).StatusCode);
    }

    [Fact]
    public async Task FetchesBeyondMaxRemoteFetchesAndTheQueue_AreTurnedAway_NamingThatLimit()
    {
        var upstream = new HeldUpstream();
        await using var app = await TestApp.StartAsync(
            ["app/page.tsx"],
            configure: o =>
            {
                o.Images.MaxConcurrency = 4;
                o.Images.MaxRemoteFetches = 1;
                o.Images.MaxQueue = 0;
                o.Images.RemoteHosts = ["images.example.com"];
            },
            services: s => s.AddHttpClient(ImageEndpoint.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => upstream));
        var held = GetAsync(app, "url=https%3A%2F%2Fimages.example.com%2Fa.png&w=640");
        await upstream.Requested.WaitAsync(Ct);

        HttpResponseMessage turnedAway;
        try
        {
            // Bounded, so a second fetch that is let through (and held by the host) fails the test instead of hanging it.
            turnedAway = await GetAsync(app, "url=https%3A%2F%2Fimages.example.com%2Fb.png&w=640").WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }
        finally
        {
            upstream.Release();
        }

        Assert.Equal(HttpStatusCode.ServiceUnavailable, turnedAway.StatusCode);
        Assert.NotNull(turnedAway.Headers.RetryAfter);
        Assert.Contains("Netjes:Images:MaxRemoteFetches", await turnedAway.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.OK, (await held).StatusCode);
    }

    /// <summary>Answers once released.</summary>
    private sealed class HeldUpstream : HttpMessageHandler
    {
        private readonly TaskCompletionSource _requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _arrivals = new(0);

        public Task Requested => _requested.Task;

        public void Release() => _release.TrySetResult();

        /// <summary>Whether <paramref name="count"/> requests have arrived within <paramref name="timeout"/>.</summary>
        public async Task<bool> HasSeenAsync(int count, TimeSpan timeout)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(timeout);
            try
            {
                for (var i = 0; i < count; i++)
                {
                    await _arrivals.WaitAsync(deadline.Token);
                }

                return true;
            }
            catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requested.TrySetResult();
            _arrivals.Release();
            await _release.Task.WaitAsync(cancellationToken);
            var content = new ByteArrayContent(Png);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    /// <summary>Holds the first image it is given at one width until released, so a test can fill the endpoint's slots.</summary>
    private sealed class HoldingProcessor(int holdWidth) : IImageProcessor
    {
        private readonly TaskCompletionSource _holding = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Holding => _holding.Task;

        public ConcurrentQueue<ImageSource> Sources { get; } = new();

        public IReadOnlyList<string> OutputFormats => [];

        public void Release() => _release.TrySetResult();

        public async ValueTask<ProcessedImage> ProcessAsync(ImageSource source, ImageTransform transform, CancellationToken cancellationToken)
        {
            Sources.Enqueue(source);
            if (transform.Width == holdWidth && _holding.TrySetResult())
            {
                await _release.Task.WaitAsync(cancellationToken);
            }

            return new ProcessedImage(source.Content, source.ContentType);
        }
    }
}
