using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Netjes.TypeGen;

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
        var owner = new PagePackageEndpointMetadata(mount.Package.Name);
        ((IEndpointConventionBuilder)this).Add(endpoint => endpoint.Metadata.Add(owner));
    }

    /// <summary>Where the package is mounted.</summary>
    public PagePackageMount Mount { get; }

    /// <summary>
    /// The app the package is mounted in, where its loaders and page actions map at their pages' templates and its server
    /// actions at <c>/_netjes/actions/{name}</c>, each with the mount's conventions.
    /// </summary>
    internal IEndpointRouteBuilder Outer => _outer;

    /// <summary>
    /// Replaces the package's page at <paramref name="pattern"/> with the app's page in the same folder of the merged tree:
    /// <c>.Override("/help/contact")</c> renders <c>app/help/contact/page.tsx</c> in place of the package's
    /// <c>contact/page.tsx</c>. The app's page renders with the package's loader and page action, and keeps the
    /// conventions of the mount and of the layout loaders above the package's page; the app maps neither a loader nor a
    /// page action of its own for it. Without an override, an app page of the same shape as a package's page stops
    /// <c>MapPages()</c>. Call it before <c>MapPages()</c>.
    /// </summary>
    /// <param name="pattern">
    /// The page's URL pattern in file syntax, as the app links to it, below the mount: <c>/help/contact</c>,
    /// <c>/[tenant]/help/articles/[slug]</c>. It is matched by shape, literals in any case and one trailing slash left
    /// out, and names the parameters as the package's page and the app's page do.
    /// </param>
    /// <returns>The mount, for more calls.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="pattern"/> is no URL pattern (no leading <c>/</c>; a <c>\</c>, a <c>%</c>, <c>.</c>, <c>..</c>
    /// or an empty segment), or is not below the mount.
    /// </exception>
    /// <exception cref="RouteConflictException">
    /// The package has no page there, the app has none there, their parameters are named otherwise, the app's page is
    /// outside a folder whose files wrap the package's, or the page there is another package's.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Called after <c>MapPages()</c>, or the app maps a loader or page action of its own for the page, which would serve
    /// it in place of the package's.
    /// </exception>
    public PagePackageBuilder Override(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var services = _outer.ServiceProvider;
        var sources = services.GetRequiredService<RouteSources>();
        var name = Mount.Package.Name;
        if (sources.PagesMapped)
        {
            throw new InvalidOperationException(
                $".Override(\"{pattern}\") on the mount of '{name}' is called after MapPages(), which has routed the pages. " +
                "Call it before app.MapPages().");
        }

        var url = OverridePattern(pattern);
        if (Mount.Prefix.Below(url) is null)
        {
            throw new ArgumentException(
                $".Override(\"{pattern}\"): '{url}' is not below the mount of '{name}' at {Mount.Prefix}. An override replaces one " +
                $"of the package's pages, so its URL pattern starts with the mount's, {Mount.Pattern}.", nameof(pattern));
        }

        var routes = services.GetRequiredService<IRouteTableProvider>();
        if (routes.Current.FindByShape(url) is { } page && sources.PackageOf(page) is { } owner && owner.Name != name)
        {
            throw new RouteConflictException(
                $".Override(\"{pattern}\") on the mount of '{name}': '{page.File}' at '{page.Pattern}' is a page of " +
                $"'{owner.Name}', mounted at {owner.Mount.Prefix}. Call .Override on that package's mount.");
        }

        sources.AddOverride(url);
        try
        {
            // Builds the table with the override, so one that fits no page stops the app here.
            var replaced = routes.Current.FindByShape(url);
            // The app's page renders with the package's loader and page action. One the app mapped for its page before
            // the mount would serve it in their place, without the mount's conventions.
            if (replaced is { Overrides: { } file } && sources.AppMappingFor(replaced.Pattern) is var (mapped, method))
            {
                throw new InvalidOperationException(
                    $".Override(\"{pattern}\") on the mount of '{name}': '{replaced.File}' replaces '{file}' and renders with the " +
                    $"package's loader and page action, but the app maps app.{method}(\"{mapped}\") for it, which would serve the " +
                    $"page in their place and without the mount's conventions. Remove the app's {method}, or the override.");
            }
        }
        catch
        {
            sources.RemoveOverride(url);
            throw;
        }

        return this;
    }

    /// <summary>
    /// Writes the package's own generated file at <paramref name="path"/>, for the app the package is developed in: the
    /// zod schemas of its loaders, page actions, layout loaders, server actions and endpoints, and its typed routes, all
    /// below the mount, with the links of <c>mounted()</c> typed by them. The file is the same wherever an app mounts the
    /// package, so the package ships it. It is written when the app starts in Development and by <c>netjes generate</c>,
    /// and <c>netjes generate --check</c> checks it, as for the app's own file.
    /// </summary>
    /// <param name="path">The file, relative to the app's root as <c>NetjesOptions.TypesFile</c> is: <c>../HelpCenter/generated/netjes.ts</c>.</param>
    /// <returns>The mount, for more calls.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is empty, or is the app's generated file or another mounted package's.
    /// </exception>
    public PagePackageBuilder WriteTypes(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException($"WriteTypes on the mount of '{Mount.Package.Name}' needs the path of a file.", nameof(path));
        }

        var services = _outer.ServiceProvider;
        var context = services.GetRequiredService<NetjesContext>();
        var full = Path.GetFullPath(path, context.Root);
        if (context.TypesFilePath is { } app && SamePath(app, full))
        {
            throw new ArgumentException(
                $"WriteTypes(\"{path}\") on the mount of '{Mount.Package.Name}' names the app's own generated file " +
                "(NetjesOptions.TypesFile). Give the package's file a path of its own, in the package.", nameof(path));
        }

        if (services.GetRequiredService<RouteSources>().Mounts
                .FirstOrDefault(m => m.Mount != Mount && m.Mount.TypesFile is { } other && SamePath(other, full)) is { } owner)
        {
            throw new ArgumentException(
                $"WriteTypes(\"{path}\") on the mount of '{Mount.Package.Name}' names the generated file of '{owner.Name}'. " +
                "Give each package's file a path of its own.", nameof(path));
        }

        Mount.TypesFile = full;
        return this;
    }

    /// <summary>
    /// Adds an entry to an export of the package's own, in the app's generated file and in the package's:
    /// <c>.AddTypeScriptExport("blocks", "hero", new Dictionary&lt;string, Type&gt; { ["props"] = typeof(HeroBlock) })</c>
    /// writes <c>export const blocks = { "hero": { "props": HeroBlock } } as const;</c>, with the zod schema of each slot's
    /// type, so the app's code and the package's read the same schemas by key. The package owns the export: the app and
    /// other packages cannot add to it. Entries are in ordinal order of their keys, and slots of their names.
    /// </summary>
    /// <param name="group">
    /// The export's name, a JavaScript identifier of ASCII letters, digits, <c>_</c> and <c>$</c>: no reserved word,
    /// and none of the names a generated file declares or imports (<c>pages</c>, <c>Href</c>, <c>Link</c>, <c>z</c>, ...).
    /// A C# type of that name gets another name in both files.
    /// </param>
    /// <param name="key">The entry's key in the export, once per export; any text but <c>__proto__</c>.</param>
    /// <param name="slots">The entry's types by name, each written as its zod schema; any name but <c>__proto__</c>.</param>
    /// <returns>The mount, for more calls.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="group"/> is no identifier, a reserved word or a name of the generated file, <paramref name="key"/>
    /// or a slot's name is <c>__proto__</c>, or a slot has no type or one no value has (an open generic type, a by-ref
    /// type, a pointer, <c>void</c>).
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The export has an entry of that key already, or the app or another package added the export.
    /// </exception>
    public PagePackageBuilder AddTypeScriptExport(string group, string key, IReadOnlyDictionary<string, Type> slots)
    {
        _outer.ServiceProvider.GetRequiredService<TypeGenRegistry>().Add(group, key, slots, Mount.Package.Name);
        return this;
    }

    /// <summary>Two full paths of one file, also on a file system that ignores case.</summary>
    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The pattern without one trailing slash, refusing what is no URL pattern in file syntax.</summary>
    private static string OverridePattern(string pattern)
    {
        ArgumentException Refused(string reason) => new($".Override(\"{pattern}\"): {reason}.", nameof(pattern));
        if (!pattern.StartsWith('/'))
        {
            throw Refused("a URL pattern starts with '/'");
        }

        if (pattern.Contains('\\'))
        {
            throw Refused("a URL pattern uses '/' between its segments, never '\\'");
        }

        if (pattern.Contains('%'))
        {
            throw Refused("a URL pattern is written as the page's folders name it, so it cannot hold '%'");
        }

        var url = pattern.Length > 1 && pattern.EndsWith('/') ? pattern[..^1] : pattern;
        foreach (var segment in url == "/" ? [] : url[1..].Split('/'))
        {
            if (segment.Length == 0)
            {
                throw Refused("a URL pattern cannot hold an empty segment");
            }

            if (segment is "." or "..")
            {
                throw Refused($"a URL pattern cannot hold the segment '{segment}'");
            }
        }

        return url;
    }

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
