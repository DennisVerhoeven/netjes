using System.Collections;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Netjes.Rendering;

/// <summary>
/// The request cookies of a render the output cache means to store, reporting each read to <paramref name="read"/>
/// (the cookie's name, or null for all of them): a stored copy would serve this visitor's cookie-derived values to
/// every visitor.
/// </summary>
internal sealed class CachedRenderCookies(IRequestCookiesFeature inner, Action<string?> read) : IRequestCookiesFeature
{
    private ReadCookies? _cookies;

    public IRequestCookieCollection Cookies
    {
        get => _cookies ??= new ReadCookies(inner.Cookies, read);
        set
        {
            inner.Cookies = value;
            _cookies = null;
        }
    }

    private sealed class ReadCookies(IRequestCookieCollection cookies, Action<string?> read) : IRequestCookieCollection
    {
        public string? this[string key]
        {
            get
            {
                read(key);
                return cookies[key];
            }
        }

        public int Count
        {
            get
            {
                read(null);
                return cookies.Count;
            }
        }

        public ICollection<string> Keys
        {
            get
            {
                read(null);
                return cookies.Keys;
            }
        }

        public bool ContainsKey(string key)
        {
            read(key);
            return cookies.ContainsKey(key);
        }

        public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out string value)
        {
            read(key);
            return cookies.TryGetValue(key, out value);
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            read(null);
            return cookies.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
