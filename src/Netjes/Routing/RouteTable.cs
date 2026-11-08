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
/// <param name="File">
/// The file's id: its module path relative to the app root, e.g. <c>app/blog/[slug]/page.tsx</c>, or for a page
/// package's file its npm name and its path in the package, e.g. <c>@acme/help-center/app/page.tsx</c>.
/// </param>
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

    /// <summary>Where <see cref="File"/> comes from: <c>app</c>, or the npm name of a page package.</summary>
    public string Source { get; init; } = PageSource.AppName;

    /// <summary>The page package's page that the app's <see cref="File"/> replaces (<c>.Override()</c>); null when none.</summary>
    public string? Overrides { get; init; }

    public bool IsFromTheApp => Source == PageSource.AppName;
}

/// <summary>An app page and a page package's page of one shape, which an override on the mount can still resolve.</summary>
/// <param name="Pattern">The package page's URL pattern, which the table holds.</param>
internal sealed record PendingConflict(string Pattern, string AppFile, string PackageFile, string Message)
{
    public RouteConflictException ToException() => new(Message);
}

/// <summary>A page that is rendered for a condition rather than a URL (404, error).</summary>
internal sealed record SpecialPage(string File, IReadOnlyList<RouteSegment> Segments)
{
    public IReadOnlyList<string> Layouts { get; } = [.. Segments.Where(s => s.Layout is not null).Select(s => s.Layout!)];
}

