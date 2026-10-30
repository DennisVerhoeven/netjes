using System.Text.RegularExpressions;

namespace Netjes.Routing;

internal sealed record RouteParameter(string Name, RouteParameterKind Kind);

internal enum RouteParameterKind { Single, CatchAll, OptionalCatchAll }

/// <summary>
/// A folder on the way to a page that has segment files: a layout, a template, a loading (Suspense) fallback, and
/// error, not-found, forbidden and unauthorized boundaries.
/// </summary>
/// <param name="Id">The folder relative to the app root, e.g. <c>app/blog</c>.</param>
/// <param name="Pattern">The URL prefix the folder covers, e.g. <c>/blog/[slug]</c>; route groups add nothing.</param>
/// <param name="Parameters">Route parameters in that prefix, outermost first.</param>
internal sealed record RouteSegment(
    string Id,
    string Pattern,
    IReadOnlyList<string> Parameters,
    string? Layout = null,
    string? Template = null,
    string? Loading = null,
    string? Error = null,
    string? NotFound = null,
    string? Forbidden = null,
    string? Unauthorized = null)
{
    public bool Equals(RouteSegment? other) =>
        other is not null
        && Id == other.Id && Pattern == other.Pattern && Parameters.SequenceEqual(other.Parameters)
        && Layout == other.Layout && Template == other.Template && Loading == other.Loading && Error == other.Error
        && NotFound == other.NotFound && Forbidden == other.Forbidden && Unauthorized == other.Unauthorized;

    public override int GetHashCode() => HashCode.Combine(Id, Pattern, Layout, Template, Loading, Error, NotFound);

    /// <summary>Parallel routes: the folder's <c>@slot</c> subfolders, by slot name (without the <c>@</c>).</summary>
    public IReadOnlyDictionary<string, SlotRoutes> Slots { get; init; } = new Dictionary<string, SlotRoutes>();

    /// <summary>
    /// The file a slot renders for a page: the slot's page whose pattern below this folder covers the page's (slots
    /// mirror the route tree; parameters match by position, <c>[...x]</c> covers one or more segments, the most
    /// specific page wins), else its <c>default.tsx</c>, else none.
    /// </summary>
    public string? SlotFor(string slot, PageRoute route) => SlotPageFor(slot, route).File;

    /// <summary>Like <see cref="SlotFor"/>, with the slot page's own pattern below this folder (null for the default).</summary>
    public (string? File, string? Pattern) SlotPageFor(string slot, PageRoute route)
    {
        if (!Slots.TryGetValue(slot, out var routes))
        {
            return (null, null);
        }

        var below = Pattern == "/" ? route.Pattern : route.Pattern.Length > Pattern.Length ? route.Pattern[Pattern.Length..] : "/";
        if (routes.Pages.TryGetValue(below, out var exact))
        {
            return (exact, below);
        }

        var page = below.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var match = routes.Pages
            .Select(p => (p.Key, File: p.Value, Rank: SlotPattern.Rank(p.Key.Split('/', StringSplitOptions.RemoveEmptyEntries), page)))
            .Where(p => p.Rank is not null)
            .OrderBy(p => p.Rank, StringComparer.Ordinal)
            .FirstOrDefault();
        return match.File is null ? (routes.Default, null) : (match.File, match.Key);
    }
}

/// <summary>Matching a slot page's pattern against a page's, both in file syntax and below the slot's folder.</summary>
internal static class SlotPattern
{
    /// <summary>
    /// Null when the slot pattern does not cover the page pattern. Otherwise its specificity, lower is more specific:
    /// one letter per slot segment, static before <c>[x]</c> before <c>[...x]</c> before <c>[[...x]]</c>.
    /// </summary>
    public static string? Rank(IReadOnlyList<string> slot, IReadOnlyList<string> page)
    {
        var rank = new System.Text.StringBuilder();
        for (var i = 0; i < slot.Count; i++)
        {
            var segment = slot[i];
            var rest = page.Skip(i).ToList();
            if (IsOptionalCatchAll(segment))
            {
                return i == slot.Count - 1 ? rank.Append('d').ToString() : null;
            }

            if (IsCatchAll(segment))
            {
                // One or more segments; a page's own [[...x]] may have none.
                return i == slot.Count - 1 && rest.Count > 0 && !IsOptionalCatchAll(rest[^1]) ? rank.Append('c').ToString() : null;
            }

            if (rest.Count == 0 || IsCatchAll(rest[0]) || IsOptionalCatchAll(rest[0]))
            {
                return null;
            }

            if (IsParameter(segment))
            {
                rank.Append('b');
            }
            else if (!IsParameter(rest[0]) && string.Equals(segment, rest[0], StringComparison.OrdinalIgnoreCase))
            {
                rank.Append('a');
            }
            else
            {
                return null;
            }
        }

        return slot.Count == page.Count ? rank.ToString() : null;
    }

