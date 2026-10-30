using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netjes.Rendering;

namespace Netjes.Routing;

/// <summary>
/// A warning about the app's routes, logged at startup and listed by <c>netjes routes</c>. <see cref="Template"/> is
/// an <see cref="ILogger"/> message template; <see cref="Arguments"/> fill its holes in order.
/// </summary>
/// <param name="Kind">
/// <c>query-binding</c>: a loader parameter binds from the query string next to an unbound route parameter, or a
/// layout loader's, named after a route parameter of a page below the layout.
/// <c>overlap</c>: two pages match the same URLs and route precedence picks one.
/// <c>form-input</c>: an action binds a <c>[FromForm]</c> positional record with a field a browser leaves out of a post.
/// </param>
/// <param name="Files">
/// The files the warning is about: the loader's page or layout, the winning page and the other, or a page action's page
/// (none for a server action).
/// </param>
internal sealed partial record RouteWarning(string Kind, IReadOnlyList<string> Files, string Template, object?[] Arguments)
{
    public string Message
    {
        get
        {
            var index = 0;
            return HoleRegex().Replace(Template, _ => Convert.ToString(Arguments[index++], System.Globalization.CultureInfo.InvariantCulture) ?? "");
        }
    }

    [GeneratedRegex(@"\{[A-Za-z]+\}")]
    private static partial Regex HoleRegex();
}

/// <summary>
/// Checks what the matcher and minimal API binding would do quietly: a loader parameter that binds from the query
/// string while a route parameter of its pattern goes unbound, a layout loader parameter that binds from the query
/// string though a page below has a route parameter of its name, two pages that match the same URLs, where route
/// precedence picks one, and an action's form input that form binding refuses when a checkbox is left unchecked.
/// </summary>
internal sealed class RouteDiagnostics(IServiceProvider services, ILogger<RouteDiagnostics> logger)
{
    // {Parameters} and {RouteParameters} are lists of quoted names: 'sort', 'page' and 'q'.
    private const string QueryBinding =
        "The loader of {File} binds {Parameters} from the query string, and route parameter '{RouteParameter}' of {Pattern} is bound by no parameter. " +
        "Name a C# parameter after the route parameter (or use [FromRoute]) to bind the route value, or use [FromQuery] where the query string is meant.";

    private const string QueryBindingRoutes =
        "The loader of {File} binds {Parameters} from the query string, and route parameters {RouteParameters} of {Pattern} are bound by no parameter. " +
        "Name C# parameters after the route parameters (or use [FromRoute]) to bind the route values, or use [FromQuery] where the query string is meant.";

    private const string QueryBindingNearName =
        "The loader of {File} binds '{Parameter}' from the query string, not route parameter '{RouteParameter}' of {Pattern}, which no parameter binds. " +
        "Rename the C# parameter to match the route parameter to bind the route value, or mark it [FromQuery] if the query string is meant.";

    private const string QueryBindingArray =
        "The loader of {File} binds the array '{Parameter}' from the query string, not route parameter '{RouteParameter}' of {Pattern}: route values bind to single values. " +
        "Take a string to bind the route value (a catch-all's segments joined by '/'), or mark it [FromQuery] if the query string is meant.";

    private const string QueryBindingBelow =
        "The loader of {File} binds '{Parameter}' from the query string, not route parameter '{RouteParameter}' of {Pattern} below it: a layout loader binds only the route values of its own URL, {Segment}. " +
        "Read the route value in the page's loader, or mark the parameter [FromQuery] if the query string is meant.";

    private const string Overlap =
        "{Winner} wins over {Loser} for URLs like {Url}: both match them, and ASP.NET Core's route precedence decides. " +
        "Rename or move one of the pages if the other should get them.";

    // {Fields} is a list of quoted constructor parameter names: 'Pinned' and 'Tags'.
    private const string FormInput =
        "{Action} binds {Type} from the form through its constructor, and form binding refuses a post without one of the constructor's fields with 400. " +
        "A browser leaves out {Fields} when nothing is checked or chosen. Give the type init properties instead, which a missing field leaves at its default.";

