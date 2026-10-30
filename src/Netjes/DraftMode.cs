using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Hosting;

namespace Netjes;

/// <summary>
/// Draft mode, like Next.js's <c>draftMode()</c>: lets an editor see unpublished content. Enable it from an endpoint
/// you protect (a CMS preview link with a secret, or an authorized route); loaders then read
/// <see cref="IsEnabled"/>. Cached pages are bypassed and draft pages are never cached.
/// </summary>
public interface IDraftMode
{
    /// <summary>Whether this request carries a valid draft mode cookie (or <see cref="Enable"/> was called during it).</summary>
    bool IsEnabled { get; }

    /// <summary>Sets a signed, HttpOnly cookie that lasts <see cref="NetjesOptions.DraftModeDuration"/>.</summary>
    void Enable();

    /// <summary>Deletes the draft mode cookie; <see cref="IsEnabled"/> is false from here on in this request.</summary>
    void Disable();
}

internal sealed class DraftMode(
    IHttpContextAccessor accessor,
    IDataProtectionProvider protection,
    TimeProvider time,
    NetjesContext context,
    IHostEnvironment environment) : IDraftMode
{
    internal const string CookieName = "__netjes_draft";

    private readonly IDataProtector _protector = protection.CreateProtector("Netjes.DraftMode.v1");

    private HttpContext Http => accessor.HttpContext ?? throw new InvalidOperationException("Draft mode is only available during a request.");

    public bool IsEnabled => IsEnabledFor(Http, _protector, time);

    public void Enable()
    {
        var expires = time.GetUtcNow() + context.Options.DraftModeDuration;
        var value = _protector.Protect(expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        var cookie = CookieOptions();
        cookie.HttpOnly = true;
        cookie.Expires = expires;
        cookie.IsEssential = true;
        Http.Response.Cookies.Append(CookieName, value, cookie);
        Http.Items[CookieName] = true;
    }

    public void Disable()
    {
        Http.Response.Cookies.Delete(CookieName, CookieOptions());
        Http.Items[CookieName] = false;
    }

    // Previews usually open in a CMS's iframe on another site, where only SameSite=None cookies are sent, and those
    // must be Secure (true behind a TLS-terminating proxy as well). Development, on plain http://localhost, keeps Lax.
    private CookieOptions CookieOptions()
    {
        var development = environment.IsDevelopment();
        return new CookieOptions
        {
            Path = "/",
            Secure = !development || Http.Request.IsHttps,
            SameSite = development ? SameSiteMode.Lax : SameSiteMode.None,
        };
    }

    internal static bool IsEnabledFor(HttpContext http, IDataProtector protector, TimeProvider time)
    {
        if (http.Items.TryGetValue(CookieName, out var decided) && decided is bool known)
        {
            return known;
        }

        var enabled = false;
        if (http.Request.Cookies.TryGetValue(CookieName, out var cookie))
        {
            try
            {
                enabled = long.TryParse(protector.Unprotect(cookie), NumberStyles.None, CultureInfo.InvariantCulture, out var expires)
                    && time.GetUtcNow().ToUnixTimeSeconds() < expires;
            }
            catch (CryptographicException)
            {
                // Forged, or signed with keys this app no longer has.
            }
        }

        http.Items[CookieName] = enabled;
        return enabled;
    }
}

/// <summary>
/// Output caching while draft mode is on: neither serve nor store a cached page. A base policy, so it covers any output
/// caching; a policy after it (ASP.NET Core's default one) switches lookup and storage back on, so each draft request
/// also gets a cache key of its own, which nothing is ever stored under, and storage is refused again once the response
/// is known, whatever answered: a page, or a redirect a loader returned for a client navigation. A key per request,
/// not one for all drafts: the output cache holds back the requests for a key while one of them renders, so editors
/// previewing a page at the same time would wait for each other. The image endpoint is left alone: its images are the
/// same for editors and visitors, and would otherwise be decoded again for every request an editor makes.
/// </summary>
internal sealed class DraftModeCachePolicy(IDataProtectionProvider protection, TimeProvider time) : IOutputCachePolicy
{
    private const string Key = "netjes-draft";

    private readonly IDataProtector _protector = protection.CreateProtector("Netjes.DraftMode.v1");

    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        // An image is the same in draft mode (the endpoint reads no cookie and sends none upstream).
        if (context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<Images.ImageEndpoint.SameForEveryone>() is null
            && DraftMode.IsEnabledFor(context.HttpContext, _protector, time))
        {
            context.AllowCacheLookup = false;
            context.AllowCacheStorage = false;
            context.CacheVaryByRules.VaryByValues[Key] = Guid.NewGuid().ToString("N");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellation) => ValueTask.CompletedTask;

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        if (context.CacheVaryByRules.VaryByValues.ContainsKey(Key))
        {
            context.AllowCacheStorage = false;
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>Adds <see cref="DraftModeCachePolicy"/> to every output-cached request.</summary>
internal sealed class DraftModeBasePolicySetup(DraftModeCachePolicy policy) : Microsoft.Extensions.Options.IConfigureOptions<OutputCacheOptions>
{
    public void Configure(OutputCacheOptions options) => options.AddBasePolicy(policy);
}
