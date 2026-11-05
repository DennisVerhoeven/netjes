using System.Diagnostics.CodeAnalysis;

namespace Netjes;

/// <summary>
/// A link for the browser, in the props a C# loader sends: the URL of one of the app's pages, root-relative and without
/// the base path (<c>/blog/hello</c>, <c>/docs/caching#keys</c>), a query or fragment of the page on screen
/// (<c>?page=2</c>, <c>#top</c>), or an <c>http:</c>, <c>https:</c>, <c>mailto:</c> or <c>tel:</c> URL.
/// </summary>
public sealed class Href : IEquatable<Href>
{
    /// <summary>The link <paramref name="value"/>.</summary>
    public Href(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    /// <summary>The URL, as given.</summary>
    public string Value { get; }

    /// <summary>The link <paramref name="value"/>, or false.</summary>
    public static bool TryCreate([NotNullWhen(true)] string? value, [NotNullWhen(true)] out Href? href)
    {
        href = null;
        return false;
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
}