    /// <summary>Every warning for the current routes; never throws (a failing check is logged and skipped).</summary>
    public IReadOnlyList<RouteWarning> Check()
    {
        try
        {
            var table = services.GetRequiredService<IRouteTableProvider>().Current;
            var all = services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
            var endpoints = PageEndpoints(all);
            var layouts = LayoutBindings();
            var boundBySegment = layouts.ToDictionary(
                l => l.Loader.Segment.Id,
                l => LoaderBinding.RouteParametersBound(l.Parameters, l.Loader.Segment.Parameters).ToList(),
                StringComparer.Ordinal);
            return
            [
                .. PageLoaderWarnings(table, endpoints, boundBySegment),
                .. LayoutLoaderWarnings(table, layouts, boundBySegment),
                .. OverlapWarnings(table, endpoints),
                .. FormInputWarnings(table, all),
            ];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Netjes could not check the routes: {Message}", ex.Message);
            return [];
        }
    }

    // The template is one of the constants above, so log sinks keep the file, parameter and pattern as fields.
    public void Log(RouteWarning warning) => logger.LogWarning(warning.Template, warning.Arguments);

    /// <summary>The endpoint that answers GET for each page, by pattern: its loader's, or the file data source's.</summary>
    private static Dictionary<string, RouteEndpoint> PageEndpoints(IEnumerable<RouteEndpoint> endpoints)
    {
        var pages = new Dictionary<string, RouteEndpoint>(StringComparer.Ordinal);
        foreach (var endpoint in endpoints)
        {
            // The not-found fallback has no pattern.
            if (endpoint.Metadata.GetMetadata<PageEndpointMetadata>() is { Pattern: { } pattern }
                && (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(HttpMethods.Get) ?? false))
            {
                pages.TryAdd(pattern, endpoint);
            }
        }

        return pages;
    }

    /// <param name="boundBySegment">The route parameters each layout loader binds, by segment: bound for every page below.</param>
    private static IEnumerable<RouteWarning> PageLoaderWarnings(
        RouteTable table, Dictionary<string, RouteEndpoint> endpoints, Dictionary<string, List<string>> boundBySegment)
    {
        foreach (var page in table.Pages)
        {
            if (endpoints.GetValueOrDefault(page.Pattern) is not { } endpoint || endpoint.Metadata.GetMetadata<MethodInfo>() is null)
            {
                continue;
            }

            // A page mapped in a route group (app.MapGroup("/{tenant}")) has the group's route parameters too, and its
            // URL is named as mapped, with the group's prefix.
            var routeParameters = endpoint.RoutePattern.Parameters.Select(p => p.Name).ToList();
            var url = endpoint.RoutePattern.RawText is { } mapped && mapped != page.Template ? mapped : page.Pattern;
            var boundAbove = page.Segments.SelectMany(s => boundBySegment.GetValueOrDefault(s.Id) ?? []);
            foreach (var warning in QueryBindings(page.File, url, endpoint.Metadata.GetMetadata<MethodInfo>()!,
                         endpoint.Metadata.OfType<IParameterBindingMetadata>(), routeParameters, boundAbove))
            {
                yield return warning;
            }
        }
    }

    /// <summary>A layout loader with the binding minimal APIs infer for it.</summary>
    private sealed record LayoutBinding(LayoutLoader Loader, MethodInfo Method, IReadOnlyList<IParameterBindingMetadata> Parameters);

    /// <summary>Each layout loader with the binding minimal APIs infer for it.</summary>
    private List<LayoutBinding> LayoutBindings()
    {
        var layouts = new List<LayoutBinding>();
        foreach (var loader in services.GetRequiredService<LayoutLoaderRegistry>().All)
        {
            try
            {
                var (method, parameters) = loader.InferParameterBindings();
                layouts.Add(new LayoutBinding(loader, method, parameters));
            }
            catch (Exception ex)
            {
                // MapLayoutLoader refuses a loader whose binding minimal APIs refuse; a check never stops the app.
                logger.LogDebug(ex, "Skipped the route check of the layout loader for {Segment}", loader.Segment.Id);
            }
        }

        return layouts;
    }

