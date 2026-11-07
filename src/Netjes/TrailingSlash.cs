namespace Netjes;

/// <summary>Whether a page's URL may end in a slash: the values of <see cref="P:Netjes.NetjesOptions.TrailingSlash"/>.</summary>
public enum TrailingSlash
{
    /// <summary>
    /// <c>/about</c> and <c>/about/</c> both answer, as ASP.NET Core routing matches them, and the slash stays as it was
    /// sent: in the route values, the payload's URL and the address bar.
    /// </summary>
    Ignore = 0,

    /// <summary>
    /// A page's URL ends without a slash: <c>/about/</c> answers 308 with <c>Location: /about</c>, and a catch-all's value
    /// loses its last slash (<c>/docs/a/b/</c> goes to <c>/docs/a/b</c>). <c>/</c> stays as it is, at the root and under
    /// a base path.
    /// </summary>
    Never = 1,
}
