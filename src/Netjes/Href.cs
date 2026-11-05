using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netjes;

/// <summary>
/// A link for the browser, in the props a C# loader sends: the URL of one of the app's pages, root-relative and without
/// the base path (<c>/blog/hello</c>, <c>/docs/caching#keys</c>), a query or fragment of the page on screen
/// (<c>?page=2</c>, <c>#top</c>), or an <c>http:</c>, <c>https:</c>, <c>mailto:</c> or <c>tel:</c> URL.
/// </summary>
/// <remarks>
/// C# checks the shape of the URL only. Whether a root-relative URL is one of the app's pages is not known here: a link to
/// an endpoint or a file, such as <c>/llms.txt</c>, has the shape too. JSON holds it as a plain string, and reading one
/// checks it as the constructor does. It is not made from the request: there is no conversion from a string and no
/// <c>TryParse</c>, so minimal APIs bind no query value, route value or header to it; the constructor's parameter
/// matches no property, so <c>[AsParameters]</c> refuses it; and form binding reads none from a field of the property's
/// name. Check a URL from the request, such as a <c>returnUrl</c>, with <see cref="Page.IsLocalUrl"/> instead.
/// </remarks>
[JsonConverter(typeof(HrefJsonConverter))]
public sealed class Href : IEquatable<Href>
{
    // Compared in any case, as browsers read a scheme.
    private static readonly string[] Schemes = ["http:", "https:", "mailto:", "tel:"];

    /// <summary>The link <paramref name="url"/>, which must have the shape of one.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="url"/> starts with none of <c>/</c>, <c>?</c>, <c>#</c>, <c>http:</c>, <c>https:</c>, <c>mailto:</c>
    /// and <c>tel:</c>; starts with <c>//</c> or <c>/\</c>, which browsers read as another host; holds an ASCII control
    /// character, such as a tab or a newline, which browsers drop; or starts or ends with a space.
    /// </exception>
    public Href(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!HasTheShape(url))
        {
            throw new ArgumentException(
                $"'{url}' is not a link: start it with '/' (but not '//' or '/\\'), '?', '#', 'http:', 'https:', 'mailto:' or 'tel:', " +
                "and leave out control characters and spaces at either end.",
                nameof(url));
        }

        Value = url;
    }

    /// <summary>The URL, as given.</summary>
    public string Value { get; }

    /// <summary>
    /// The link <paramref name="url"/> when it has the shape the constructor asks for; false, with
    /// <paramref name="href"/> null, otherwise and for null.
    /// </summary>
    public static bool TryCreate([NotNullWhen(true)] string? url, [NotNullWhen(true)] out Href? href)
    {
        href = url is not null && HasTheShape(url) ? new Href(url) : null;
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

/// <summary>An <see cref="Href"/> as the plain string it holds; reading one that is not a link throws.</summary>
/// <remarks>
/// <see cref="Href"/> names it, so apps never need to. It is public for a System.Text.Json source-generated context in
/// an app, which creates the converter in the app's own code: one it could not reach would leave the property out.
/// </remarks>
public sealed class HrefJsonConverter : JsonConverter<Href>
{
    public override Href Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && Href.TryCreate(reader.GetString(), out var href)
            ? href
            // The value is left out of the message: it comes from whoever sent the JSON.
            : throw new JsonException(
                "Not a link: an Href starts with '/' (but not '//' or '/\\'), '?', '#', 'http:', 'https:', 'mailto:' or 'tel:', " +
                "and has no control characters and no spaces at either end.");

    public override void Write(Utf8JsonWriter writer, Href value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
}