    private static IEnumerable<RouteWarning> LayoutLoaderWarnings(
        RouteTable table, List<LayoutBinding> layouts, Dictionary<string, List<string>> boundBySegment)
    {
        foreach (var (loader, method, parameters) in layouts)
        {
            var segment = loader.Segment;
            // A query parameter named after a route parameter below is meant for it, and for none of the layout's own.
            var meantForBelow = BindingsMeantForBelow(table, segment, method, parameters);
            foreach (var (parameter, route, pattern) in meantForBelow)
            {
                yield return new RouteWarning("query-binding", [segment.Layout!], QueryBindingBelow,
                    [segment.Layout!, parameter.Name, route, pattern, segment.Pattern]);
            }

            // Segment ids are folders (app/[team]/[project]), so the ones above are the prefixes ending at a slash.
            var boundAbove = boundBySegment.Where(s => segment.Id.StartsWith(s.Key + "/", StringComparison.Ordinal)).SelectMany(s => s.Value);
            var others = parameters.Except(meantForBelow.Select(m => m.Parameter));
            foreach (var warning in QueryBindings(segment.Layout!, segment.Pattern, method, others, segment.Parameters, boundAbove))
            {
                yield return warning;
            }
        }
    }

    /// <summary>
    /// The layout loader's parameters that bind from the query string under the name, in any case, of a route parameter
    /// that a page below the layout has and the layout's URL does not, with that page's pattern (the first in order).
    /// </summary>
    private static List<(IParameterBindingMetadata Parameter, string Route, string Pattern)> BindingsMeantForBelow(
        RouteTable table, RouteSegment segment, MethodInfo loader, IReadOnlyList<IParameterBindingMetadata> parameters)
    {
        var below = new Dictionary<string, (string Route, string Pattern)>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in table.Pages.Where(p => p.Segments.Any(s => s.Id == segment.Id)))
        {
            foreach (var route in page.Parameters.Where(r => !segment.Parameters.Contains(r.Name, StringComparer.OrdinalIgnoreCase)))
            {
                below.TryAdd(route.Name, (route.Name, page.Pattern));
            }
        }

