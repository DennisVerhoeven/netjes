using System.Text;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Netjes.Rendering;

namespace Netjes.Routing;

/// <summary>
/// On an endpoint that is no page but answers one spelling of its URL, as a page does: <c>/robots.txt</c> and
/// <c>/sitemap.xml</c>. <see cref="OtherSpellings"/> answers the others with a plain 308.
/// </summary>
internal sealed class OneSpellingMetadata;

/// <summary>
/// The last middleware of <c>UseNetjes</c>, after routing: a <c>GET</c> or <c>HEAD</c> for a page in another spelling of
/// its URL than its file's gets a 308 to the file's spelling, and a client navigation a redirect payload. ASP.NET Core's
/// matcher matches literal segments in any case and ignores one trailing slash, so every page answers many URLs; this
/// sends another case of a literal (<see cref="NetjesOptions.RedirectLiteralCase"/>) and, with
/// <see cref="TrailingSlash.Never"/>, a trailing slash to the one its file names. Route values, the query string and
/// the base path go along as they were sent.
/// Only the URL the client sent is redirected: the request target, which a rewrite or a re-execution leaves as it was,
/// must spell the path routing matched, so a server that passes on no target (TestServer) redirects nothing. The
/// location is built from the target, so a value goes back as it came (<c>%2541</c> stays that, where the decoded path
/// would give <c>%41</c>, which is <c>A</c>), and none is sent that a client could read as another site.
/// </summary>
internal sealed class OtherSpellings(NetjesContext context)
{
    private readonly bool _literalCase = context.Options.RedirectLiteralCase;
    private readonly bool _noTrailingSlash = context.Options.TrailingSlash == TrailingSlash.Never;

    /// <summary>Whether a setting asks for a redirect, in an app that serves requests: an export writes the files' spellings.</summary>
    public static bool AreRedirected(NetjesContext context) =>
        (context.Options.RedirectLiteralCase || context.Options.TrailingSlash == TrailingSlash.Never)
        && context.Command is not { Name: "export" };

    public Task InvokeAsync(HttpContext http, RequestDelegate next)
    {
        if (http.GetEndpoint() is not RouteEndpoint endpoint
            || !(HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)))
        {
            return next(http);
        }

        var page = endpoint.Metadata.GetMetadata<PageEndpointMetadata>() is { Pattern: not null };
        if ((!page && endpoint.Metadata.GetMetadata<OneSpellingMetadata>() is null)
            // A page run again for another URL (a failed form post, a status code page, an exception handler) answers
            // the request that was sent, and must not turn into a redirect.
            || ReExecute.IsReExecution(http)
            || http.Features.Get<IStatusCodeReExecuteFeature>() is not null
            || http.Features.Get<IExceptionHandlerFeature>() is not null
            || LocationFor(http, endpoint.RoutePattern.PathSegments) is not { } location)
        {
            return next(http);
        }

        if (page)
        {
            return http.RequestServices.GetRequiredService<PageRenderer>().RedirectToAsync(http, location, permanent: true);
        }

