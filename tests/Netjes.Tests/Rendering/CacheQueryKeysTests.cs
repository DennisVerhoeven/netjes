using Microsoft.AspNetCore.WebUtilities;
using Netjes.Rendering;

namespace Netjes.Tests.Rendering;

/// <summary>
/// Which raw query strings a page that names its keys with <c>CacheQueryKeys</c> keeps in the output cache: those whose
/// every segment names one of the keys. Each is still keyed as sent, so reading a name more leniently than the query
/// parser can cost a hit but never serve one query string's page for another.
/// </summary>
public class CacheQueryKeysTests
{
    private static readonly HashSet<string> None = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Page = new(["page"], StringComparer.OrdinalIgnoreCase);

    [Theory]
    // No query string at all, whatever the keys.
    [InlineData("", "page", true)]
    [InlineData("", null, true)]
    // The declared key, in any form the parser reads as that key.
    [InlineData("?page=2", "page", true)]
    [InlineData("?PAGE=2", "page", true)]
    [InlineData("?p%61ge=2", "page", true)]
    [InlineData("?P%41GE=2", "page", true)]
    [InlineData("?page", "page", true)]
    [InlineData("?page=", "page", true)]
    [InlineData("?page=2&page=3", "page", true)]
    [InlineData("?page=2&PAGE=3", "page", true)]
    // A value is the page's to read, separators included.
    [InlineData("?page=a=b", "page", true)]
    [InlineData("?page=a%26utm_source%3Dx", "page", true)]
    // Empty segments, which the parser drops but which make another raw query string.
    [InlineData("?", null, false)]
    [InlineData("?&", null, false)]
    [InlineData("?&&", null, false)]
    [InlineData("?", "page", false)]
    [InlineData("?&", "page", false)]
    [InlineData("?page=2&", "page", false)]
    [InlineData("?&page=2", "page", false)]
    [InlineData("?page=2&&page=3", "page", false)]
    // A segment without a name.
    [InlineData("?=1", "page", false)]
    [InlineData("?=", "page", false)]
    [InlineData("?page=2&=1", "page", false)]
    // Another name, also next to the declared one, and any name at all when none is declared.
    [InlineData("?x=1", null, false)]
    [InlineData("?page=2", null, false)]
    [InlineData("?x", "page", false)]
    [InlineData("?page=2&utm_source=x", "page", false)]
    [InlineData("?utm_source=x&page=2", "page", false)]
    // Names the parser reads as something else than the declared key, however close their raw text is.
    [InlineData("?page%3D2", "page", false)]
    [InlineData("?page%26x=1", "page", false)]
    [InlineData("?pa+ge=1", "page", false)]
    [InlineData("?+page=1", "page", false)]
    [InlineData("?%20page=1", "page", false)]
    [InlineData("?page%=1", "page", false)]
    public void ReadsOnlyDeclaredKeys_AcceptsOnlyQueriesWhoseSegmentsAllNameADeclaredKey(string query, string? key, bool expected) =>
        Assert.Equal(expected, CachePaths.ReadsOnlyDeclaredKeys(query, key is null ? None : Page));

    /// <summary>A name decoded to a key with a space in it is that key, read as the parser reads it.</summary>
    [Theory]
    [InlineData("?sort+by=name")]
    [InlineData("?sort%20by=name")]
    public void ReadsOnlyDeclaredKeys_DecodesPlusAndPercentAsTheParserDoes(string query)
    {
        var keys = new HashSet<string>(["sort by"], StringComparer.OrdinalIgnoreCase);

        Assert.True(CachePaths.ReadsOnlyDeclaredKeys(query, keys));
        Assert.Equal("sort by", Assert.Single(QueryHelpers.ParseQuery(query).Keys));
    }
}