/// <summary>
/// Two pages in <c>app/</c> of one shape, which match the same URLs whatever their parameters are called, or two files
/// for one convention in one folder, also when one of them comes from a page package; the message names both. Also an
/// override on a page package's mount that does not fit the files.
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

    /// <summary>
    /// App pages of the same shape as a page package's page, in order of the URL. The package's page is in the table;
    /// <c>MapPages()</c> throws the first conflict left, unless the mount's <c>.Override()</c> resolved it.
    /// </summary>
    public IReadOnlyList<PendingConflict> PendingConflicts { get; private init; } = [];

    /// <summary>The pending conflict over the URL <paramref name="pattern"/>, matched by shape; null when none.</summary>
    public PendingConflict? PendingConflictFor(string pattern) =>
        PendingConflicts.FirstOrDefault(c => string.Equals(ShapeOf(c.Pattern), ShapeOf(pattern), StringComparison.OrdinalIgnoreCase));

    /// <summary>Every intercepting route in the app.</summary>
    public IReadOnlyList<RouteIntercept> Intercepts { get; private init; } = [];

    /// <summary>The intercepting routes that can show <paramref name="pattern"/> in a slot instead.</summary>
    /// <remarks>Matched by shape: <c>(.)photo/[photoId]</c> intercepts <c>/photo/[id]</c>.</remarks>
    public IReadOnlyList<RouteIntercept> InterceptsFor(string pattern) =>
        [.. Intercepts.Where(i => string.Equals(ShapeOf(i.Pattern), ShapeOf(pattern), StringComparison.OrdinalIgnoreCase))];

    public static RouteTable Build(IEnumerable<string> files, string appDirectory = "app") =>
        Build([PageSource.App(files, appDirectory)], []);

    /// <summary>
    /// Merges the app's directory and the page packages mounted in it into one table, by folder: a package's files go
    /// under its prefix's folders, so its layout at the mount wraps the app's pages in that folder too, and a package's
    /// route group wraps the app's pages in a folder of that group.
    /// </summary>
    /// <param name="sources">The app's source and one per mounted package, in any order.</param>
    /// <param name="overrides">App-absolute URL patterns where the app's page replaces a package's page.</param>
    public static RouteTable Build(IReadOnlyList<PageSource> sources, IReadOnlyCollection<string> overrides)
    {
        RequireValid(sources);
        var root = sources.Single(s => s.IsApp).IdPrefix.TrimEnd('/');
        // In ordinal order, so the same files give the same table and the same error, whichever order the file system
        // (development) or the build manifest (production) lists them in, and whichever order the sources come in.
        var candidates = sources.OrderBy(s => s.Name, StringComparer.Ordinal).SelectMany(Candidates).OrderBy(c => c.File, StringComparer.Ordinal).ToList();
        var app = BuildAppRouter(candidates, root);

        // Pages of one shape match the same URLs. Within one source, or between two packages, that stops the table.
        // An app page and a package page of one shape are held until the overrides are applied.
        var byShape = new Dictionary<string, List<PageRoute>>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in app.Routes)
        {
            var shape = ShapeOf(route);
            if (!byShape.TryGetValue(shape, out var same))
            {
                byShape[shape] = same = [];
            }

            foreach (var existing in same.Where(e => e.Source == route.Source || (!e.IsFromTheApp && !route.IsFromTheApp)))
            {
                // Conflict names the files in ordinal order; the URL is that of the one named first.
                var first = string.CompareOrdinal(existing.File, route.File) < 0 ? existing : route;
                throw Conflict(existing.File, existing.Source, route.File, route.Source, $"both match the URL '{first.Pattern}'");
            }

            same.Add(route);
        }

        var overridden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in overrides.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var same = byShape.GetValueOrDefault(ShapeOf(pattern)) ?? [];
            var package = same.FirstOrDefault(r => !r.IsFromTheApp) ?? throw new RouteConflictException(
                $".Override(\"{pattern}\"): no page package has a page at '{pattern}'. Remove the override, or give it the URL " +
                "pattern of one of the package's pages.");
            var page = same.FirstOrDefault(r => r.IsFromTheApp) ?? throw new RouteConflictException(
                $".Override(\"{pattern}\") replaces '{package.File}', but the app has no page at '{pattern}' " +
                $"({FolderOf(sources, package)}/page.tsx). Add the app's page, or remove the override.");
            if (!ParameterNames(pattern).SequenceEqual(package.Parameters.Select(p => p.Name), StringComparer.Ordinal)
                || !page.Parameters.Select(p => p.Name).SequenceEqual(package.Parameters.Select(p => p.Name), StringComparer.Ordinal))
            {
                throw new RouteConflictException(
                    $".Override(\"{pattern}\"): '{page.File}', '{package.File}' ('{package.Pattern}') and the override must name " +
                    "their parameters alike, since the app's page renders with the package's loader.");
            }

            overridden.Add(ShapeOf(pattern));
        }

        var routes = new List<PageRoute>();
        var pending = new List<PendingConflict>();
        foreach (var (shape, same) in byShape)
        {
            if (same is not [var one, var other])
            {
                routes.AddRange(same);
                continue;
            }

            var (page, package) = one.IsFromTheApp ? (one, other) : (other, one);
            if (overridden.Contains(shape))
            {
                // The app's file, in the package's route: the URL is spelled as the package spells it, so the mount's
                // loader finds it.
                routes.Add(page with { Pattern = package.Pattern, Template = package.Template, Overrides = package.File });
                continue;
            }

            routes.Add(package);
            var (first, second) = string.CompareOrdinal(page.File, package.File) < 0 ? (page.File, package.File) : (package.File, page.File);
            pending.Add(new PendingConflict(
                package.Pattern,
                page.File,
                package.File,
                $"'{first}' and '{second}' both match the URL '{package.Pattern}'. Rename or remove the app's page, or replace " +
                $"the package's page with .Override(\"{package.Pattern}\") on its mount."));
        }

        routes.Sort((a, b) => string.CompareOrdinal(a.Pattern, b.Pattern));
        pending.Sort((a, b) => string.CompareOrdinal(a.Pattern, b.Pattern));
        return new RouteTable(routes, app.NotFound, app.Error, app.GlobalError, app.RootSegments)
        {
            Intercepts = [.. (app.Intercepts ?? []).OrderBy(i => i.Segment, StringComparer.Ordinal).ThenBy(i => i.Slot, StringComparer.Ordinal)],
            PendingConflicts = pending,
        };
    }

    // The sources' names and id prefixes keep every file id unique and tell the app's files from a package's.
    private static void RequireValid(IReadOnlyList<PageSource> sources)
    {
        var apps = sources.Where(s => s.IsApp).ToList();
        if (apps is not [var app])
        {
            throw new ArgumentException($"A route table has one source for the app's own pages, not {apps.Count}.", nameof(sources));
        }

        if (app.Folder.Length > 0)
        {
            throw new ArgumentException($"The app's pages are not mounted at a prefix ('{app.Folder}').", nameof(sources));
        }

        var appDirectory = app.IdPrefix.TrimEnd('/');
        foreach (var source in sources.Where(s => !s.IsApp).OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            if (!NpmNameRegex().IsMatch(source.Name))
            {
                throw new ArgumentException($"'{source.Name}' is not an npm package name, which is what a page package goes by.", nameof(sources));
            }

            if (source.Name.Equals(PageSource.AppName, StringComparison.OrdinalIgnoreCase)
                || source.Name.Equals(appDirectory, StringComparison.OrdinalIgnoreCase)
                || appDirectory.StartsWith(source.Name + "/", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"A page package cannot be named '{source.Name}': its file ids would read as the app's files, which are in " +
                    $"'{appDirectory}'.", nameof(sources));
            }

            var pages = source.IdPrefix.StartsWith(source.Name + "/", StringComparison.Ordinal) && source.IdPrefix.EndsWith('/')
                ? source.IdPrefix[(source.Name.Length + 1)..^1]
                : "";
            if (!IsPathInside(pages))
            {
                throw new ArgumentException(
                    $"'{source.IdPrefix}' is not the id prefix of the page package '{source.Name}': its name, then its pages " +
                    "folder, a relative path inside the package without '.', '..' or empty segments, '\\' or ':', then '/'.",
                    nameof(sources));
            }

            MountPrefix.Parse("/" + source.Folder);
        }

        if (sources.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            throw new ArgumentException($"'{twice.Key}' is in the route table more than once.", nameof(sources));
        }
    }

    // A file id of a package is its id prefix and a path inside its pages folder.
    private static void RequireInside(PageSource source, string file)
    {
        if (!file.StartsWith(source.IdPrefix, StringComparison.Ordinal) || !IsPathInside(file[source.IdPrefix.Length..]))
        {
            throw new InvalidPageFileException(
                $"'{file}' is no file of the page package '{source.Name}': a file id is '{source.IdPrefix}' followed by a " +
                "path inside the package's pages folder, without '.', '..' or empty segments, '\\' or ':'.");
        }
    }

    private static bool IsPathInside(string path) =>
        path.Split('/').All(segment => segment is not ("" or "." or "..") && segment.IndexOfAny(['\\', ':']) < 0);

    /// <summary>The merged folder of a package's page, as the app's directory spells it: <c>app/help/contact</c>.</summary>
    private static string FolderOf(IReadOnlyList<PageSource> sources, PageRoute route)
    {
        var source = sources.First(s => s.Name == route.Source);
        var file = route.File[source.IdPrefix.Length..];
        var app = sources.First(s => s.IsApp).IdPrefix.TrimEnd('/');
        return string.Join('/', new[] { app, source.Folder, file.Contains('/') ? file[..file.LastIndexOf('/')] : "" }.Where(p => p.Length > 0));
    }

    private static IEnumerable<string> ParameterNames(string pattern) => pattern.Split('/').Select(ParameterName).OfType<string>();

    private sealed record RouterResult(
        List<PageRoute> Routes,
        SpecialPage? NotFound,
        SpecialPage? Error,
        SpecialPage? GlobalError,
        IReadOnlyList<RouteSegment> RootSegments,
        IReadOnlyList<RouteIntercept>? Intercepts = null);

    /// <summary>A file of one source, with its folders in the merged tree and its name last.</summary>
    private sealed record Candidate(string File, string[] Segments, PageSource Source);

    private static IEnumerable<Candidate> Candidates(PageSource source)
    {
        if (!source.IsApp)
        {
            foreach (var file in source.Files.Order(StringComparer.Ordinal))
            {
                RequireInside(source, file);
            }
        }

        var files = source.IsApp ? source.Files.Select(f => f.Replace('\\', '/')) : source.Files;
        string[] folder = source.Folder.Length == 0 ? [] : source.Folder.Split('/');
        return files
            .Distinct(StringComparer.Ordinal)
            .Where(f => f.StartsWith(source.IdPrefix, StringComparison.Ordinal))
            .Where(f => !IsTestFile(f))
            .Select(f => new Candidate(f, [.. folder, .. f[source.IdPrefix.Length..^Path.GetExtension(f).Length].Split('/')], source));
    }

    private static RouterResult BuildAppRouter(List<Candidate> candidates, string root)
    {
        // Convention files by folder (relative to the app directory); everything else is colocated code.
        var folders = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        // Parallel routes: folder -> slot -> (pages by pattern below the slot, default).
        var slots = new Dictionary<string, Dictionary<string, (Dictionary<string, string> Pages, string? Default)>>(StringComparer.Ordinal);
        var intercepts = new List<RouteIntercept>();
        // Page files by folder, one per source.
        var pages = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        // The source of each file.
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        // The folders of each source's convention files, and the folders above them.
        var spelled = new HashSet<(string Folder, string Source)>();
        foreach (var (file, segments, source) in candidates)
        {
            sources[file] = source.Name;
            var name = segments[^1];
            var directories = segments[..^1];
            if (IsPrivate(directories))
            {
                continue;
            }

            // A file the table routes: its folders merge with the other sources' folders, and a package's must not
            // name the mount prefix's parameters again.
            void Accept()
            {
                for (var depth = 1; depth <= directories.Length; depth++)
                {
                    spelled.Add((string.Join('/', directories[..depth]), source.Name));
                }

                if (!source.IsApp && ReusedPrefixParameter(source, directories) is { } reused)
                {
                    throw new InvalidPageFileException(
                        $"'{file}': parameter '{reused}' is used more than once: the mount prefix '/{source.Folder}' has it too. " +
                        "Give the prefix's parameter another name.");
                }
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

                Accept();
                var slotName = directories[slotIndex][1..];
                if (!bySlot.TryGetValue(slotName, out var slot))
                {
                    bySlot[slotName] = slot = (new Dictionary<string, string>(StringComparer.Ordinal), null);
                }

                var below = directories[(slotIndex + 1)..];
                var slotFolder = $"{root}/{string.Join('/', directories[..(slotIndex + 1)])}";
                if (name == "default")
                {
                    if (below.Length == 0)
                    {
                        if (slot.Default is { } other)
                        {
                            throw Conflict(other, sources[other], file, source.Name, $"are both the default of {slotFolder}");
                        }

                        bySlot[slotName] = slot with { Default = file };
                    }
                }
                else if (below.Length > 0 && InterceptMarker(below[0]) is var (levels, rest) && levels >= 0)
                {
                    // (.) the slot's own level, (..) one up, (...) the root: the URL this page intercepts.
                    var baseUrl = directories[..slotIndex].Where(d => !IsGroup(d) && !(d.Length > 1 && d[0] == '@')).ToList();
                    if (!source.IsApp)
                    {
                        // A package intercepts only its own URLs, below the mount, wherever the app mounts it.
                        var mounted = source.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Count(d => !IsGroup(d));
                        if (levels == int.MaxValue ? mounted > 0 : levels > baseUrl.Count - mounted)
                        {
                            throw new InvalidPageFileException(
                                $"'{file}': this intercepting route climbs above the mount prefix '/{source.Folder}'. A page " +
                                "package can intercept only the URLs below its mount.");
                        }
                    }

                    var kept = levels == int.MaxValue ? [] : baseUrl.Take(Math.Max(0, baseUrl.Count - levels)).ToList();
                    var target = "/" + string.Join('/', kept.Append(rest).Concat(below[1..].Where(d => !IsGroup(d))).Where(d => d.Length > 0));
                    var intercept = new RouteIntercept(SegmentOf(root, directories[..slotIndex]).Id, slotName, file, target);
                    // The client shows the first intercepting route whose segment the page is in, so two sources'
                    // routes for one URL on one chain of segments would leave one of them unseen.
                    if (intercepts.FirstOrDefault(i => sources[i.Page] != source.Name
                            && string.Equals(ShapeOf(i.Pattern), ShapeOf(target), StringComparison.OrdinalIgnoreCase)
                            && OnOneChain(i.Segment, intercept.Segment)) is { } clash)
                    {
                        var first = string.CompareOrdinal(clash.Page, file) < 0 ? clash : intercept;
                        var inner = clash.Segment.Length > intercept.Segment.Length ? clash.Segment : intercept.Segment;
                        throw Conflict(clash.Page, sources[clash.Page], file, source.Name, $"both intercept the URL '{first.Pattern}' from the pages in {inner}");
                    }

                    intercepts.Add(intercept);
                }
                else
                {
                    var pattern = "/" + string.Join('/', below.Where(d => !IsGroup(d)));
                    if (slot.Pages.TryGetValue(pattern, out var other))
                    {
                        throw Conflict(other, sources[other], file, source.Name, $"both match the URL '{pattern}' in {slotFolder}");
                    }

                    // Between sources by shape too: the slot shows the first of its best-ranked pages, whatever their
                    // parameters are called.
                    if (slot.Pages.FirstOrDefault(p => sources[p.Value] != source.Name
                            && string.Equals(ShapeOf(p.Key), ShapeOf(pattern), StringComparison.OrdinalIgnoreCase)) is { Value: { } shaped } same)
                    {
                        var url = string.CompareOrdinal(shaped, file) < 0 ? same.Key : pattern;
                        throw Conflict(shaped, sources[shaped], file, source.Name, $"both match the URL '{url}' in {slotFolder}");
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

            if (name == "global-error" && !source.IsApp)
            {
                throw new InvalidPageFileException(
                    $"'{file}': a page package cannot have a global-error file. The app's own ({root}/global-error) renders when " +
                    "its root layout fails.");
            }

            if (name == "global-error" && directories.Length > 0)
            {
                throw new InvalidPageFileException($"'{file}': global-error is only supported at the app root ({root}/global-error).");
            }

            Accept();
            var key = string.Join('/', directories);
            if (!folders.TryGetValue(key, out var conventions))
            {
                folders[key] = conventions = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var existing = name == "page"
                ? pages.GetValueOrDefault(key)?.FirstOrDefault(p => sources[p] == source.Name)
                : conventions.GetValueOrDefault(name);
            if (existing is not null)
            {
                throw Conflict(existing, sources[existing], file, source.Name, $"are both the {name} of {(key.Length == 0 ? root : $"{root}/{key}")}");
            }

            if (name == "page")
            {
                // An app page and a package page in one folder are of one shape: Build holds or resolves the conflict.
                if (!pages.TryGetValue(key, out var inFolder))
                {
                    pages[key] = inFolder = [];
                }

                inFolder.Add(file);
            }
            else
            {
                conventions[name] = file;
            }
        }

        // Folders of two sources that differ only in case would not merge, while their URLs match.
        var spellings = new Dictionary<string, List<(string Folder, string Source)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, source) in spelled.OrderBy(f => f.Folder, StringComparer.Ordinal).ThenBy(f => f.Source, StringComparer.Ordinal))
        {
            if (!spellings.TryGetValue(folder, out var seen))
            {
                spellings[folder] = seen = [];
            }

            if (seen.FirstOrDefault(s => s.Folder != folder && s.Source != source) is ({ } other, { } otherSource))
            {
                static string From(string name) => name == PageSource.AppName ? "the app" : $"'{name}'";
                throw new RouteConflictException(
                    $"'{root}/{other}' (from {From(otherSource)}) and '{root}/{folder}' (from {From(source)}) differ only in case, " +
                    "so their files would not merge while their URLs match. Spell the folders alike, or mount the package at another prefix.");
            }

            seen.Add((folder, source));
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
        foreach (var folder in folders.Keys)
        {
            foreach (var page in pages.GetValueOrDefault(folder) ?? [])
            {
                var directories = folder.Length == 0 ? [] : folder.Split('/');
                routes.Add(ParseRoute(page, [.. directories], SegmentsFor(directories)) with { Source = sources[page] });
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
    // Between sources, the fix is the app's, or the mount's: a package's files are not the app's to rename.
    private static RouteConflictException Conflict(string one, string oneSource, string another, string anotherSource, string what)
    {
        var (first, second) = string.CompareOrdinal(one, another) < 0 ? (one, another) : (another, one);
        var fix = oneSource == anotherSource ? "Rename or remove one of them."
            : oneSource == PageSource.AppName || anotherSource == PageSource.AppName ? "Remove the app's file, or mount the package at another prefix."
            : "Mount one of them at another prefix.";
        return new RouteConflictException($"'{first}' and '{second}' {what}. {fix}");
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

    /// <summary>A parameter of a package's folders that its mount prefix has too; null when none.</summary>
    private static string? ReusedPrefixParameter(PageSource source, string[] directories)
    {
        var mounted = source.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var prefix = mounted.Select(ParameterName).OfType<string>().ToList();
        return directories[mounted.Length..].Select(ParameterName).OfType<string>()
            .FirstOrDefault(name => prefix.Contains(name, StringComparer.OrdinalIgnoreCase));
    }

    private static string? ParameterName(string segment) =>
        OptionalCatchAllRegex().Match(segment) is { Success: true } o ? o.Groups[1].Value
        : CatchAllRegex().Match(segment) is { Success: true } c ? c.Groups[1].Value
        : SingleRegex().Match(segment) is { Success: true } p ? p.Groups[1].Value
        : null;

    /// <summary>Whether one segment is the other or holds it, so a page in the inner one is in both.</summary>
    private static bool OnOneChain(string segment, string another) =>
        segment == another || another.StartsWith(segment + "/", StringComparison.Ordinal) || segment.StartsWith(another + "/", StringComparison.Ordinal);

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

    // npm's rules, with the capitals that older names have.
    [GeneratedRegex("^(?:@[A-Za-z0-9~-][A-Za-z0-9._~-]*/)?[A-Za-z0-9~-][A-Za-z0-9._~-]*$")]
    private static partial Regex NpmNameRegex();
}
