using Microsoft.Extensions.Primitives;

namespace Netjes.Rendering;

/// <summary>
/// Reads a Content-Security-Policy as browsers parse one (CSP Level 3, "parse a serialized CSP"): directives separated by
/// semicolons, each a name, compared ignoring case, followed by its source expressions, separated by ASCII whitespace.
/// Of a directive written twice, browsers obey the first.
/// </summary>
internal static class CspDirectives
{
    private static readonly char[] AsciiWhitespace = [' ', '\t', '\n', '\f', '\r'];

    /// <summary>
    /// Each policy in the header's values. Browsers enforce every one, whether each comes in a header of its own or
    /// several are joined by commas in one value (a source expression never holds a comma: it is written <c>%2C</c>).
    /// </summary>
    public static IEnumerable<string> Policies(StringValues values) =>
        values.SelectMany(value => (value ?? "").Split(','));

    /// <summary>The source expressions of the policy's first directive named <paramref name="name"/>, or null without one.</summary>
    public static IReadOnlyList<string>? Sources(string? policy, string name)
    {
        foreach (var directive in (policy ?? "").Split(';'))
        {
            var words = directive.Split(AsciiWhitespace, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 0 && words[0].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return words[1..];
            }
        }

        return null;
    }
}
