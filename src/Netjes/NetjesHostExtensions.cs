using Microsoft.Extensions.DependencyInjection;
using Netjes;

namespace Microsoft.Extensions.Hosting;

/// <summary>Tells an app which netjes CLI command, if any, it is running for.</summary>
public static class NetjesHostExtensions
{
    /// <summary>
    /// The netjes CLI command this run of the app is for (<c>generate</c>, <c>routes</c> or <c>export</c>), or null for
    /// an ordinary run. <c>generate</c> and <c>routes</c> answer no requests and render nothing, so startup work they
    /// do not need, such as migrating or seeding a database, can be skipped:
    /// <c>if (app.GetNetjesCommand() is null) await SeedAsync(app.Services);</c>. <c>export</c> requests every page and
    /// each endpoint marked <c>.IncludeInExport()</c>, so it needs what they need.
    /// </summary>
    public static string? GetNetjesCommand(this IHost host) =>
        host.Services.GetRequiredService<NetjesContext>().Command?.Name;
}
