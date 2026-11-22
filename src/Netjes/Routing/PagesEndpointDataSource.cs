using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Primitives;

namespace Netjes.Routing;

/// <summary>
/// One endpoint per page file that has no explicit loader, plus a lowest-priority fallback that renders the 404 page.
/// Rebuilt whenever the route table changes, so pages added in development are routable without a restart. A page gets
/// the conventions of the layout loaders above it, a page package's page those of its mount, then those of MapPages().
/// The fallback gets none of them, so MapPages().RequireAuthorization() never challenges an unknown URL; below a mount
/// whose not-found has conventions of its own (<see cref="PagePackageBuilder.ConfigureNotFound"/>), a fallback of the
/// mount's carries those. The layout
/// loaders around a page the request's endpoint has not let its visitor in for are judged on those of MapPages() too
/// (<see cref="PageConventions"/>).
/// </summary>
internal sealed class PagesEndpointDataSource(
    IRouteTableProvider routes, PageLoaderRegistry loaders, LayoutLoaderRegistry layouts, RouteSources sources)
    : EndpointDataSource, IEndpointConventionBuilder
{
    private static readonly HttpMethodMetadata ReadMethods = new([HttpMethods.Get, HttpMethods.Head]);

    private readonly List<Action<EndpointBuilder>> _conventions = [];
    private readonly List<Action<EndpointBuilder>> _finallyConventions = [];
    private readonly Lock _gate = new();
    private (RouteTable Table, int LoaderVersion, int LayoutVersion, IReadOnlyList<Endpoint> Endpoints)? _cache;

    public override IReadOnlyList<Endpoint> Endpoints
    {
        get
        {
            var table = routes.Current;
            lock (_gate)
            {
                if (_cache is not { } cache || cache.Table != table || cache.LoaderVersion != loaders.Version
                    || cache.LayoutVersion != layouts.Version)
                {
                    cache = (table, loaders.Version, layouts.Version, Build(table));
                    _cache = cache;
                }

                return cache.Endpoints;
            }
        }
    }

    public override IChangeToken GetChangeToken() => routes.GetChangeToken();

    public void Add(Action<EndpointBuilder> convention) => _conventions.Add(convention);

    public void Finally(Action<EndpointBuilder> finallyConvention) => _finallyConventions.Add(finallyConvention);

    /// <summary>The conventions of <c>MapPages()</c>, its final ones last, as a file page gets them.</summary>
    public IEnumerable<Action<EndpointBuilder>> PageConventions => _conventions.Concat(_finallyConventions);

    private List<Endpoint> Build(RouteTable table)
    {
        var endpoints = new List<Endpoint>();
        foreach (var route in table.Pages.Where(r => !loaders.Contains(r.Pattern)))
        {
            var builder = new RouteEndpointBuilder(
                http => Page.Props().ExecuteAsync(http), RoutePatternFactory.Parse(route.Template), order: 0)
            {
                DisplayName = $"Page {route.Pattern}",
            };
            builder.Metadata.Add(ReadMethods);
            builder.Metadata.Add(new PageEndpointMetadata(route.Pattern));
            builder.Metadata.Add(new PageRouteMetadata(route.Pattern, isLoader: false));
            layouts.ApplyConventions(route, builder);
            // A package's file pages are its own endpoints, so its mount's conventions apply, before MapPages()'s; so do
            // the app's pages that replace one of them (.Override()), which answer the package's URL.
            if (sources.PackageOf(route) is { } mounted)
            {
                mounted.Mount.Conventions.ForEach(convention => convention(builder));
                mounted.Mount.FinallyConventions.ForEach(convention => convention(builder));
            }

            _conventions.ForEach(convention => convention(builder));
            _finallyConventions.ForEach(convention => convention(builder));
            endpoints.Add(builder.Build());
        }

        // Paths that look like files (have an extension) are left to the static file middleware.
        var fallback = new RouteEndpointBuilder(
            http => Page.NotFound().ExecuteAsync(http), RoutePatternFactory.Parse("{**path:nonfile}"), order: int.MaxValue)
        {
            DisplayName = "Page not found",
        };
        fallback.Metadata.Add(ReadMethods);
        fallback.Metadata.Add(new PageEndpointMetadata(null));
        endpoints.Add(fallback.Build());

        // Below a mount whose not-found has conventions (ConfigureNotFound), an endpoint of its own answers the unknown
        // URLs, with those alone. In the same last order, the mount's literal or value segments rank its route ahead of
        // the fallback's catch-all, and a mount inside another ahead of the outer one's. Its parameter's name cannot be a
        // prefix's value's, which is an identifier.
        foreach (var mounted in sources.Mounts)
        {
            if (mounted.Mount.NotFound is not { } conventions)
            {
                continue;
            }

            var notFound = new RouteEndpointBuilder(
                http => Page.NotFound().ExecuteAsync(http),
                RoutePatternFactory.Parse(mounted.Mount.Prefix.Template + "/{**not-found:nonfile}"),
                order: int.MaxValue)
            {
                DisplayName = "Page not found",
            };
            notFound.Metadata.Add(ReadMethods);
            notFound.Metadata.Add(new PageEndpointMetadata(null));
            conventions.Conventions.ForEach(convention => convention(notFound));
            conventions.FinallyConventions.ForEach(convention => convention(notFound));
            endpoints.Add(notFound.Build());
        }

        return endpoints;
    }
}
