using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Netjes;
using Netjes.Rendering;
using Netjes.Routing;

namespace Microsoft.AspNetCore.Builder;

// The extensions on a page package's mount: they map below its URL, for the package's own pages.
public static partial class NetjesEndpointRouteBuilderExtensions
{
    /// <summary>The page a loader or page action is for, and the builder it maps on, at the page's template.</summary>
    /// <param name="Mount">The page package's mount it is mapped through, whose conventions it gets; null for the app's.</param>
    private sealed record PageTarget(PageRoute Route, IEndpointRouteBuilder Endpoints, PagePackageBuilder? Mount)
    {
        public string? Package => Mount?.Mount.Package.Name;
    }

    /// <summary>
    /// On the app, the app's page at <paramref name="pattern"/>, mapped on <paramref name="endpoints"/> (a group puts its
    /// prefix in front). On a page package's mount, the package's page at <paramref name="pattern"/> below the mount,
    /// mapped on the app at the page's own template, which holds the mount's URL. A group at the mount's URL would map
    /// the mount's own page at <c>/help/</c>, where the page's template is <c>/help</c>.
    /// </summary>
    private static PageTarget TargetFor(IEndpointRouteBuilder endpoints, string pattern, string method)
    {
        if (endpoints is not PagePackageBuilder mount)
        {
            RequireOutsideAMount(endpoints, method);
            var route = RequireRoute(endpoints, pattern, method);
            endpoints.ServiceProvider.GetRequiredService<RouteSources>().MappedByTheApp(route.Pattern, method);
            return new(route, endpoints, null);
        }

        var mounted = MountedOf(mount);
        var table = endpoints.ServiceProvider.GetRequiredService<IRouteTableProvider>().Current;
        var sources = endpoints.ServiceProvider.GetRequiredService<RouteSources>();
        var prefix = mount.Mount.Prefix;
        var merged = prefix.Above(pattern);
        // A conflict with an app page leaves the package's page in the table: the app may still replace it with
        // .Override() on the mount, which comes after the package's own extension has mapped through it.
        var found = pattern.StartsWith('/') ? table.Find(merged) : null;
        if (found is null)
        {
            var own = table.Pages.Where(p => sources.PackageOf(p) == mounted).Select(p => prefix.Below(p.Pattern)).OfType<string>();
            throw new InvalidOperationException(
                $"No page of '{mounted.Name}' matches '{pattern}' below its mount at {prefix}" +
                (pattern.StartsWith('/') ? $" ('{merged}')" : ", where a pattern starts with '/' ('/' is the mount's own page)") +
                $". Expected {PageRenderer.ExpectedFile(mounted, pattern)}. Its pages: {string.Join(", ", own)}.");
        }

        if (sources.PackageOf(found) != mounted)
        {
            throw new InvalidOperationException(
                $"{method}(\"{pattern}\") on the mount of '{mounted.Name}' at {prefix}: '{found.Pattern}' is the page '{found.File}' of " +
                $"{OwnerOf(sources.PackageOf(found))}, not one of the package's. A mount maps loaders and page actions for the package's own pages" +
                (sources.PackageOf(found) is null ? $"; the app maps this one with app.{method}(\"{found.Pattern}\")." : "."));
        }

        return new(found, mount.Outer, mount);
    }

    /// <summary>
    /// The conventions of the mount an endpoint is mapped through, on the endpoint, as a route group's would be: before
    /// the endpoint's own, and its finally conventions after the endpoint's. Read when the endpoint is built, so the
    /// conventions put on the mount after the call apply too.
    /// </summary>
    private static TBuilder WithMountConventions<TBuilder>(this TBuilder builder, PagePackageBuilder? mount) where TBuilder : IEndpointConventionBuilder
    {
        if (mount is not null)
        {
            builder.Add(endpoint => mount.Mount.Conventions.ForEach(convention => convention(endpoint)));
            builder.Finally(endpoint => mount.Mount.FinallyConventions.ForEach(convention => convention(endpoint)));
        }

        return builder;
    }

