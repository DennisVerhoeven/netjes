using System.Security.Cryptography;

namespace Microsoft.AspNetCore.Http;

/// <summary>Content-Security-Policy support on <see cref="HttpContext"/>: the per-request nonce.</summary>
public static class NetjesHttpContextExtensions
{
    private static readonly object NonceKey = new();

    /// <summary>
    /// This request's Content-Security-Policy nonce, created on first use. Netjes puts it on every script and
    /// stylesheet it renders (and <c>useNonce()</c> returns it), so a policy like
    /// <c>script-src 'nonce-…' 'strict-dynamic'</c> allows exactly those. A document rendered with a nonce is never
    /// stored in the output cache.
    /// </summary>
    public static string GetCspNonce(this HttpContext http)
    {
        if (http.Items.TryGetValue(NonceKey, out var existing) && existing is string nonce)
        {
            return nonce;
        }

        nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        http.Items[NonceKey] = nonce;
        return nonce;
    }

    /// <summary>The nonce if <see cref="GetCspNonce"/> was called for this request, else null.</summary>
    internal static string? PeekCspNonce(this HttpContext http) =>
        http.Items.TryGetValue(NonceKey, out var existing) ? existing as string : null;
}
