using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Netjes.Rendering;

namespace __NAME__.Tests;

/// <summary>Runs the real app in memory, with its pipeline and services, and without a client build or Node.</summary>
public sealed class AppFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not Development: there Netjes output-caches no page, so a test of CachePage would see every request render.
        builder.UseEnvironment("Testing");
        // Development mode reads the routes from app/ on disk; nothing is rendered, so no Vite or Node is needed.
        builder.UseSetting("Netjes:Mode", "Development");
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<ISsrRenderer, FixedHtml>();
            // Development mode would rewrite generated/netjes.ts: a test run leaves it as it is, for `netjes generate --check`.
            services.Configure<NetjesOptions>(options => options.TypesFile = null);
        });
    }

    /// <summary>Stands in for the Node process: a document request gets this HTML. A payload request goes through no renderer.</summary>
    private sealed class FixedHtml : ISsrRenderer
    {
        public Task<string> RenderAsync(string payloadJson, CancellationToken cancellationToken) =>
            Task.FromResult("<!DOCTYPE html><html><body>rendered</body></html>");
    }
}
