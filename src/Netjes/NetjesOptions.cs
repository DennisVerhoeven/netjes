using Microsoft.AspNetCore.Http;

namespace Netjes;

/// <summary>How an app gets its pages: compiled on demand, or from the build.</summary>
public enum NetjesMode
{
    /// <summary>Pages are compiled on demand by the Vite dev server, with hot module replacement.</summary>
    Development,

    /// <summary>Pages come from the <c>npm run build</c> output in the dist directory.</summary>
    Production,
}

/// <summary>Configures Netjes. Binds from the <c>Netjes</c> configuration section, e.g. <c>Netjes__Mode=Production</c>.</summary>
public sealed class NetjesOptions
{
    /// <summary>Directory holding package.json, vite.config and the app directory. Relative to the content root. Default: the content root.</summary>
    public string? Root { get; set; }

    /// <summary>The directory of page files, with the Next.js App Router's <c>app/</c> conventions. Relative to <see cref="Root"/>.</summary>
    public string AppDirectory { get; set; } = "app";

    /// <summary>Where the client build writes its <c>client/</c> and <c>server/</c> output. Relative to <see cref="Root"/>.</summary>
    public string DistDirectory { get; set; } = "dist";

    /// <summary>Static files served as-is in development. Vite copies them into the client build for production.</summary>
    public string PublicDirectory { get; set; } = "public";

    /// <summary>Default: <see cref="NetjesMode.Development"/> in the Development environment, otherwise <see cref="NetjesMode.Production"/>.</summary>
    public NetjesMode? Mode { get; set; }

    /// <summary>The Node.js executable that runs the sidecar processes.</summary>
    public string NodeExecutable { get; set; } = "node";

    /// <summary>Overrides the sidecar script location. Default: the netjes package in development, the copy in dist/server in production.</summary>
    public string? SidecarScript { get; set; }

    /// <summary>The port of the Vite dev server in development.</summary>
    public int ViteDevPort { get; set; } = 5173;

    /// <summary>
    /// How many Node processes render pages in production. One process renders one page at a time on one core, so
    /// CPU-heavy pages under load want about one per core. Development always runs one (it hosts the Vite dev server).
    /// </summary>
    public int SidecarProcesses { get; set; } = 1;

    /// <summary>How long the sidecar may take to render the shell (everything outside unresolved Suspense boundaries).</summary>
    public TimeSpan RenderTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long a streamed document may take in total, Suspense boundaries included.</summary>
    public TimeSpan StreamTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Stream documents: send the shell as soon as it is ready, with <c>loading.tsx</c> fallbacks, and the rest as
    /// it resolves. Default true. Crawlers (<see cref="CrawlerUserAgents"/>) and <c>netjes export</c> always get the
    /// complete document.
    /// </summary>
    public bool StreamDocuments { get; set; } = true;

    /// <summary>
    /// Origins besides the request's own host that may call server actions and post to page actions, e.g.
    /// <c>https://shop.example</c> when a proxy rewrites the Host header. Prefer ForwardedHeaders so the request sees its
    /// public host.
    /// </summary>
    public IReadOnlyList<string> AllowedActionOrigins { get; set; } = [];

    /// <summary>
    /// The largest request body a server action (<c>MapAction</c>) accepts, in bytes, like Next.js's
    /// <c>serverActions.bodySizeLimit</c>; larger ones get 413. Default 1 MB. It only lowers the server's own limit
    /// (Kestrel: 30 MB), never raises it; null leaves the server's limit. One action can set its own, higher or lower,
    /// with <c>[RequestSizeLimit]</c> or <c>[DisableRequestSizeLimit]</c> on its delegate or group, or
    /// <c>.WithMetadata(new RequestSizeLimitAttribute(bytes))</c>; like any such limit, that one replaces the server's.
    /// </summary>
    public long? ActionBodySizeLimit { get; set; } = 1024 * 1024;

    /// <summary>
    /// Words that mark a form field as private: a server action's failure without JavaScript echoes what was typed
    /// back into the page, except fields with one of these words in their name, compared case-insensitively. Words of
    /// five letters or more match anywhere, also across <c>_</c>, <c>-</c> and <c>.</c> (<c>newpassword</c>,
    /// <c>password2</c>, <c>api_key</c>), and so do words written with those separators (<c>auth-code</c> also hides
    /// <c>authCode</c>); shorter ones as a word of the name or at the start or end of one
    /// (<c>card_number</c>, <c>cardnumber</c>, <c>PINCode</c>, <c>newpwd</c>, not <c>spinner</c>). <c>cc</c> and
    /// <c>csc</c> cover the browser's autocomplete names (<c>cc-number</c>, <c>cc-csc</c>).
    /// </summary>
    public IReadOnlyList<string> PrivateFormFields { get; set; } =
    [
        "password", "passwd", "pwd", "pass", "pw", "passcode", "passphrase", "secret", "token", "apikey", "privatekey", "credential",
        "pin", "otp", "totp", "mfa", "recovery", "cvc", "cvv", "csc", "cc", "card", "iban", "ssn", "bsn",
    ];

    /// <summary>
    /// The secret that signs layout segment keys (partial navigation), at least 32 bytes. Default: random per process,
    /// so instances do not share keys and simply load segments in full. Set the same value on every instance to share them.
    /// On an output-cache store the instances share, a cached client payload that reuses a segment is keyed by a
    /// fingerprint of this secret (never the secret), so only instances with the same secret serve each other's.
    /// </summary>
    public string? SegmentKeySecret { get; set; }