    /// <summary>
    /// The layout a layout loader is for, as one match. On the app, the layout in the folder <paramref name="segment"/>
    /// (<c>app/(shop)</c>) or at its URL (<c>/dashboard</c>); on a page package's mount, the package's layout in its folder
    /// relative to the package (<c>app/(center)</c>) or at its URL below the mount (<c>/</c>). A layout of the other's is
    /// refused, naming the mount.
    /// </summary>
    private static List<RouteSegment> LayoutsFor(IEndpointRouteBuilder endpoints, string segment)
    {
        var mount = endpoints as PagePackageBuilder;
        if (mount is null)
        {
            RequireOutsideAMount(endpoints, nameof(MapLayoutLoader));
        }

        var services = endpoints.ServiceProvider;
        var context = services.GetRequiredService<NetjesContext>();
        var table = services.GetRequiredService<IRouteTableProvider>().Current;
        var sources = services.GetRequiredService<RouteSources>();
        var mounted = mount is null ? null : MountedOf(mount);
        var layouts = table.Pages.SelectMany(p => p.Segments).Concat(table.RootSegments)
            .Where(s => s.Layout is not null)
            .DistinctBy(s => s.Id)
            .ToList();
        var normalized = segment.Replace('\\', '/').TrimEnd('/');
        List<RouteSegment> matches;
        string expected;
        if (mounted is null)
        {
            matches = [.. layouts.Where(s => s.Id == normalized)];
            var path = segment.Trim('/');
            expected = $"{context.AppDirectory}/{(path.Length == 0 ? "" : path + "/")}layout.tsx";
            if (matches.Count == 0)
            {
                var pattern = normalized.Length == 0 ? "/" : normalized;
                if (table.PendingConflictFor(pattern) is { } conflict)
                {
                    throw conflict.ToException();
                }

                matches = [.. layouts.Where(s => s.Pattern == pattern)];
            }
        }
        else if (segment.StartsWith('/'))
        {
            var pattern = mounted.Mount.Prefix.Above(normalized.Length == 0 ? "/" : normalized);
            matches = [.. layouts.Where(s => s.Pattern == pattern)];
            var path = segment.Trim('/');
            expected = $"{mounted.Name}/{mounted.Install.Pages}/{(path.Length == 0 ? "" : path + "/")}layout.tsx";
        }
        else
        {
            // The package's folder, its pages folder first, is a folder of the merged tree below the mount's folders.
            var pages = mounted.Install.Pages;
            var inPackage = normalized == pages ? "" : normalized.StartsWith(pages + "/", StringComparison.Ordinal) ? normalized[(pages.Length + 1)..] : null;
            var id = inPackage is null
                ? null
                : string.Join('/', new[] { sources.AppDirectory, mounted.Mount.Folder, inPackage }.Where(part => part.Length > 0));
            matches = [.. layouts.Where(s => s.Id == id)];
            expected = inPackage is null
                ? $"a folder of '{mounted.Name}' that starts with its pages folder, such as '{pages}' or '{pages}/(group)', or a URL below the mount, such as '/'"
                : $"{mounted.Name}/{normalized}/layout.tsx";
        }

        var own = matches.Where(s => sources.OwnerOf(s.Layout) == mounted).ToList();
        if (own.Count == 0 && matches.Count > 0)
        {
            var layout = matches[0].Layout!;
            throw new InvalidOperationException(sources.OwnerOf(layout) is { } owner && mounted is null
                ? $"MapLayoutLoader(\"{segment}\"): '{layout}' is a layout of the page package '{owner.Name}' mounted at " +
                  $"{owner.Mount.Prefix}, which maps its layout loaders through its mount. An app layout loader goes on a layout of the app's."
                : $"MapLayoutLoader(\"{segment}\") on the mount of '{mounted!.Name}' at {mounted.Mount.Prefix}: '{layout}' is a " +
                  $"layout of {OwnerOf(sources.OwnerOf(layout))}, not one of the package's" +
                  (sources.OwnerOf(layout) is null ? $"; the app maps its loader with app.MapLayoutLoader(\"{matches[0].Id}\")." : "."));
        }

        if (own.Count > 1)
        {
            throw new InvalidOperationException(
                $"Several layouts serve '{segment}': {string.Join(", ", own.Select(m => $"\"{FolderIn(mounted, sources, m.Id)}\""))}. Pass the folder instead.");
        }

        return own.Count == 1
            ? own
            : throw new InvalidOperationException(mounted is null
                ? $"No layout file serves '{segment}'. Expected {expected}."
                : $"No layout of '{mounted.Name}' serves '{segment}' below its mount at {mounted.Mount.Prefix}. Expected {expected}.");
    }

    /// <summary>A merged folder as the caller names it: the app by its id, a package relative to the package (<c>app/(center)</c>).</summary>
    private static string FolderIn(MountedPackage? mounted, RouteSources sources, string id)
    {
        if (mounted is null)
        {
            return id;
        }

        var root = string.Join('/', new[] { sources.AppDirectory, mounted.Mount.Folder }.Where(part => part.Length > 0));
        return id == root ? mounted.Install.Pages : $"{mounted.Install.Pages}/{id[(root.Length + 1)..]}";
    }

    /// <summary>Refuses a page that a page package maps for: its own, or one it renders an override of with its loader.</summary>
    private static void RequireTheApps(PageRoute route, RouteSources sources, string method, string pattern)
    {
        if (sources.PackageOf(route) is not { } owner)
        {
            return;
        }

        throw new InvalidOperationException(route.Overrides is { } replaced
            ? $"{method}(\"{pattern}\"): '{route.File}' replaces '{replaced}' (.Override() on the mount of '{owner.Name}' at " +
              $"{owner.Mount.Prefix}), and renders with the package's loader and page action, so the app maps neither for it."
            : $"{method}(\"{pattern}\"): '{route.Pattern}' is '{route.File}', a page of the page package '{owner.Name}' mounted at " +
              $"{owner.Mount.Prefix}, which maps its loaders and page actions through its mount. To render an app page there, add " +
              $"{AppFileFor(sources, owner, route.File)} and replace the package's page with " +
              $".Override(\"{route.Pattern}\") on the mount.");
    }

    /// <summary>
    /// Refuses a <c>MapGroup</c> made from a page package's mount: a loader, page action or layout loader there would map
    /// at the group's URL on top of the page's, and a server action below a URL that the client never calls.
    /// </summary>
    private static void RequireOutsideAMount(IEndpointRouteBuilder endpoints, string method)
    {
        if (MountAbove(endpoints) is { } mount)
        {
            throw new InvalidOperationException(
                $"{method} is called on a MapGroup made from the mount of '{mount.Mount.Package.Name}' at {mount.Mount.Prefix}, " +
                "where it would map at the wrong URL. Call it on the mount itself.");
        }
    }

    /// <summary>The page package mount a chain of route groups was made from, as far as their outer builders can be read.</summary>
    private static PagePackageBuilder? MountAbove(IEndpointRouteBuilder endpoints)
    {
        for (var builder = endpoints; builder is RouteGroupBuilder group;)
        {
            if (GroupOuter?.GetValue(group) is not IEndpointRouteBuilder outer)
            {
                return null;
            }

            if (outer is PagePackageBuilder mount)
            {
                return mount;
            }

            builder = outer;
        }

        return null;
    }

    private static MountedPackage MountedOf(PagePackageBuilder mount) =>
        mount.Outer.ServiceProvider.GetRequiredService<RouteSources>().Find(mount.Mount.Package.Name)!;

    private static string OwnerOf(MountedPackage? package) => OwnerOf(package?.Name);

    private static string OwnerOf(string? package) => package is null ? "the app" : $"the page package '{package}'";

    /// <summary>The app's file that would replace a package's file: <c>app/help/contact/page.tsx</c> for its <c>contact/page.tsx</c>.</summary>
    private static string AppFileFor(RouteSources sources, MountedPackage owner, string file) =>
        string.Join('/', new[] { sources.AppDirectory, owner.Mount.Folder, file[(owner.Name.Length + owner.Install.Pages.Length + 2)..] }
            .Where(part => part.Length > 0));
}