        // [AsParameters] members are left out, as in the other warnings.
        return
        [
            .. parameters
                .Where(p => p.ParameterInfo.Member.Equals(loader) && LoaderBinding.SourceOf(p, segment.Parameters).Query)
                .Select(p => (Parameter: p, Below: below.GetValueOrDefault(p.Name)))
                .Where(b => b.Below.Route is not null)
                .Select(b => (b.Parameter, b.Below.Route, b.Below.Pattern)),
        ];
    }

    /// <summary>
    /// Loader parameters that bind from the query string while a route parameter of the pattern is bound by none, nor
    /// by a layout loader above (<paramref name="boundAbove"/>). When some of them have a near name of such a route
    /// parameter (a case-only difference, or two edits or fewer), only those are named: the others are ordinary query
    /// parameters.
    /// </summary>
    private static IEnumerable<RouteWarning> QueryBindings(
        string file,
        string pattern,
        MethodInfo loader,
        IEnumerable<IParameterBindingMetadata> parameters,
        IReadOnlyList<string> routeParameters,
        IEnumerable<string> boundAbove)
    {
        var bindings = parameters.Select(p => (Parameter: p, Source: LoaderBinding.SourceOf(p, routeParameters))).ToList();
        var bound = bindings.Where(b => b.Source.Route is not null).Select(b => b.Source.Route!)
            .Concat(boundAbove)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unbound = routeParameters.Where(r => !bound.Contains(r)).ToList();
        // [AsParameters] members bind routes like parameters, but are left out of the warnings.
        var query = bindings.Where(b => b.Source.Query && b.Parameter.ParameterInfo.Member.Equals(loader)).Select(b => b.Parameter).ToList();
        if (unbound.Count == 0 || query.Count == 0)
        {
            yield break;
        }

        var near = query
            .Select(p => (Parameter: p, Route: unbound.MinBy(r => Distance(p.Name, r))!))
            .Where(n => Distance(n.Parameter.Name, n.Route) <= 2)
            .ToList();
        if (near.Count == 0)
        {
            // Any of them could be the one meant for the route: one warning names them all.
            var names = Quoted(query.Select(p => p.Name));
            yield return unbound.Count == 1
                ? new RouteWarning("query-binding", [file], QueryBinding, [file, names, unbound[0], pattern])
                : new RouteWarning("query-binding", [file], QueryBindingRoutes, [file, names, Quoted(unbound), pattern]);
            yield break;
        }

        foreach (var (parameter, route) in near)
        {
            var template = string.Equals(parameter.Name, route, StringComparison.OrdinalIgnoreCase) ? QueryBindingArray : QueryBindingNearName;
            yield return new RouteWarning("query-binding", [file], template, [file, parameter.Name, route, pattern]);
        }
    }

    /// <summary>
    /// Pairs of pages that can match the same URL, except where one is the other with literals for some of its
    /// parameters (<c>/blog/new</c> next to <c>/blog/[slug]</c>): the literal winning is the convention, as in Next.js.
    /// The winner is the endpoint the matcher prefers: lower order, then lower route precedence.
    /// </summary>
    private static IEnumerable<RouteWarning> OverlapWarnings(RouteTable table, Dictionary<string, RouteEndpoint> endpoints)
    {
        var pages = table.Pages.Select(page =>
        {
            var endpoint = endpoints.GetValueOrDefault(page.Pattern);
            var rank = (endpoint?.Order ?? 0, (endpoint?.RoutePattern ?? RoutePatternFactory.Parse(page.Template)).InboundPrecedence);
            return (page.File, Url: UrlPattern.Parse(page.Pattern), Rank: rank);
        }).ToList();

        for (var i = 0; i < pages.Count; i++)
        {
            for (var j = i + 1; j < pages.Count; j++)
            {
                if (!UrlSegment.Overlap(pages[i].Url, pages[j].Url))
                {
                    continue;
                }

                var (winner, loser) = pages[i].Rank.CompareTo(pages[j].Rank) <= 0 ? (pages[i], pages[j]) : (pages[j], pages[i]);
                if (UrlSegment.IsLiteralException(winner.Url, loser.Url))
                {
                    continue;
                }

                yield return new RouteWarning("overlap", [winner.File, loser.File], Overlap,
                    [winner.File, loser.File, UrlSegment.Format(UrlSegment.Intersection(winner.Url.Segments, loser.Url.Segments))]);
            }
        }
    }

    /// <summary>
    /// Server actions and page actions that bind a <c>[FromForm]</c> positional record (a class or record built through
    /// its only constructor) whose constructor takes a field a browser leaves out of a post: a checkbox (<c>bool</c>) left
    /// unchecked, or a list with nothing checked or chosen. Form binding refuses such a post (400) before the action runs.
    /// Text fields are not named: a browser posts them even when empty.
    /// </summary>
    private static IEnumerable<RouteWarning> FormInputWarnings(RouteTable table, IEnumerable<RouteEndpoint> endpoints)
    {
        foreach (var endpoint in endpoints)
        {
            if (endpoint.Metadata.GetMetadata<MethodInfo>() is not { } method)
            {
                continue;
            }

            string action;
            string[] files;
            if (endpoint.Metadata.GetMetadata<ServerActionMetadata>() is { } server)
            {
                (action, files) = ($"The server action '{server.Name}'", []);
            }
            else if (endpoint.Metadata.GetMetadata<PageEndpointMetadata>() is { Pattern: { } pattern }
                     && (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(HttpMethods.Post) ?? false)
                     && table.Find(pattern) is { } page)
            {
                (action, files) = ($"The action of {page.File}", [page.File]);
            }
            else
            {
                continue;
            }

            foreach (var parameter in method.GetParameters().Where(EmptyFormInputs.IsFormObject))
            {
                var type = parameter.ParameterType;
                if (type.GetConstructor(Type.EmptyTypes) is not null || type.GetConstructors() is not [var constructor])
                {
                    continue;
                }

                var fields = constructor.GetParameters().Where(p => LeftOutWhenEmpty(p.ParameterType)).Select(p => p.Name!).ToList();
                if (fields.Count > 0)
                {
                    yield return new RouteWarning("form-input", files, FormInput, [action, type.Name, Quoted(fields)]);
                }
            }
        }
    }

    /// <summary>A checkbox's <c>bool</c>, or a list (an array, or any <c>IEnumerable&lt;T&gt;</c> but a string).</summary>
    private static bool LeftOutWhenEmpty(Type type) =>
        (Nullable.GetUnderlyingType(type) ?? type) == typeof(bool)
        || (type != typeof(string) && (type.IsArray || type.GetInterfaces().Append(type)
            .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))));

    /// <summary>'a', 'a' and 'b', or 'a', 'b' and 'c'.</summary>
    private static string Quoted(IEnumerable<string> names)
    {
        var quoted = names.Select(n => $"'{n}'").ToList();
        return quoted.Count == 1 ? quoted[0] : $"{string.Join(", ", quoted[..^1])} and {quoted[^1]}";
    }

    /// <summary>Levenshtein distance, ignoring case: route values match parameter names in any case.</summary>
    private static int Distance(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1], previous[j]) + 1, previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            previous = current;
        }

        return previous[b.Length];
    }
}

