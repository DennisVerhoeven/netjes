namespace Netjes;

/// <summary>The <c>&lt;Image&gt;</c> component's optimization endpoint. Binds from <c>Netjes:Images</c>.</summary>
public sealed class NetjesImageOptions
{
    internal static readonly int[] DefaultWidths = [16, 32, 48, 64, 96, 128, 256, 384, 640, 750, 828, 1080, 1200, 1920, 2048, 3840];
    internal static readonly int[] DefaultQualities = [75];

    /// <summary>
    /// The widths the endpoint produces and the component builds <c>srcset</c> from. Default: Next.js's device and
    /// image sizes. Configured widths replace the defaults (they start empty so configuration binding does not add to them).
    /// </summary>
    public int[] Widths { get; set; } = [];

    /// <summary>
    /// Hosts remote images may come from: <c>images.example.com</c>, or <c>*.example.com</c> for its subdomains.
    /// Nothing else is fetched, and redirects are never followed.
    /// </summary>
    public string[] RemoteHosts { get; set; } = [];

    /// <summary>Fetch remote images over https only. Default true.</summary>
    public bool RemoteRequireHttps { get; set; } = true;

    /// <summary>Ports remote images may come from besides the scheme's default (443, or 80 for http).</summary>
    public int[] RemotePorts { get; set; } = [];

    /// <summary>How long fetching a remote image may take, headers and body together.</summary>
    public TimeSpan RemoteTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The largest source image read, local or remote.</summary>
    public long MaxSourceBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>
    /// How many images are read and processed at once (default one per CPU). A request takes a slot before it reads
    /// its source, so this also bounds the memory sources take.
    /// </summary>
    public int MaxConcurrency { get; set; } = Environment.ProcessorCount;

    /// <summary>
    /// How many remote images are fetched at once (default 16). These slots are separate from
    /// <see cref="MaxConcurrency"/>'s: a fetch waits on the network, and a slow host must not hold the slots local
    /// images need. A fetch keeps its slot until its image is processed, so fetched sources take at most this many
    /// times <see cref="MaxSourceBytes"/>.
    /// </summary>
    public int MaxRemoteFetches { get; set; } = 16;

    /// <summary>
    /// How many requests may wait for a slot (default 1000), in each of the two pools (<see cref="MaxConcurrency"/> and
    /// <see cref="MaxRemoteFetches"/>). A waiting request holds no image in memory; past this the endpoint answers 503
    /// with <c>Retry-After</c>.
    /// </summary>
    public int MaxQueue { get; set; } = 1000;

    /// <summary>How long browsers (and the output cache) keep an optimized image.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromDays(7);

    /// <summary>The quality used when a request names none (1–100).</summary>
    public int DefaultQuality { get; set; } = 75;

    /// <summary>
    /// The qualities the endpoint accepts besides <see cref="DefaultQuality"/> (Next.js's <c>images.qualities</c>);
    /// the component rounds to the nearest. Default <c>[75]</c>; configured ones replace it.
    /// </summary>
    public int[] Qualities { get; set; } = [];

    /// <summary>Serve SVG files (unprocessed, sandboxed). Off by default because SVG can carry script.</summary>
    public bool AllowSvg { get; set; }

    /// <summary>
    /// The endpoint's path. It is used with one leading slash and none trailing (<c>img-opt/</c> becomes <c>/img-opt</c>),
    /// and other spellings of it are refused.
    /// </summary>
    public string Path { get; set; } = "/_netjes/image";
}