    /// <summary>
    /// The route values a pattern (file syntax) gives a URL's segments, by position: <c>[x]</c> one segment,
    /// <c>[...x]</c> and <c>[[...x]]</c> the rest as an array. Null when the URL does not fit the pattern.
    /// </summary>
    public static Dictionary<string, object?>? ParamsFor(IReadOnlyList<string> pattern, IReadOnlyList<string> url)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < pattern.Count; i++)
        {
            var part = pattern[i];
            if (IsOptionalCatchAll(part) || IsCatchAll(part))
            {
                if (IsCatchAll(part) && url.Count <= i)
                {
                    return null;
                }

                values[NameOf(part)] = url.Skip(i).ToArray();
                return values;
            }

            if (i >= url.Count)
            {
                return null;
            }

            if (IsParameter(part))
            {
                values[NameOf(part)] = url[i];
            }
            else if (!string.Equals(part, url[i], StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return pattern.Count == url.Count ? values : null;
    }

    private static string NameOf(string parameter) => parameter.Trim('[', ']').TrimStart('.');

    private static bool IsParameter(string segment) => segment.StartsWith('[') && segment.EndsWith(']');

    private static bool IsCatchAll(string segment) => segment.StartsWith("[...", StringComparison.Ordinal) && segment.EndsWith(']');

    private static bool IsOptionalCatchAll(string segment) => segment.StartsWith("[[...", StringComparison.Ordinal) && segment.EndsWith("]]");
}

/// <param name="Pages">The slot's page files by pattern below the slot's folder, e.g. <c>/settings</c> or <c>/</c>.</param>
/// <param name="Default">The slot's <c>default.tsx</c>, rendered when no page of the slot matches.</param>
internal sealed record SlotRoutes(IReadOnlyDictionary<string, string> Pages, string? Default);

/// <summary>
/// An intercepting route: a page in a slot (<c>@modal/(.)photo/[id]/page.tsx</c>) shown in that slot instead of
/// navigating away, when the client moves to <paramref name="Pattern"/> from a page inside <paramref name="Segment"/>.
/// </summary>
internal sealed record RouteIntercept(string Segment, string Slot, string Page, string Pattern);

/// <summary>A page file and the URL it serves.</summary>
/// <param name="Pattern">URL pattern in file-name syntax, e.g. <c>/blog/[slug]</c>. Route groups are removed.</param>
/// <param name="Template">The equivalent ASP.NET Core route template, e.g. <c>/blog/{slug}</c>.</param>
/// <param name="File">Module path relative to the app root, e.g. <c>app/blog/[slug]/page.tsx</c>.</param>
/// <param name="Segments">The folders with segment files wrapping the page, outermost first.</param>
internal sealed record PageRoute(
    string Pattern,
    string Template,
    string File,
    IReadOnlyList<RouteSegment> Segments,
    IReadOnlyList<RouteParameter> Parameters)
{
    /// <summary>Layout modules wrapping the page, outermost first.</summary>
    public IReadOnlyList<string> Layouts { get; } = [.. Segments.Where(s => s.Layout is not null).Select(s => s.Layout!)];
}

/// <summary>A page that is rendered for a condition rather than a URL (404, error).</summary>
internal sealed record SpecialPage(string File, IReadOnlyList<RouteSegment> Segments)
{
    public IReadOnlyList<string> Layouts { get; } = [.. Segments.Where(s => s.Layout is not null).Select(s => s.Layout!)];
}

/// <summary>
/// Two pages in <c>app/</c> of one shape, which match the same URLs whatever their parameters are called, or two files
/// for one convention in one folder; the message names both.
/// </summary>
public sealed class RouteConflictException(string message) : Exception(message);

/// <summary>
/// A file in <c>app/</c> whose name the conventions do not allow, or page files in a <c>pages/</c> folder, which
/// Netjes does not route; the message names the file and what to do instead.
/// </summary>
public sealed class InvalidPageFileException(string message) : Exception(message);

/// <summary>
/// Turns the files in <c>app/</c> into routes, following the Next.js App Router conventions: <c>page</c> files are
/// pages; <c>layout</c>, <c>template</c>, <c>loading</c>, <c>error</c>, <c>not-found</c>, <c>forbidden</c> and
/// <c>unauthorized</c> files apply to their folder; root <c>global-error</c>; <c>[param]</c>, <c>[...catchAll]</c>,
/// <c>[[...optional]]</c>, <c>(group)</c> and <c>_private</c> folders, <c>@slot</c> folders and intercepting
/// <c>(.)</c> folders inside them; other files are colocated.
/// </summary>
internal sealed partial class RouteTable
{
    private static readonly string[] PageExtensions = [".tsx", ".jsx"];
    private static readonly string[] AppSegmentFiles = ["layout", "template", "loading", "error", "not-found", "forbidden", "unauthorized"];

    private readonly Dictionary<string, PageRoute> _byPattern;

    private RouteTable(
        IReadOnlyList<PageRoute> pages,
        SpecialPage? notFound,
        SpecialPage? error,
        SpecialPage? globalError,
        IReadOnlyList<RouteSegment> rootSegments)
    {
        Pages = pages;
        NotFound = notFound;
        Error = error;
        GlobalError = globalError;
        RootSegments = rootSegments;
        RootLayouts = [.. rootSegments.Where(s => s.Layout is not null).Select(s => s.Layout!)];
        _byPattern = pages.ToDictionary(p => p.Pattern, StringComparer.Ordinal);
    }

    public static RouteTable Empty { get; } = new([], null, null, null, []);

    public IReadOnlyList<PageRoute> Pages { get; }
    public SpecialPage? NotFound { get; }
    public SpecialPage? Error { get; }

    /// <summary><c>app/global-error.tsx</c>: rendered without any layout when even the root ones fail.</summary>
    public SpecialPage? GlobalError { get; }

    /// <summary>The root segments, used to wrap the built-in 404 and error pages.</summary>
    public IReadOnlyList<RouteSegment> RootSegments { get; }

    /// <summary>Layouts of the root segments.</summary>
    public IReadOnlyList<string> RootLayouts { get; }

    public PageRoute? Find(string pattern) => _byPattern.GetValueOrDefault(pattern);

    /// <summary>Every intercepting route in the app.</summary>
    public IReadOnlyList<RouteIntercept> Intercepts { get; private init; } = [];

    /// <summary>The intercepting routes that can show <paramref name="pattern"/> in a slot instead.</summary>
    /// <remarks>Matched by shape: <c>(.)photo/[photoId]</c> intercepts <c>/photo/[id]</c>.</remarks>
    public IReadOnlyList<RouteIntercept> InterceptsFor(string pattern) =>
        [.. Intercepts.Where(i => string.Equals(ShapeOf(i.Pattern), ShapeOf(pattern), StringComparison.OrdinalIgnoreCase))];

    public static RouteTable Build(IEnumerable<string> files, string appDirectory = "app")
    {
        // In ordinal order, so the same files give the same table and the same error, whichever order the file system
        // (development) or the build manifest (production) lists them in.
        var all = files.Select(f => f.Replace('\\', '/')).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var app = BuildAppRouter(all, Prefix(appDirectory));

        var routes = new List<PageRoute>();
        var byShape = new Dictionary<string, PageRoute>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in app.Routes)
        {
            var shape = ShapeOf(route);
            if (byShape.TryGetValue(shape, out var existing))
            {
                // Conflict names the files in ordinal order; the URL is that of the one named first.
                var first = string.CompareOrdinal(existing.File, route.File) < 0 ? existing : route;
                throw Conflict(existing.File, route.File, $"both match the URL '{first.Pattern}'");
            }

            byShape[shape] = route;
            routes.Add(route);
        }

        routes.Sort((a, b) => string.CompareOrdinal(a.Pattern, b.Pattern));
        return new RouteTable(routes, app.NotFound, app.Error, app.GlobalError, app.RootSegments)
        {
            Intercepts = [.. (app.Intercepts ?? []).OrderBy(i => i.Segment, StringComparer.Ordinal).ThenBy(i => i.Slot, StringComparer.Ordinal)],
        };
    }

    private sealed record RouterResult(
        List<PageRoute> Routes,
        SpecialPage? NotFound,
        SpecialPage? Error,
        SpecialPage? GlobalError,
        IReadOnlyList<RouteSegment> RootSegments,
        IReadOnlyList<RouteIntercept>? Intercepts = null);

    private static string Prefix(string directory) => directory.Replace('\\', '/').Trim('/') + "/";

    private static List<(string File, string[] Segments)> Candidates(List<string> files, string prefix) =>
        files
            .Where(f => f.StartsWith(prefix, StringComparison.Ordinal))
            .Where(f => !IsTestFile(f))
            .Select(f => (File: f, Segments: f[prefix.Length..^Path.GetExtension(f).Length].Split('/')))
            .ToList();

    private static RouterResult BuildAppRouter(List<string> files, string prefix)
    {
        var root = prefix.TrimEnd('/');
        // Convention files by folder (relative to the app directory); everything else is colocated code.
        var folders = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        // Parallel routes: folder -> slot -> (pages by pattern below the slot, default).
        var slots = new Dictionary<string, Dictionary<string, (Dictionary<string, string> Pages, string? Default)>>(StringComparer.Ordinal);
        var intercepts = new List<RouteIntercept>();
        foreach (var (file, segments) in Candidates(files, prefix))
        {
            var name = segments[^1];
            var directories = segments[..^1];
            if (IsPrivate(directories))
            {
                continue;
            }

            var slotIndex = Array.FindIndex(directories, d => d.Length > 1 && d[0] == '@');
            if (slotIndex >= 0)
            {
                if (!PageExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) || name is not ("page" or "default"))
                {
                    continue;
                }

                var parent = string.Join('/', directories[..slotIndex]);
                if (!slots.TryGetValue(parent, out var bySlot))
                {
                    slots[parent] = bySlot = new(StringComparer.Ordinal);
                }

                var slotName = directories[slotIndex][1..];
                if (!bySlot.TryGetValue(slotName, out var slot))
                {
                    bySlot[slotName] = slot = (new Dictionary<string, string>(StringComparer.Ordinal), null);
                }

                var below = directories[(slotIndex + 1)..];
                var slotFolder = prefix + string.Join('/', directories[..(slotIndex + 1)]);
                if (name == "default")
                {
                    if (below.Length == 0)
                    {
                        if (slot.Default is { } other)
                        {
                            throw Conflict(other, file, $"are both the default of {slotFolder}");
                        }

                        bySlot[slotName] = slot with { Default = file };
                    }
                }
                else if (below.Length > 0 && InterceptMarker(below[0]) is var (levels, rest) && levels >= 0)
                {
                    // (.) the slot's own level, (..) one up, (...) the root: the URL this page intercepts.
                    var baseUrl = directories[..slotIndex].Where(d => !IsGroup(d) && !(d.Length > 1 && d[0] == '@')).ToList();
                    var kept = levels == int.MaxValue ? [] : baseUrl.Take(Math.Max(0, baseUrl.Count - levels)).ToList();
                    var target = "/" + string.Join('/', kept.Append(rest).Concat(below[1..].Where(d => !IsGroup(d))).Where(d => d.Length > 0));
                    intercepts.Add(new RouteIntercept(SegmentOf(root, directories[..slotIndex]).Id, slotName, file, target));
                }
                else
                {
                    var pattern = "/" + string.Join('/', below.Where(d => !IsGroup(d)));
                    if (slot.Pages.TryGetValue(pattern, out var other))
                    {
                        throw Conflict(other, file, $"both match the URL '{pattern}' in {slotFolder}");
                    }

                    slot.Pages[pattern] = file;
                }

                continue;
            }

            if (name == "route")
            {
                throw new InvalidPageFileException(
                    $"'{file}': route handlers are C# in Netjes. Map a minimal API endpoint instead (app.MapGet, app.MapPost, ...).");
            }

            if (!PageExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)
                || !(name is "page" or "global-error" || AppSegmentFiles.Contains(name)))
            {
                continue;
            }

            if (name == "global-error" && directories.Length > 0)
            {
                throw new InvalidPageFileException($"'{file}': global-error is only supported at the app root ({prefix}global-error).");
            }

            var key = string.Join('/', directories);
            if (!folders.TryGetValue(key, out var conventions))
            {
                folders[key] = conventions = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            if (conventions.TryGetValue(name, out var existing))
            {
                throw Conflict(existing, file, $"are both the {name} of {(key.Length == 0 ? root : $"{root}/{key}")}");
            }

            conventions[name] = file;
        }

        RouteSegment? SegmentAt(string[] directories)
        {
            var key = string.Join('/', directories);
            var c = folders.GetValueOrDefault(key) ?? [];
            var folderSlots = slots.GetValueOrDefault(key);
            if (!AppSegmentFiles.Any(c.ContainsKey) && folderSlots is null)
            {
                return null;
            }

            return SegmentOf(root, directories) with
            {
                Slots = folderSlots?.ToDictionary(s => s.Key, s => new SlotRoutes(s.Value.Pages, s.Value.Default), StringComparer.Ordinal)
                    ?? new Dictionary<string, SlotRoutes>(),
                Layout = c.GetValueOrDefault("layout"),
                Template = c.GetValueOrDefault("template"),
                Loading = c.GetValueOrDefault("loading"),
                Error = c.GetValueOrDefault("error"),
                NotFound = c.GetValueOrDefault("not-found"),
                Forbidden = c.GetValueOrDefault("forbidden"),
                Unauthorized = c.GetValueOrDefault("unauthorized"),
            };
        }

        IReadOnlyList<RouteSegment> SegmentsFor(string[] directories) =>
            [.. Enumerable.Range(0, directories.Length + 1).Select(depth => SegmentAt(directories[..depth])).OfType<RouteSegment>()];

        var routes = new List<PageRoute>();
        foreach (var (folder, conventions) in folders)
        {
            if (conventions.TryGetValue("page", out var page))
            {
                var directories = folder.Length == 0 ? [] : folder.Split('/');
                routes.Add(ParseRoute(page, [.. directories], SegmentsFor(directories)));
            }
        }

        var rootSegments = SegmentsFor([]);
        var rootConventions = folders.GetValueOrDefault("") ?? [];
        return new RouterResult(
            routes,
            rootConventions.TryGetValue("not-found", out var notFound) ? new SpecialPage(notFound, rootSegments) : null,
            rootConventions.TryGetValue("error", out var error)
                ? new SpecialPage(error, [.. rootSegments.Select(s => s with { Error = null })])
                : null,
            rootConventions.TryGetValue("global-error", out var globalError) ? new SpecialPage(globalError, []) : null,
            rootSegments,
            intercepts);
    }

    // Two files for one convention (page.tsx and page.jsx): the one listed last would win, and development (the file
    // system) and production (the build manifest) list them in different orders. Two pages of one shape conflict too.
    // The files are named in ordinal order, so the message does not depend on the order either.
    private static RouteConflictException Conflict(string one, string another, string what)
    {
        var (first, second) = string.CompareOrdinal(one, another) < 0 ? (one, another) : (another, one);
        return new RouteConflictException($"'{first}' and '{second}' {what}. Rename or remove one of them.");
    }

    /// <summary>
    /// The levels an intercepting folder name goes up ((.) 0, (..) 1, (..)(..) 2, (...) to the root) and the folder
    /// name after the marker; (-1, "") when it is no intercepting folder.
    /// </summary>
    private static (int Levels, string Name) InterceptMarker(string folder)
    {
        if (folder.StartsWith("(...)", StringComparison.Ordinal)) return (int.MaxValue, folder[5..]);
        if (folder.StartsWith("(.)", StringComparison.Ordinal)) return (0, folder[3..]);
        var levels = 0;
        var rest = folder;
        while (rest.StartsWith("(..)", StringComparison.Ordinal))
        {
            levels++;
            rest = rest[4..];
        }

        return levels > 0 ? (levels, rest) : (-1, "");
    }

    /// <summary>The segment for a folder, without its files: its id, URL prefix and parameters.</summary>
    private static RouteSegment SegmentOf(string root, string[] directories)
    {
        var url = directories.Where(d => !IsGroup(d)).ToList();
        var parameters = url
            .Select(d => OptionalCatchAllRegex().Match(d) is { Success: true } o ? o.Groups[1].Value
                : CatchAllRegex().Match(d) is { Success: true } c ? c.Groups[1].Value
                : SingleRegex().Match(d) is { Success: true } p ? p.Groups[1].Value
                : null)
            .OfType<string>()
            .ToList();
        return new RouteSegment(
            directories.Length == 0 ? root : $"{root}/{string.Join('/', directories)}", "/" + string.Join('/', url), parameters);
    }

    private static bool IsPrivate(IEnumerable<string> directories) => directories.Any(d => d.StartsWith('_'));

    // Co-located tests (page.test.tsx, page.spec.jsx) are not pages; the Vite plugin leaves them out of the build too.
    private static bool IsTestFile(string file) =>
        Path.GetExtension(Path.GetFileNameWithoutExtension(file)) is ".test" or ".spec";

    private static PageRoute ParseRoute(string file, List<string> segments, IReadOnlyList<RouteSegment> routeSegments)
    {
        var urlSegments = segments.Where(s => !IsGroup(s)).ToList();

        var parameters = new List<RouteParameter>();
        var templateSegments = new List<string>();
        for (var i = 0; i < urlSegments.Count; i++)
        {
            var segment = urlSegments[i];
            var isLast = i == urlSegments.Count - 1;

            if (OptionalCatchAllRegex().Match(segment) is { Success: true } optional)
            {
                var name = RequireParameter(file, optional.Groups[1].Value, isLast, catchAll: true, parameters);
                parameters.Add(new RouteParameter(name, RouteParameterKind.OptionalCatchAll));
                templateSegments.Add($"{{**{name}}}");
            }
            else if (CatchAllRegex().Match(segment) is { Success: true } catchAll)
            {
                var name = RequireParameter(file, catchAll.Groups[1].Value, isLast, catchAll: true, parameters);
                parameters.Add(new RouteParameter(name, RouteParameterKind.CatchAll));
                templateSegments.Add($"{{**{name}:minlength(1)}}");
            }
            else if (SingleRegex().Match(segment) is { Success: true } single)
            {
                var name = RequireParameter(file, single.Groups[1].Value, isLast, catchAll: false, parameters);
                parameters.Add(new RouteParameter(name, RouteParameterKind.Single));
                templateSegments.Add($"{{{name}}}");
            }
            else if (segment.IndexOfAny(['{', '}', '[', ']', '*', '?']) >= 0 || segment.Length == 0)
            {
                throw new InvalidPageFileException(
                    $"'{file}': segment '{segment}' contains characters that are not allowed in a static URL segment.");
            }
            else
            {
                templateSegments.Add(segment);
            }
        }

        return new PageRoute(
            Pattern: "/" + string.Join('/', urlSegments),
            Template: "/" + string.Join('/', templateSegments),
            File: file,
            Segments: routeSegments,
            Parameters: parameters);
    }

    private static string RequireParameter(
        string file, string name, bool isLast, bool catchAll, List<RouteParameter> existing)
    {
        if (catchAll && !isLast)
        {
            throw new InvalidPageFileException($"'{file}': a catch-all segment must be the last segment.");
        }

        if (!ParameterNameRegex().IsMatch(name))
        {
            throw new InvalidPageFileException(
                $"'{file}': '{name}' is not a valid parameter name (letters, digits and underscores, not starting with a digit).");
        }

        if (existing.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidPageFileException($"'{file}': parameter '{name}' is used more than once.");
        }

        return name;
    }

    // Two routes conflict when they match the same URLs, regardless of parameter names.
    private static string ShapeOf(PageRoute route) => ShapeOf(route.Pattern);

    private static string ShapeOf(string pattern) =>
        string.Join('/', pattern.Split('/').Select(segment => segment switch
        {
            _ when OptionalCatchAllRegex().IsMatch(segment) => "[[...]]",
            _ when CatchAllRegex().IsMatch(segment) => "[...]",
            _ when SingleRegex().IsMatch(segment) => "[]",
            _ => segment,
        }));

    private static bool IsGroup(string segment) => segment.Length > 2 && segment[0] == '(' && segment[^1] == ')';

    [GeneratedRegex(@"^\[\[\.\.\.([^\]]*)\]\]$")]
    private static partial Regex OptionalCatchAllRegex();

    [GeneratedRegex(@"^\[\.\.\.([^\]]*)\]$")]
    private static partial Regex CatchAllRegex();

    [GeneratedRegex(@"^\[([^\].]*)\]$")]
    private static partial Regex SingleRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex ParameterNameRegex();
}
