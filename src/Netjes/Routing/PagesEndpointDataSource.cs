using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Primitives;

namespace Netjes.Routing;

/// <summary>
/// One endpoint per page file that has no explicit loader, plus a lowest-priority fallback that renders the 404 page.
/// Rebuilt whenever the route table changes, so pages added in development are routable without a restart.
/// </summary>
internal sealed class PagesEndpointDataSource(IRouteTableProvider routes, PageLoaderRegistry loaders, LayoutLoaderRegistry layouts)
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
            layouts.ApplyConventions(route, builder);
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
        return endpoints;
    }
}
