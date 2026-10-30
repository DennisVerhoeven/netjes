using Microsoft.Extensions.DependencyInjection;
using Netjes.Export;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Conventions for <c>netjes export</c>, the counterpart of Next.js <c>output: 'export'</c>.</summary>
public static class StaticExportEndpointConventionBuilderExtensions
{
    /// <summary>
    /// The route values a dynamic page is exported for, like Next.js <c>generateStaticParams</c>: one object per page,
    /// e.g. <c>new { slug = "hello" }</c>, with arrays for catch-all parameters.
    /// </summary>
    public static TBuilder StaticParams<TBuilder>(this TBuilder builder, Func<IServiceProvider, IEnumerable<object>> parameters)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(parameters);
        builder.Add(endpoint => endpoint.Metadata.Add(new StaticParamsMetadata(parameters)));
        return builder;
    }

    /// <inheritdoc cref="StaticParams{TBuilder}(TBuilder, Func{IServiceProvider, IEnumerable{object}})"/>
    public static RouteHandlerBuilder StaticParams<TService>(this RouteHandlerBuilder builder, Func<TService, IEnumerable<object>> parameters)
        where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return builder.StaticParams(services => parameters(services.GetRequiredService<TService>()));
    }

    /// <summary>Leaves the page out of <c>netjes export</c>, e.g. a page that needs a server for its form or its user.</summary>
    public static TBuilder ExcludeFromExport<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpoint => endpoint.Metadata.Add(new ExcludeFromExportMetadata()));
        return builder;
    }
}