/// <summary>One segment of a page pattern in file syntax: a literal, <c>[x]</c>, <c>[...x]</c> or <c>[[...x]]</c>.</summary>
/// <param name="Text">The literal, or the parameter's name.</param>
internal readonly record struct UrlSegment(UrlSegmentKind Kind, string Text)
{
    public bool IsCatchAll => Kind is UrlSegmentKind.CatchAll or UrlSegmentKind.OptionalCatchAll;

    public static IReadOnlyList<UrlSegment> Parse(string pattern) =>
    [
        .. pattern.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => s switch
        {
            _ when s.StartsWith("[[...", StringComparison.Ordinal) => new UrlSegment(UrlSegmentKind.OptionalCatchAll, s[5..^2]),
            _ when s.StartsWith("[...", StringComparison.Ordinal) => new UrlSegment(UrlSegmentKind.CatchAll, s[4..^1]),
            _ when s.StartsWith('[') => new UrlSegment(UrlSegmentKind.Parameter, s[1..^1]),
            _ => new UrlSegment(UrlSegmentKind.Literal, s),
        }),
    ];

    public static string Format(IEnumerable<UrlSegment> segments) => "/" + string.Join('/', segments.Select(s => s.Kind switch
    {
        UrlSegmentKind.Parameter => $"[{s.Text}]",
        UrlSegmentKind.CatchAll => $"[...{s.Text}]",
        UrlSegmentKind.OptionalCatchAll => $"[[...{s.Text}]]",
        _ => s.Text,
    }));

    /// <summary>
    /// Whether some URL matches both patterns, as ASP.NET Core matches their templates: literals in any case, a
    /// parameter one segment, <c>[...x]</c> (<c>minlength(1)</c>) one or more, <c>[[...x]]</c> any number.
    /// </summary>
    public static bool Overlap(UrlPattern a, UrlPattern b)
    {
        var (x, xRest) = (a.Required, a.More);
        var (y, yRest) = (b.Required, b.More);
        for (var i = 0; ; i++)
        {
            if (i >= x.Count || i >= y.Count)
            {
                // Same length, or the shorter one takes any number of further segments.
                return (i >= x.Count && i >= y.Count) || (i >= x.Count ? xRest : yRest);
            }

            if (x[i] is { } l && y[i] is { } r && !string.Equals(l, r, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
    }

    /// <summary>Whether every URL that matches <paramref name="inner"/> also matches <paramref name="outer"/>.</summary>
    public static bool Covers(UrlPattern outer, UrlPattern inner)
    {
        var (o, oRest) = (outer.Required, outer.More);
        var (n, nRest) = (inner.Required, inner.More);
        for (var i = 0; i < n.Count; i++)
        {
            if (i >= o.Count)
            {
                return oRest;
            }

            if (o[i] is { } literal && !string.Equals(literal, n[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // Inner ends here, or goes on with any number of segments: outer must take exactly that.
        return o.Count == n.Count && (oRest || !nRest);
    }

    /// <summary>
    /// Whether <paramref name="winner"/> matches only URLs <paramref name="loser"/> matches too, and differs from it
    /// first by a literal where the loser has a parameter or catch-all: an exception carved out by a static segment.
    /// </summary>
    public static bool IsLiteralException(UrlPattern winner, UrlPattern loser)
    {
        if (!Covers(loser, winner))
        {
            return false;
        }

        var (w, l) = (winner.Segments, loser.Segments);
        for (var i = 0; i < w.Count; i++)
        {
            if (i >= l.Count || !Same(w[i], l[i]))
            {
                return w[i].Kind == UrlSegmentKind.Literal;
            }
        }

        return false;
    }

    /// <summary>The URLs two overlapping patterns both match, as a pattern; the first one's names where both have one.</summary>
    public static IReadOnlyList<UrlSegment> Intersection(IReadOnlyList<UrlSegment> first, IReadOnlyList<UrlSegment> second)
    {
        var result = new List<UrlSegment>();
        for (var i = 0; i < Math.Max(first.Count, second.Count); i++)
        {
            UrlSegment? a = i < first.Count ? first[i] : null;
            UrlSegment? b = i < second.Count ? second[i] : null;
            if (a?.IsCatchAll == true && b?.IsCatchAll == true)
            {
                // Both take the rest: the required one is the narrower.
                result.Add(a.Value.Kind == UrlSegmentKind.CatchAll || b.Value.Kind != UrlSegmentKind.CatchAll ? a.Value : b.Value);
                break;
            }

            if (a?.IsCatchAll == true || b?.IsCatchAll == true)
            {
                // One takes whatever the other has left.
                result.AddRange(a?.IsCatchAll == true ? second.Skip(i) : first.Skip(i));
                break;
            }

            if (a is not { } one || b is not { } two)
            {
                break;
            }

            result.Add(one.Kind == UrlSegmentKind.Literal || two.Kind != UrlSegmentKind.Literal ? one : two);
        }

        return result;
    }

    private static bool Same(UrlSegment a, UrlSegment b) => a.Kind == b.Kind && string.Equals(a.Text, b.Text, StringComparison.OrdinalIgnoreCase);

    /// <summary>One entry per segment that must be there (the literal, or null for any value), and whether more may follow.</summary>
    public static (List<string?> Required, bool More) Tokens(IReadOnlyList<UrlSegment> segments)
    {
        var required = new List<string?>();
        foreach (var segment in segments)
        {
            switch (segment.Kind)
            {
                case UrlSegmentKind.Literal:
                    required.Add(segment.Text);
                    break;
                case UrlSegmentKind.Parameter:
                    required.Add(null);
                    break;
                case UrlSegmentKind.CatchAll:
                    required.Add(null);
                    return (required, true);
                case UrlSegmentKind.OptionalCatchAll:
                    return (required, true);
            }
        }

        return (required, false);
    }
}

internal enum UrlSegmentKind { Literal, Parameter, CatchAll, OptionalCatchAll }

/// <summary>A page pattern split once for the pairwise overlap pass: its segments, and what a URL needs to match it.</summary>
/// <param name="Required">One entry per segment that must be there: the literal, or null for any value.</param>
/// <param name="More">Whether any number of further segments may follow.</param>
internal sealed record UrlPattern(IReadOnlyList<UrlSegment> Segments, IReadOnlyList<string?> Required, bool More)
{
    public static UrlPattern Parse(string pattern)
    {
        var segments = UrlSegment.Parse(pattern);
        var (required, more) = UrlSegment.Tokens(segments);
        return new UrlPattern(segments, required, more);
    }
}

/// <summary>Logs the route warnings once the app has started, in every mode; <c>netjes routes</c> reports them instead.</summary>
internal sealed class RouteDiagnosticsHostedService(RouteDiagnostics diagnostics, NetjesContext context) : IHostedLifecycleService
{
    public Task StartedAsync(CancellationToken cancellationToken)
    {
        if (context.Command?.Name is not "routes")
        {
            foreach (var warning in diagnostics.Check())
            {
                diagnostics.Log(warning);
            }
        }

        return Task.CompletedTask;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
