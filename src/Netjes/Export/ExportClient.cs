using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Netjes.Export;

/// <summary>How the export reaches the running app: an HTTP client for its own address (replaced in tests).</summary>
internal sealed class ExportClient(Func<HttpClient> create)
{
    public HttpClient Create() => create();

    /// <summary>A client for the address the server bound (the CLI passes <c>http://127.0.0.1:0</c>), not following redirects.</summary>
    public static ExportClient ForServer(IServiceProvider services) => new(() =>
    {
        var address = services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("The server has no address to export from.");
        // Wildcard bindings ("http://+:8080") are reachable on loopback.
        var uri = new UriBuilder(address.Replace("://+", "://127.0.0.1").Replace("://*", "://127.0.0.1").Replace("://[::]", "://127.0.0.1"));
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = uri.Uri };
    });
}

/// <summary>The route values <c>StaticParams</c> exports a dynamic page for.</summary>
internal sealed record StaticParamsMetadata(Func<IServiceProvider, IEnumerable<object>> Parameters);

internal sealed class ExcludeFromExportMetadata;
