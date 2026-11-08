using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Netjes.Routing;

/// <summary>
/// A page package's mount, which <c>MapPagePackage</c> returns. Endpoints mapped through it map below the mount's URL,
/// and conventions on it (<c>RequireAuthorization()</c>, <c>ExcludeFromExport()</c>) apply to the package's own
/// endpoints: its file pages and what is mapped through the mount. They do not reach the app's own pages under the
/// prefix; the conventions of a layout loader above them do.
/// </summary>
public sealed class PagePackageBuilder : IEndpointRouteBuilder, IEndpointConventionBuilder
{
    private readonly IEndpointRouteBuilder _outer;
    private readonly RouteGroupBuilder _group;

    internal PagePackageBuilder(IEndpointRouteBuilder outer, PagePackageMount mount)
    {
        _outer = outer;
        _group = outer.MapGroup(mount.Prefix.Template);
        Mount = mount;
    }

    /// <summary>Where the package is mounted.</summary>
    public PagePackageMount Mount { get; }

    IServiceProvider IEndpointRouteBuilder.ServiceProvider => _outer.ServiceProvider;

    ICollection<EndpointDataSource> IEndpointRouteBuilder.DataSources => ((IEndpointRouteBuilder)_group).DataSources;

    IApplicationBuilder IEndpointRouteBuilder.CreateApplicationBuilder() => _outer.CreateApplicationBuilder();

    void IEndpointConventionBuilder.Add(Action<EndpointBuilder> convention)
    {
        Mount.Conventions.Add(convention);
        ((IEndpointConventionBuilder)_group).Add(convention);
    }

    void IEndpointConventionBuilder.Finally(Action<EndpointBuilder> finallyConvention)
    {
        Mount.FinallyConventions.Add(finallyConvention);
        ((IEndpointConventionBuilder)_group).Finally(finallyConvention);
    }
}
