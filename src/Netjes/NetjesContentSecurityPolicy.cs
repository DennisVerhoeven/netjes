namespace Netjes;

/// <summary>What a Content-Security-Policy has to allow for the scripts Netjes writes into documents.</summary>
public static class NetjesContentSecurityPolicy
{
    /// <summary>
    /// The <c>script-src</c> sources that allow Netjes's fixed inline scripts by their hashes, for a policy without a
    /// nonce: each <c>'sha256-…'</c> in its quotes, separated by spaces, as in
    /// <c>$"script-src 'self' {NetjesContentSecurityPolicy.ScriptHashes}; object-src 'none'; base-uri 'self'"</c>.
    /// A fixed script has the same text in every document. In this version there is one, the bootstrap that collects
    /// streamed <c>Page.Defer</c> values, and a Netjes version that changes it or adds another changes the value. The
    /// scripts that deliver those values, and React's scripts that reveal Suspense boundaries, differ from render to
    /// render, so no hash covers them. React's script for form actions that are functions is left out too, since its
    /// text follows the React version. The npm package exports the same value as <c>CSP_SCRIPT_HASHES</c> from
    /// <c>netjes</c>.
    /// </summary>
    /// <remarks>
    /// A property rather than a constant: the compiler copies a constant's value into every assembly that uses it, so
    /// an assembly compiled against one version of Netjes would keep that version's hashes when it runs with another.
    /// The property returns the hashes of the Netjes the app runs with.
    /// </remarks>
    public static string ScriptHashes { get; } = "'sha256-FSjVV+FzmXP8KKrLKLdvst/5N/bEWSkde01XzpGKRgs='";
}
