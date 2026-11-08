using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Netjes;
using Netjes.Routing;

namespace Microsoft.AspNetCore.Builder;

public static partial class NetjesEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Mounts a page package at <paramref name="prefix"/>: its pages, layouts and boundaries merge into the app's route
    /// table by folder, so its layout at the mount wraps the app's own pages in that folder too. Call it before
    /// <c>MapPages()</c>, once per package, on the app rather than inside a <c>MapGroup</c>. Its npm half is found in
    /// <c>node_modules</c> in development and in the client build's record in production (<c>netjes({ packages })</c> in
    /// <c>vite.config.ts</c>); one that is not there stops the app in one line. Conventions on the result apply to the
    /// package's own endpoints, its file pages among them, but not to the app's pages under the prefix.
    /// </summary>
    /// <param name="endpoints">The app.</param>
    /// <param name="package">The package's descriptor.</param>
    /// <param name="prefix">
    /// The folders the package's pages go under, in folder syntax: <c>/help</c>, <c>/(marketing)/help</c>,
    /// <c>/support/(desk)/help</c>, <c>/[tenant]/help</c> or <c>/</c>.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="prefix"/> is not a mount prefix.</exception>
    /// <exception cref="InvalidOperationException">
    /// Called inside a <c>MapGroup</c>, after <c>MapPages()</c>, or for a package that is mounted already.
    /// </exception>
    public static PagePackageBuilder MapPagePackage(this IEndpointRouteBuilder endpoints, PagePackage package, string prefix)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(package);
        var parsed = MountPrefix.Parse(prefix);
        var services = endpoints.ServiceProvider;
        var sources = services.GetRequiredService<RouteSources>();
        var context = services.GetRequiredService<NetjesContext>();
        var mount = new PagePackageMount(package, parsed);
        try
        {
            var mounted = sources.Add(mount);
            try
            {
                // Builds the table with the package's files, so a conflict or a file it refuses stops the app here.
                _ = services.GetRequiredService<IRouteTableProvider>().Current;
            }
            catch
            {
                sources.Remove(mounted);
                throw;
            }
        }
        catch (NetjesStartupException failure) when (context.IsTheProgram)
        {
            UnhandledStartupFailure.ExitIfUnhandled(failure, services, typeof(PagePackageMounts).FullName!);
            throw;
        }

        return new PagePackageBuilder(endpoints, mount);
    }
}
