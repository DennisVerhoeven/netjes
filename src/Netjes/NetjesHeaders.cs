namespace Netjes;

/// <summary>Names of the HTTP headers the client router and the server exchange.</summary>
public static class NetjesHeaders
{
    /// <summary>Sent by the client router to ask for the page payload as JSON instead of a rendered document.</summary>
    public const string Data = "X-Netjes-Data";

    /// <summary>
    /// The server's clock (Unix milliseconds) when a data response was sent, also for output-cached ones. The client
    /// uses it to place the payload's server timestamps on its own clock.
    /// </summary>
    public const string ServerTime = "X-Netjes-Now";

    /// <summary>
    /// Sent by the client router with the keys of the layout segments it already has; their loaders are skipped and
    /// the payload marks them <c>reused</c>.
    /// </summary>
    public const string Segments = "X-Netjes-Segments";

    /// <summary>
    /// <c>invalid</c> on the 400 a client's server action call gets when the server refused its antiforgery token, so the
    /// action did not run. The client router sends only such a call again, once, with a fresh token; any other 400
    /// may come from an action that changed data first.
    /// </summary>
    public const string Antiforgery = "X-Netjes-Antiforgery";

    /// <summary>
    /// <c>changed</c> on the response to a client router's call of a server action or page action that set or deleted
    /// a cookie other than the antiforgery cookie (signing in or out, for one). Script cannot read Set-Cookie, so this
    /// tells the client router to forget the pages it keeps for back and forward, and its prefetched pages, which
    /// showed the app as it was before the change.
    /// </summary>
    public const string Cookies = "X-Netjes-Cookies";
}
