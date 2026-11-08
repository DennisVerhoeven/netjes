using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;

namespace Netjes.Routing;

/// <summary>
/// The prefix a page package is mounted at, in folder syntax: <c>/help</c>, <c>/(marketing)/help</c>,
/// <c>/support/(desk)/help</c>, <c>/[tenant]/help</c> or <c>/</c>. The package's folders go under the prefix's folders,
/// and its URL leaves out the route groups. Its segments are literal URL segments, <c>[name]</c> values and
/// <c>(group)</c> route groups; nothing that would match more than one folder, hide the package or leave the app
/// directory.
/// </summary>
internal sealed partial class MountPrefix
{
    private MountPrefix(string folder, IReadOnlyList<string> segments)
    {
        Folder = folder;
        Segments = segments;
        var url = segments.Where(s => !IsGroup(s)).ToList();
        Pattern = "/" + string.Join('/', url);
        Template = "/" + string.Join('/', url.Select(s => IsParameter(s) ? $"{{{s[1..^1]}}}" : s));
        Parameters = [.. url.Where(IsParameter).Select(s => s[1..^1])];
    }

    /// <summary>The prefix's folders, relative to the app directory: <c>support/(desk)/help</c>, or empty for <c>/</c>.</summary>
    public string Folder { get; }

    /// <summary>The prefix's folders, outermost first.</summary>
    public IReadOnlyList<string> Segments { get; }

    /// <summary>The URL pattern in file-name syntax, without route groups: <c>/support/help</c>, <c>/[tenant]/help</c>.</summary>
    public string Pattern { get; }

    /// <summary>The equivalent ASP.NET Core route template: <c>/{tenant}/help</c>.</summary>
    public string Template { get; }

    /// <summary>The names of the prefix's route parameters, outermost first.</summary>
    public IReadOnlyList<string> Parameters { get; }

    public override string ToString() => "/" + Folder;

    /// <summary>
    /// The prefix's URL for these route values: its literal segments as written, and each value encoded as one segment.
    /// Null, with the parameter in <paramref name="unfilled"/>, when a value is missing or empty, or is <c>.</c> or
    /// <c>..</c>, which a browser would resolve away.
    /// </summary>
    public string? Url(RouteValueDictionary values, out string? unfilled)
    {
        var url = new List<string>();
        foreach (var segment in Segments.Where(s => !IsGroup(s)))
        {
            if (!IsParameter(segment))
            {
                url.Add(segment);
                continue;
            }

            var name = segment[1..^1];
            var value = values.TryGetValue(name, out var raw) ? Convert.ToString(raw, CultureInfo.InvariantCulture) : null;
            if (value is null or "" or "." or "..")
            {
                unfilled = name;
                return null;
            }

            url.Add(Uri.EscapeDataString(value));
        }

        unfilled = null;
        return "/" + string.Join('/', url);
    }

    /// <summary>Parses a prefix, throwing <see cref="ArgumentException"/> with the reason when it is not one.</summary>
    public static MountPrefix Parse(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (prefix.Contains('\\'))
        {
            throw Refused(prefix, "a mount prefix uses '/' between its segments, never '\\'");
        }

        if (prefix.Contains('%'))
        {
            throw Refused(prefix, "a mount prefix is written as folders, so it cannot hold '%'");
        }

        if (!prefix.StartsWith('/'))
        {
            throw Refused(prefix, "a mount prefix starts with '/'");
        }

        string[] segments = prefix == "/" ? [] : prefix[1..].Split('/');
        var parameters = new List<string>();
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                throw Refused(prefix, "a mount prefix cannot hold an empty segment (write '/help', not '/help/' or '//help')");
            }

            if (segment is "." or "..")
            {
                throw Refused(prefix, $"a mount prefix cannot hold the segment '{segment}'");
            }

            if (segment.StartsWith("[...", StringComparison.Ordinal) || segment.StartsWith("[[", StringComparison.Ordinal))
            {
                throw Refused(prefix, $"'{segment}' is a catch-all segment, which would leave no URL for the package's pages");
            }

            if (segment.StartsWith('@'))
            {
                throw Refused(prefix, $"'{segment}' is a slot folder, which serves no URL of its own");
            }

            if (segment.StartsWith('_'))
            {
                throw Refused(prefix, $"'{segment}' is a private folder, whose pages are not routed");
            }

            if (segment.StartsWith("(.)", StringComparison.Ordinal) || segment.StartsWith("(..)", StringComparison.Ordinal)
                || segment.StartsWith("(...)", StringComparison.Ordinal))
            {
                throw Refused(prefix, $"'{segment}' is an intercepting folder, which only a slot can hold");
            }

            if (segment.StartsWith('[') && segment.EndsWith(']'))
            {
                var name = segment[1..^1];
                if (!ParameterNameRegex().IsMatch(name))
                {
                    throw Refused(prefix, $"'{name}' is not a valid parameter name (letters, digits and underscores, not starting with a digit)");
                }

                if (parameters.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    throw Refused(prefix, $"parameter '{name}' is used more than once");
                }

                parameters.Add(name);
            }
            else if (!(GroupRegex().IsMatch(segment) || LiteralRegex().IsMatch(segment)))
            {
                throw Refused(prefix,
                    $"the segment '{segment}' holds characters that are not allowed in a mount prefix: use letters, digits, " +
                    "'-', '.', '_' and '~', '[name]' for a value and '(name)' for a route group");
            }
        }

        return new MountPrefix(string.Join('/', segments), segments);
    }

    private static ArgumentException Refused(string prefix, string reason) =>
        new($"'{prefix}' is not a mount prefix: {reason}.", nameof(prefix));

    private static bool IsGroup(string segment) => segment.Length > 2 && segment[0] == '(' && segment[^1] == ')';

    private static bool IsParameter(string segment) => segment.Length > 2 && segment[0] == '[' && segment[^1] == ']';

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex ParameterNameRegex();

    [GeneratedRegex(@"^\([\p{L}\p{N}_-]+\)$")]
    private static partial Regex GroupRegex();

    [GeneratedRegex(@"^[\p{L}\p{N}._~-]+$")]
    private static partial Regex LiteralRegex();
}
