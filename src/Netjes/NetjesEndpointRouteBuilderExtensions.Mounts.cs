using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Netjes;
using Netjes.Rendering;
using Netjes.Routing;

namespace Microsoft.AspNetCore.Builder;

// The extensions on a page package's mount: they map below its URL, for the package's own pages.
public static partial class NetjesEndpointRouteBuilderExtensions
{
    /// <summary>The page a loader or page action is for, the builder it maps on, and its route template there.</summary>
    /// <param name="Package">The package whose page it is, for the spans; null for the app's.</param>
    private sealed record PageTarget(PageRoute Route, IEndpointRouteBuilder Endpoints, string Template, string? Package);

    /// <summary>
    /// On the app, the app's page at <paramref name="pattern"/>, at its template on <paramref name="endpoints"/> (a group
    /// puts its prefix in front). On a page package's mount, the package's page at <paramref name="pattern"/> below the
    /// mount, at its template below the mount on the mount's group, which puts the mount's URL in front and applies the
    /// mount's conventions.
    /// </summary>
    private static PageTarget TargetFor(IEndpointRouteBuilder endpoints, string pattern, string method)
    {
        if (endpoints is not PagePackageBuilder mount)
        {
            RequireOutsideAMount(endpoints, method);
            var route = RequireRoute(endpoints, pattern, method);
            return new(route, endpoints, route.Template, null);
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
                $"No page of '{mounted.Name}' matches '{pattern}' below its mount at {prefix} ('{merged}'). Expected " +
                $"{PageRenderer.ExpectedFile(mounted, pattern)}. Its pages: {string.Join(", ", own)}.");
        }

        if (sources.PackageOf(found) != mounted)
        {
            throw new InvalidOperationException(
                $"{method}(\"{pattern}\") on the mount of '{mounted.Name}' at {prefix}: '{found.Pattern}' is {OwnerOf(sources.PackageOf(found))}'s " +
                $"page '{found.File}', not one of the package's. A mount maps loaders and page actions for the package's own pages" +
                (sources.PackageOf(found) is null ? $"; the app maps this one with app.{method}(\"{found.Pattern}\")." : "."));
        }

        var below = prefix.Template == "/" ? found.Template : found.Template[prefix.Template.Length..];
        return new(found, mount.Group, below, mounted.Name);
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

    private static string OwnerOf(MountedPackage? package) => package is null ? "the app" : $"the page package '{package.Name}'";

    /// <summary>The app's file that would replace a package's file: <c>app/help/contact/page.tsx</c> for its <c>contact/page.tsx</c>.</summary>
    private static string AppFileFor(RouteSources sources, MountedPackage owner, string file) =>
        string.Join('/', new[] { sources.AppDirectory, owner.Mount.Folder, file[(owner.Name.Length + owner.Install.Pages.Length + 2)..] }
            .Where(part => part.Length > 0));
}
