using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Netjes.Images;
using Netjes.Tests.Endpoints;

namespace Netjes.Tests.Images;

/// <summary>Remote images: only public addresses, https on the default port unless configured, bounded body reads.</summary>
public class ImageSsrfTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2001:4860:4860::8888", true)]
    public void IsPublicAddress_RejectsLoopbackPrivateLinkLocalAndUniqueLocal(string address, bool expected)
    {
        Assert.Equal(expected, ImageEndpoint.IsPublicAddress(IPAddress.Parse(address)));
    }

    [Theory]
    // NAT64, well-known (64:ff9b::/96) and local-use (64:ff9b:1::/48) prefixes: 10.0.0.1, 169.254.169.254, 127.0.0.1
    [InlineData("64:ff9b::a00:1", false)]
    [InlineData("64:ff9b::a9fe:a9fe", false)]
    [InlineData("64:ff9b::127.0.0.1", false)]
    [InlineData("64:ff9b:1::a00:1", false)]
    [InlineData("64:ff9b:1:abcd::c0a8:101", false)]
    // IPv4-compatible (::/96) and IPv4-translated, the SIIT form (::ffff:0:a.b.c.d)
    [InlineData("::0a00:0001", false)]
    [InlineData("::127.0.0.1", false)]
    [InlineData("::ffff:0:0a00:0001", false)]
    [InlineData("::ffff:0:a9fe:a9fe", false)]
    // 6to4 (2002::/16) carries the IPv4 address in its second and third groups
    [InlineData("2002:0a00:0001::", false)]
    [InlineData("2002:7f00:1::", false)]
    [InlineData("2002:a9fe:a9fe::1", false)]
    // Teredo (2001::/32): its client address is obscured and its relay is not ours to judge
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2", false)]
    // The same forms of a public IPv4 address stay public
    [InlineData("64:ff9b::808:808", true)]
    [InlineData("64:ff9b:1::808:808", true)]
    [InlineData("2002:808:808::1", true)]
    [InlineData("::ffff:0:808:808", true)]
    public void IsPublicAddress_JudgesAnEmbeddedIPv4AddressByTheIPv4Rules(string address, bool expected)
    {
        Assert.Equal(expected, ImageEndpoint.IsPublicAddress(IPAddress.Parse(address)));
    }

    private static Task<TestApp> StartAsync(Action<NetjesImageOptions> configure, Action<IServiceCollection>? services = null) =>
        TestApp.StartAsync(["app/page.tsx"], configure: o => configure(o.Images), services: services);

    [Fact]
    public async Task AnAllowedHostThatResolvesToALocalAddress_IsNotFetched()
    {
        // DNS (or a rebinding attack) can point an allow-listed name anywhere; the real handler checks where it connects.
        await using var app = await StartAsync(o => o.RemoteHosts = ["localhost"]);

        var response = await app.Client.GetAsync("/_netjes/image?url=https%3A%2F%2Flocalhost%2Fa.png&w=640", Ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("public", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task AProxy_DoesNotTakeTheImageHostsPlaceInTheAddressCheck()
    {
        // As HTTPS_PROXY would: through a proxy the connect callback sees the proxy's address, never the image host's.
        await using var app = await StartAsync(
            o => o.RemoteHosts = ["localhost"],
            s => s.AddHttpClient(ImageEndpoint.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler((handler, _) => ((SocketsHttpHandler)handler).Proxy = new WebProxy("http://127.0.0.1:9")));

        var response = await app.Client.GetAsync("/_netjes/image?url=https%3A%2F%2Flocalhost%2Fa.png&w=640", Ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("'localhost'", await response.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("http://images.example.com/a.png", "https")]
    [InlineData("https://images.example.com:8443/a.png", "port")]
    public async Task PlainHttpAndOtherPorts_AreRefusedUnlessConfigured(string url, string reason)
    {
        await using var app = await StartAsync(o => o.RemoteHosts = ["images.example.com"]);

        var response = await app.Client.GetAsync($"/_netjes/image?url={Uri.EscapeDataString(url)}&w=640", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(reason, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task ConfiguredPortsAndPlainHttp_AreAllowed()
    {
        var upstream = new StubUpstream(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = Png() });
        await using var app = await StartAsync(
            o =>
            {
                o.RemoteHosts = ["images.example.com"];
                o.RemoteRequireHttps = false;
                o.RemotePorts = [8080];
            },
            s => s.AddHttpClient(ImageEndpoint.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => upstream));

        var response = await app.Client.GetAsync(
            "/_netjes/image?url=" + Uri.EscapeDataString("http://images.example.com:8080/a.png") + "&w=640", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ARemoteBodyThatNeverFinishes_IsGivenUpOn()
    {
        var upstream = new StubUpstream(() =>
        {
            var content = new StreamContent(new StallingStream());
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        await using var app = await StartAsync(
            o =>
            {
                o.RemoteHosts = ["images.example.com"];
                o.RemoteTimeout = TimeSpan.FromMilliseconds(200);
            },
            s => s.AddHttpClient(ImageEndpoint.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => upstream));
        var started = System.Diagnostics.Stopwatch.StartNew();

        var response = await app.Client.GetAsync("/_netjes/image?url=https%3A%2F%2Fimages.example.com%2Fa.png&w=640", Ct);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"took {started.Elapsed}");
    }

    private static ByteArrayContent Png()
    {
        var content = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return content;
    }

    private sealed class StubUpstream(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond());
    }

    /// <summary>Headers arrive, then the body sends nothing until the read is canceled.</summary>
    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
