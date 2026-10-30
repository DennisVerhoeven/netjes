using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Netjes;
using Netjes.Export;
using Netjes.Images;
using Netjes.Rendering;
using Netjes.Routing;
using Netjes.Sidecar;
using Netjes.TypeGen;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers Netjes and its health check in the service collection.</summary>
public static class NetjesServiceCollectionExtensions
{
    /// <summary>Adds React page rendering. Pair with <c>app.UseNetjes()</c> and <c>app.MapPages()</c>.</summary>
    public static IServiceCollection AddNetjes(this IServiceCollection services, Action<NetjesOptions>? configure = null)
    {
        var options = services.AddOptions<NetjesOptions>().BindConfiguration("Netjes");
        if (configure is not null)
        {
            options.Configure(configure);
        }

        // Defaults for lists only when nothing set them: binding an array appends to whatever is already there.
        options.PostConfigure(o =>
        {
            if (o.Images.Widths.Length == 0) o.Images.Widths = [.. NetjesImageOptions.DefaultWidths];
            if (o.Images.Qualities.Length == 0) o.Images.Qualities = [.. NetjesImageOptions.DefaultQualities];
            // One spelling of the image path ('img-opt/' is '/img-opt'): the endpoint answers only the one it is mapped on.
            o.Images.Path = "/" + o.Images.Path.Trim('/');
        });

        // Page actions always validate antiforgery tokens, and every page payload carries one for its forms.
        services.AddAntiforgery();
        services.TryAddSingleton<NetjesContext>();
        services.TryAddSingleton<ViteManifestProvider>();
        services.TryAddSingleton<IRouteTableProvider>(sp => sp.GetRequiredService<NetjesContext>().ScansPageFiles
            ? new FileSystemRouteTableProvider(
                sp.GetRequiredService<NetjesContext>(), sp.GetRequiredService<ILogger<FileSystemRouteTableProvider>>())
            : new ManifestRouteTableProvider(sp.GetRequiredService<ViteManifestProvider>(), sp.GetRequiredService<NetjesContext>()));
        services.TryAddSingleton<IAssetResolver>(sp => sp.GetRequiredService<NetjesContext>().Mode == NetjesMode.Development
            ? new DevAssetResolver(sp.GetRequiredService<NetjesContext>(), sp.GetRequiredService<SidecarPool>().Primary)
            : new ManifestAssetResolver(sp.GetRequiredService<ViteManifestProvider>()));
        services.TryAddSingleton<SidecarPool>();
        services.TryAddSingleton<ISsrRenderer>(sp => sp.GetRequiredService<SidecarPool>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<PageRenderer>();
        services.TryAddSingleton<LayoutLoaderRegistry>();
        services.TryAddSingleton<ServerActionRegistry>();
        services.TryAddSingleton<IPageCache, PageCache>();
        // AddProblemDetails() only TryAdds its service, so this holds whichever is called first.
        services.Replace(ServiceDescriptor.Singleton<IProblemDetailsService, ActionValidationProblems>());
        services.TryAddSingleton<PageLoaderRegistry>();
        services.TryAddSingleton<PagesEndpointDataSource>();
        services.TryAddSingleton<TypeGenRegistry>();
        services.TryAddSingleton<TypeScriptGenerator>();
        services.TryAddSingleton<CommandExit>();
        services.TryAddSingleton<PendingStartupFailure>();
        services.TryAddSingleton(ExportClient.ForServer);
        services.TryAddSingleton<RouteDiagnostics>();
        services.AddHostedService<TypeGenHostedService>();
        services.AddHostedService<RouteDiagnosticsHostedService>();
        services.AddHostedService<StaticExportService>();
        services.AddHostedService<NodeSidecarHostedService>();
        services.AddHttpContextAccessor();
        services.TryAddScoped<IDraftMode, DraftMode>();
        services.TryAddSingleton<DraftModeCachePolicy>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            Microsoft.Extensions.Options.IConfigureOptions<Microsoft.AspNetCore.OutputCaching.OutputCacheOptions>, DraftModeBasePolicySetup>());
        services.TryAddSingleton<CachePaths>();
        services.TryAddSingleton<CachedSegments>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            Microsoft.Extensions.Options.IConfigureOptions<Microsoft.AspNetCore.OutputCaching.OutputCacheOptions>, PageCacheBasePolicySetup>());
        services.TryAddSingleton<IImageProcessor, PassThroughImageProcessor>();
        services.TryAddSingleton<ImageSlots>();
        services.TryAddSingleton<LocalImageFiles>();
        services.TryAddSingleton<BuiltInEndpoints>();
        // Remote images: never follow redirects (they could point anywhere), connect only to public addresses (checked
        // after DNS resolution), and leave the deadline to the endpoint (Netjes:Images:RemoteTimeout covers the body too).
        // No proxy (HTTP_PROXY, HTTPS_PROXY): through one, the connect callback would check the proxy, not the image host.
        services.AddHttpClient(ImageEndpoint.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                ConnectCallback = ImageEndpoint.ConnectToPublicAddressAsync,
            });
        return services;
    }

    /// <summary>Reports whether the Node sidecars that render pages are up: degraded while some are restarting.</summary>
    public static IHealthChecksBuilder AddNetjes(this IHealthChecksBuilder builder) =>
        builder.AddCheck<NodeSidecarHealthCheck>("netjes");
}