        http.Response.StatusCode = StatusCodes.Status308PermanentRedirect;
        http.Response.Headers.Location = location;
        return Task.CompletedTask;
    }

    /// <summary>
    /// The file's spelling of the URL the client sent, or null when it is that spelling already, when the request
    /// target does not spell the path routing matched, or when the location could lead off the site.
    /// </summary>
    private string? LocationFor(HttpContext http, IReadOnlyList<RoutePatternPathSegment> segments)
    {
        // First on the path routing matched, without allocating: most requests have nothing to change.
        var path = http.Request.Path.Value ?? "";
        if (!Changes(path, segments))
        {
            return null;
        }

        if (http.Features.Get<IHttpRequestFeature>()?.RawTarget is not ['/', ..] target)
        {
            return null;
        }

        var queryStart = target.IndexOf('?');
        var sent = queryStart < 0 ? target : target[..queryStart];
        string decoded;
        try
        {
            // Decoded as the server decodes a path: %2F stays as it is.
            decoded = PathString.FromUriComponent(sent).Value ?? "";
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        var pathBase = http.Request.PathBase.Value ?? "";
        var location = new StringBuilder(target.Length + 8);
        int below;
        if (decoded.Length == pathBase.Length + path.Length && decoded.StartsWith(pathBase, StringComparison.Ordinal)
            && decoded.EndsWith(path, StringComparison.Ordinal))
        {
            // The base path is in the target (UsePathBase): it goes out as the client spelled it. A slash in the decoded
            // path is one in the target, so the base path takes as many segments of it.
            below = 0;
            var baseSegments = pathBase.AsSpan().Count('/');
            for (var segment = 0; segment < baseSegments; segment++)
            {
                var next = sent.IndexOf('/', below + 1);
                below = next < 0 ? sent.Length : next;
            }

            location.Append(sent, 0, below);
        }
        else if (pathBase.Length > 0 && string.Equals(decoded, path, StringComparison.Ordinal))
        {
            // A base path the client sees but the target leaves out, from a proxy that strips it (X-Forwarded-Prefix).
            below = 0;
            location.Append(http.Request.PathBase.ToUriComponent());
        }
        else
        {
            // Rewritten, normalised (dot segments) or run again for another path: not the URL the client sent.
            return null;
        }

        AppendFilesSpelling(location, path, sent, below, segments);
        if (queryStart >= 0)
        {
            location.Append(target, queryStart, target.Length - queryStart);
        }

        var result = location.ToString();
        return StaysOnTheSite(result) ? result : null;
    }

    /// <summary>
    /// Whether <paramref name="path"/>, which routing matched to <paramref name="segments"/>, spells a literal in another
    /// case, or ends in a slash to drop. False when the two do not line up segment for segment, so nothing is redirected.
    /// </summary>
    private bool Changes(string path, IReadOnlyList<RoutePatternPathSegment> segments)
    {
        var changes = false;
        var position = 0;
        var catchAll = false;
        for (var i = 0; i < segments.Count; i++)
        {
            if (IsCatchAll(segments[i]))
            {
                catchAll = true;
                break;
            }

            if (position >= path.Length || path[position] != '/')
            {
                return false;
            }

            var end = SegmentEnd(path, position);
            changes |= _literalCase && LiteralOf(segments[i]) is { } literal && !path.AsSpan(position + 1, end - position - 1).SequenceEqual(literal);
            position = end;
        }

        // Without a catch-all, what is left is nothing or the one trailing slash the matcher ignores.
        if (!catchAll && path.Length - position > 1)
        {
            return false;
        }

        return changes || EndsInASlashToDrop(path);
    }

    /// <summary>
    /// Appends the target's path below the base path (<paramref name="sent"/> from <paramref name="below"/>), each literal
    /// in another case replaced by the file's and every other segment as it was sent; a catch-all's value goes as it was.
    /// </summary>
    private void AppendFilesSpelling(StringBuilder location, string path, string sent, int below, IReadOnlyList<RoutePatternPathSegment> segments)
    {
        var position = 0;
        var at = below;
        for (var i = 0; i < segments.Count && !IsCatchAll(segments[i]); i++)
        {
            // Changes saw the decoded path line up with the segments; its slashes are the target's.
            var end = SegmentEnd(path, position);
            var sentEnd = SegmentEnd(sent, at);
            if (_literalCase && LiteralOf(segments[i]) is { } literal && !path.AsSpan(position + 1, end - position - 1).SequenceEqual(literal))
            {
                location.Append('/').Append(Escaped(literal));
            }
            else
            {
                location.Append(sent, at, sentEnd - at);
            }

            position = end;
            at = sentEnd;
        }

        location.Append(sent, at, sent.Length - at);
        if (EndsInASlashToDrop(path))
        {
            location.Length--;
        }
    }

    // One trailing slash, but never the root's: '/' stays, at the root and under a base path.
    private bool EndsInASlashToDrop(string path) => _noTrailingSlash && path.Length > 1 && path[^1] == '/';

    private static int SegmentEnd(string path, int start)
    {
        var end = path.IndexOf('/', start + 1);
        return end < 0 ? path.Length : end;
    }

    private static string? LiteralOf(RoutePatternPathSegment segment) =>
        segment.IsSimple && segment.Parts[0] is RoutePatternLiteralPart literal ? literal.Content : null;

    private static bool IsCatchAll(RoutePatternPathSegment segment)
    {
        for (var i = 0; i < segment.Parts.Count; i++)
        {
            if (segment.Parts[i] is RoutePatternParameterPart { IsCatchAll: true })
            {
                return true;
            }
        }

        return false;
    }

    // Escaped as PathString escapes a path, so that it decodes to the literal: a '%' first, since PathString keeps
    // anything that looks like an escape already (a folder named a%41 would go out as aA otherwise).
    private static string Escaped(string literal) =>
        new PathString("/" + literal.Replace("%", "%25", StringComparison.Ordinal)).ToUriComponent()[1..];

    /// <summary>
    /// Whether a browser, or a client that decodes the location first, takes it for a path on this site: one slash and
    /// then no second slash or backslash, as <see cref="Page.IsLocalUrl"/> has it, read as sent and decoded. Nothing in
    /// it may read differently in a browser than on the server: no control character or space (browsers drop tabs and
    /// newlines), nothing beyond ASCII (no header carries it), no backslash (a slash to a browser) and no '#' (the end of
    /// the path). A target can hold each of those, since Kestrel takes them as sent.
    /// </summary>
    private static bool StaysOnTheSite(string location)
    {
        foreach (var c in location)
        {
            if (c <= ' ' || c >= '\x7f' || c is '\\' or '#')
            {
                return false;
            }
        }

        return Page.IsLocalUrl(location) && Page.IsLocalUrl(Uri.UnescapeDataString(location));
    }
}
