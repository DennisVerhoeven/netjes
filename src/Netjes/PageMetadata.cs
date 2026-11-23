using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Netjes;

/// <summary>
/// Page metadata from C#, the counterpart of Next.js <c>generateMetadata</c>: merged over the page's and layouts'
/// <c>export const metadata</c> (layout title templates still apply to <see cref="Title"/> but not to <see cref="AbsoluteTitle"/>).
/// </summary>
public sealed record PageMetadata
{
    /// <summary>The document title; a layout's title template still applies. <see cref="AbsoluteTitle"/> wins when both are set.</summary>
    [JsonIgnore] public string? Title { get; init; }
    /// <summary>
    /// A document title that no layout's title template wraps, the counterpart of <c>title: { absolute }</c> in a page
    /// file's metadata. It is the whole title: <c>Users · Back office</c> stays that, whatever the layouts' templates are.
    /// </summary>
    [JsonIgnore] public string? AbsoluteTitle { get; init; }
    /// <summary>The title as the payload carries it: a string, or <c>{ "absolute": … }</c>, the form the client's metadata takes.</summary>
    [JsonPropertyName("title"), JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal object? WireTitle => AbsoluteTitle is { } absolute ? new Dictionary<string, string> { ["absolute"] = absolute } : Title;
    /// <summary>The <c>description</c> meta tag.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Description { get; init; }
    /// <summary>The <c>keywords</c> meta tag.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<string>? Keywords { get; init; }
    /// <summary>The <c>robots</c> meta tag.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public PageMetadataRobots? Robots { get; init; }
    /// <summary>The canonical URL and the alternate-language links.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public PageMetadataAlternates? Alternates { get; init; }
    /// <summary>The Open Graph (<c>og:</c>) tags.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public PageMetadataOpenGraph? OpenGraph { get; init; }
    /// <summary>The Twitter card (<c>twitter:</c>) tags.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public PageMetadataTwitter? Twitter { get; init; }
    /// <summary>The <c>theme-color</c> meta tag.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ThemeColor { get; init; }
    /// <summary>Further <c>&lt;meta name content&gt;</c> tags, by name.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyDictionary<string, string>? Other { get; init; }

    private readonly string? _lang;

    /// <summary>
    /// The <c>lang</c> of the document's <c>&lt;html&gt;</c> element: a BCP 47 language tag, such as <c>nl</c> or
    /// <c>en-GB</c>. A page loader's wins over its layout loaders', and the innermost layout loader's over those above
    /// it; a page none of them gives one has <see cref="NetjesOptions.Lang"/>, or the locale with localization. The
    /// server writes it in the first response, and a client navigation updates the attribute. A value that does not
    /// have the shape of a language tag throws <see cref="ArgumentException"/>.
    /// </summary>
    [JsonIgnore]
    public string? Lang
    {
        get => _lang;
        init => _lang = value is null || LanguageTag.IsValid(value)
            ? value
            : throw new ArgumentException($"'{value}' is not a BCP 47 language tag, such as 'nl' or 'en-GB'.", nameof(Lang));
    }
}

/// <summary>The shape of a BCP 47 language tag (RFC 5646), which <see cref="PageMetadata.Lang"/> must have.</summary>
internal static partial class LanguageTag
{
    // A primary subtag of two to eight letters, or x (private use) or i (grandfathered) with at least one subtag after
    // it, then subtags of one to eight letters and digits, each after one hyphen. ASCII only, so no quote, angle bracket,
    // space or ampersand can reach an attribute. \z, not $, which also matches before a final newline.
    [GeneratedRegex(@"^(?:[A-Za-z]{2,8}(?:-[A-Za-z0-9]{1,8})*|[xXiI](?:-[A-Za-z0-9]{1,8})+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();

    public static bool IsValid(string value) => Shape().IsMatch(value);
}

/// <summary>Whether search engines may index the page and follow its links.</summary>
/// <param name="Index">False writes <c>noindex</c>.</param>
/// <param name="Follow">False writes <c>nofollow</c>.</param>
public sealed record PageMetadataRobots(bool Index = true, bool Follow = true);

/// <summary>The canonical URL and the other-language versions of the page.</summary>
/// <param name="Canonical">The <c>rel="canonical"</c> link.</param>
/// <param name="Languages">A <c>rel="alternate" hreflang</c> link per locale, by locale name.</param>
public sealed record PageMetadataAlternates(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Canonical = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string>? Languages = null);

/// <summary>The Open Graph tags link previews use.</summary>
public sealed record PageMetadataOpenGraph
{
    /// <summary>The <c>og:title</c> tag.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Title { get; init; }
    /// <summary>The <c>og:description</c> tag.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Description { get; init; }
    /// <summary>The <c>og:url</c> tag.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Url { get; init; }
    /// <summary>The <c>og:site_name</c> tag.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SiteName { get; init; }
    /// <summary>The <c>og:type</c> tag, e.g. <c>article</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Type { get; init; }
    /// <summary>The <c>og:locale</c> tag.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Locale { get; init; }
    /// <summary>The <c>og:image</c> tags, one per image.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<PageMetadataImage>? Images { get; init; }
}

/// <summary>An image for a link preview, in <see cref="PageMetadataOpenGraph.Images"/> or <see cref="PageMetadataTwitter.Images"/>.</summary>
/// <param name="Url">The image's URL, written as <c>og:image</c> or <c>twitter:image</c>.</param>
/// <param name="Width">The width in pixels, written as <c>og:image:width</c>. Twitter images leave it out.</param>
/// <param name="Height">The height in pixels, written as <c>og:image:height</c>. Twitter images leave it out.</param>
/// <param name="Alt">A text alternative, written as <c>og:image:alt</c> or <c>twitter:image:alt</c>.</param>
public sealed record PageMetadataImage(
    string Url,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Width = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Height = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Alt = null)
{
    /// <summary>A bare URL is an image with no details, so <c>Images = ["/card.png"]</c> works.</summary>
    public static implicit operator PageMetadataImage(string url) => new(url);
}

/// <summary>
/// The Twitter card tags link previews use. Once this metadata is merged over the files', the Twitter tags that are
/// not set fill in from Open Graph and the page's title and description, as in Next.js.
/// </summary>
public sealed record PageMetadataTwitter
{
    /// <summary><c>summary</c>, <c>summary_large_image</c>, <c>app</c> or <c>player</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Card { get; init; }
    /// <summary>The <c>twitter:site</c> tag: the site's account.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Site { get; init; }
    /// <summary>The <c>twitter:creator</c> tag: the author's account.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Creator { get; init; }
    /// <summary>The <c>twitter:image</c> tags, one per image, each followed by its <c>twitter:image:alt</c> when it has an alt.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<PageMetadataImage>? Images { get; init; }
}