    /// <summary>A regular expression for User-Agents that get complete documents instead of streamed ones.</summary>
    public string CrawlerUserAgents { get; set; } =
        @"bot\b|crawl|spider|slurp|facebookexternalhit|embedly|quora link preview|outbrain|pinterest|vkshare|w3c_validator|whatsapp|lighthouse|bingpreview";

    /// <summary>How long the sidecar processes may take to report ready at startup; past it the app does not start.</summary>
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// In production, when no Node sidecar is ready (all of them crashed and are restarting), answer a document request
    /// with a shell .NET writes itself, the payload and the assets, and let the browser render the page instead of
    /// hydrating it. Loaders, authorization and the status run as usual; each such document is logged as a warning and
    /// counted (<c>netjes.sidecar.fallbacks</c>), and is never stored in the output cache. Development (whose sidecar
    /// also runs the Vite dev server) and commands such as export never fall back. Default true. Off, such a request
    /// waits for a restart (up to <see cref="RenderTimeout"/>) and gets the error page if none comes.
    /// </summary>
    public bool ClientRenderFallback { get; set; } = true;

    /// <summary>The <c>lang</c> attribute of the rendered <c>&lt;html&gt;</c> element.</summary>
    public string Lang { get; set; } = "en";

    /// <summary>Include exception messages and stacks in error pages. Default: only in the Development environment.</summary>
    public bool? IncludeErrorDetails { get; set; }

    /// <summary>
    /// A Content-Security-Policy for rendered pages, with <c>{nonce}</c> where the per-request nonce goes, e.g.
    /// <c>script-src 'nonce-{nonce}' 'strict-dynamic'; object-src 'none'; base-uri 'self'</c>. Netjes sets the header on
    /// documents (unless the app already did) and puts the nonce on the scripts and styles it renders. A page with a
    /// nonce is never stored in the output cache (a cached nonce would be reused across visitors); Netjes warns once
    /// when that turns off a page's <c>CachePage</c>. A policy without <c>{nonce}</c> (hash-based, which output-cached
    /// pages need) gets no nonce, so its pages can be stored; no hash covers the scripts that stream <c>Page.Defer</c>
    /// values or reveal Suspense boundaries, though, so pages under one must not stream.
    /// </summary>
    public string? ContentSecurityPolicy { get; set; }

    /// <summary>
    /// Accept Core Web Vitals from <c>&lt;WebVitals /&gt;</c> (<c>netjes/vitals</c>) at <c>/_netjes/vitals</c> and record them
    /// as <c>netjes.web_vitals.*</c> histograms on the Netjes meter. Off by default.
    /// </summary>
    public bool CollectWebVitals { get; set; }

    /// <summary>How long <see cref="IDraftMode.Enable"/> keeps draft mode on.</summary>
    public TimeSpan DraftModeDuration { get; set; } = TimeSpan.FromHours(1);

    /// <summary>The <c>&lt;Image&gt;</c> endpoint: allowed widths, remote hosts, caching.</summary>
    public NetjesImageOptions Images { get; set; } = new();

    /// <summary>nosniff, a referrer policy and same-origin framing on documents; on by default.</summary>
    public NetjesSecurityHeaderOptions SecurityHeaders { get; set; } = new();


    /// <summary>
    /// The generated TypeScript (zod schemas for page props, shared props, endpoints and forms; typed routes), relative
    /// to <see cref="Root"/>. Rewritten on startup in Development when it changed, and by <c>netjes generate</c>. Null turns it off.
    /// </summary>
    public string? TypesFile { get; set; } = "generated/netjes.ts";

    /// <summary>
    /// A one-off tooling command instead of serving requests; the netjes CLI sets it through <c>NETJES_COMMAND</c>.
    /// Internal so configuration binding (appsettings, <c>Netjes__Command__Name</c>, arguments) cannot reach it.
    /// </summary>
    internal NetjesCommandOptions? Command { get; set; }

    /// <summary>
    /// Props available to every page and layout through <c>useSharedProps()</c>, such as the signed-in user.
    /// Prefer <see cref="UseSharedProps{T}"/>, which also records their type for the generated TypeScript. An
    /// output-cached page stores them for every visitor, so they must not depend on cookies or other per-visitor state.
    /// A render that reads <c>Request.Cookies</c> (draft mode's cookie aside) is still stored, and Netjes warns once per
    /// page that the value was stored and will be served to every visitor; reads of the raw <c>Cookie</c> header are not
    /// seen.
    /// </summary>
    public Func<HttpContext, ValueTask<object?>>? SharedProps
    {
        get => _sharedProps;
        set
        {
            _sharedProps = value;
            SharedPropsType = null;
        }
    }

    /// <summary>The type <see cref="UseSharedProps{T}"/> was called with; null when untyped.</summary>
    public Type? SharedPropsType { get; private set; }

    private Func<HttpContext, ValueTask<object?>>? _sharedProps;

    /// <summary>Sets <see cref="SharedProps"/> with a typed factory, so the generated TypeScript knows their shape.</summary>
    public NetjesOptions UseSharedProps<T>(Func<HttpContext, ValueTask<T>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        SharedProps = async http => await factory(http);
        SharedPropsType = typeof(T);
        return this;
    }
}

/// <summary>A tooling command (<c>generate</c>, <c>routes</c>, <c>export</c>): the app runs it on startup and stops.</summary>
internal sealed class NetjesCommandOptions
{
    public string? Name { get; set; }

    /// <summary>Where the command writes its result. Default for <c>generate</c>: <see cref="NetjesOptions.TypesFile"/>.</summary>
    public string? Output { get; set; }

    /// <summary><c>generate</c> only: compare instead of writing, and exit with 1 when the file is out of date.</summary>
    public bool Check { get; set; }
}
