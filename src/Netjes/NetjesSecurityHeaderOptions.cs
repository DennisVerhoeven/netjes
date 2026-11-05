namespace Netjes;

/// <summary>
/// Headers Netjes adds, each only when the app has not set that header itself. Every response that passes through
/// <c>UseNetjes</c> gets <c>X-Content-Type-Options: nosniff</c>: documents and client payloads, static files, the app's
/// own endpoints and output-cache answers. The documents Netjes renders also get
/// <c>Referrer-Policy: strict-origin-when-cross-origin</c> and framing headers, same-origin by default:
/// <c>X-Frame-Options: SAMEORIGIN</c>, and <c>frame-ancestors 'self'</c> in
/// <see cref="NetjesOptions.ContentSecurityPolicy"/>. An app that sends its own <c>X-Frame-Options</c> decides about
/// framing itself, and Netjes adds neither framing header. An app with <c>frame-ancestors</c> in a policy that goes out
/// (its own header, or else <see cref="NetjesOptions.ContentSecurityPolicy"/>, which Netjes writes only when the app
/// sent no header) gets the <c>X-Frame-Options</c> it implies, for browsers that know only that header:
/// <c>SAMEORIGIN</c> when every such list holds <c>'self'</c>, <c>*</c> or a scheme source such as <c>https:</c>, and
/// <c>DENY</c> otherwise. Netjes then adds no <c>frame-ancestors</c>.
/// Data responses for the client router get nosniff only. Binds from <c>Netjes:SecurityHeaders</c>.
/// </summary>
public sealed class NetjesSecurityHeaderOptions
{
    /// <summary>
    /// Default true. False: Netjes sets none of these headers, on any response. The image endpoint keeps its own
    /// <c>X-Content-Type-Options: nosniff</c> and sandboxing <c>Content-Security-Policy</c> either way. ASP.NET Core
    /// antiforgery still sends <c>X-Frame-Options: SAMEORIGIN</c> with each new token cookie unless
    /// <c>AntiforgeryOptions.SuppressXFrameOptionsHeader</c> is set.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Sites besides the app's own that may show its pages in a frame while draft mode is on, such as a CMS's preview
    /// pane: <c>https://cms.example</c> (any Content-Security-Policy host source). Draft documents then get
    /// <c>frame-ancestors 'self' https://cms.example</c> and keep <c>X-Frame-Options: SAMEORIGIN</c>, which cannot name
    /// another site: browsers obey <c>frame-ancestors</c> over it, and it still holds if a later middleware rewrites the
    /// policy without <c>frame-ancestors</c>. Ignored when the app sets its own <c>X-Frame-Options</c>
    /// or <c>Content-Security-Policy</c> header, or puts <c>frame-ancestors</c> in
    /// <see cref="NetjesOptions.ContentSecurityPolicy"/>.
    /// </summary>
    public string[] DraftFrameAncestors { get; set; } = [];
}
