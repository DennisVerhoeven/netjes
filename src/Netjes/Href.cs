using System.Diagnostics.CodeAnalysis;

namespace Netjes;

/// <summary>
/// A link for the browser, in the props a C# loader sends: the URL of one of the app's pages, root-relative and without
/// the base path (<c>/blog/hello</c>, <c>/docs/caching#keys</c>), a query or fragment of the page on screen
/// (<c>?page=2</c>, <c>#top</c>), or an <c>http:</c>, <c>https:</c>, <c>mailto:</c> or <c>tel:</c> URL.
/// </summary>
/// <remarks>
/// C# checks the shape of the URL only. Whether a root-relative URL is one of the app's pages is not known here: a link to
/// an endpoint or a file, such as <c>/llms.txt</c>, has the shape too. There is no conversion from a string and no
/// <c>TryParse</c>, so minimal APIs and form binding never read a request value into it: check a URL from the request
/// with <see cref="Page.IsLocalUrl"/> instead.
/// </remarks>
public sealed class Href : IEquatable<Href>
{
    // "http:" and "https:" in any case, as browsers read a scheme.
    private static readonly string[] Schemes = ["http:", "https:", "mailto:", "tel:"];

    /// <summary>The link <paramref name="value"/>, which must have the shape of one.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> starts with none of <c>/</c>, <c>?</c>, <c>#</c>, <c>http:</c>, <c>https:</c>, <c>mailto:</c>
    /// and <c>tel:</c>; starts with <c>//</c> or <c>/\</c>, which browsers read as another host; holds an ASCII control
    /// character, such as a tab or a newline, which browsers drop; or starts or ends with a space.
    /// </exception>
    public Href(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!HasTheShape(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a link: start it with '/' (but not '//' or '/\\'), '?', '#', 'http:', 'https:', 'mailto:' or 'tel:', " +
                "and leave out control characters and spaces at either end.",
                nameof(value));
        }

        Value = value;
    }

    /// <summary>The URL, as given.</summary>
    public string Value { get; }

    /// <summary>
    /// The link <paramref name="value"/> when it has the shape the constructor asks for; false, with
    /// <paramref name="href"/> null, otherwise and for null.
    /// </summary>
    public static bool TryCreate([NotNullWhen(true)] string? value, [NotNullWhen(true)] out Href? href)
    {
        href = value is not null && HasTheShape(value) ? new Href(value) : null;
        return href is not null;
    }

    /// <summary>The URL, as given.</summary>
    public override string ToString() => Value;

    /// <summary>Whether <paramref name="other"/> holds the same URL, compared character by character.</summary>
    public bool Equals(Href? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Href other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public static bool operator ==(Href? left, Href? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(Href? left, Href? right) => !(left == right);

    /// <summary>The URL, for code that takes a string.</summary>
    [return: NotNullIfNotNull(nameof(href))]
    public static implicit operator string?(Href? href) => href?.Value;

    // The same rule as isRouteHref in the npm package, without its check of the app's pages.
    private static bool HasTheShape(string value)
    {
        // Browsers drop tabs and newlines anywhere in a URL, and control characters and spaces at either end:
        // "/\t/evil.example" would be "//evil.example".
        if (value.Length == 0 || value[0] == ' ' || value[^1] == ' ' || value.Any(c => c < ' ' || c == '\u007F'))
        {
            return false;
        }

        return value[0] switch
        {
            '?' or '#' => true,
            // "//host" and "/\host" are other hosts as far as browsers are concerned.
            '/' => value.Length == 1 || (value[1] != '/' && value[1] != '\\'),
            _ => Schemes.Any(scheme => value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)),
        };
    }
}
