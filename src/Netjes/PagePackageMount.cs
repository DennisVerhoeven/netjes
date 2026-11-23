using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Netjes.Routing;

namespace Netjes;

/// <summary>Where an app mounted a page package: <c>app.MapPagePackage(package, "/support/(desk)/help")</c>.</summary>
public sealed class PagePackageMount
{
    internal PagePackageMount(PagePackage package, MountPrefix prefix)
    {
        Package = package;
        Prefix = prefix;
    }

    /// <summary>
    /// A mount for a package's own tests, where an app would have mounted it, without an app: a package's services that build
    /// URLs from their mount (<see cref="Url"/>) or render a page of the package (<see cref="Render"/>) run against it, and
    /// <see cref="PagePackageMounts.ForTests"/> lists it for the services that take the app's mounts.
    /// </summary>
    /// <param name="package">The package mounted.</param>
    /// <param name="prefix">
    /// Where it is mounted, as <c>MapPagePackage</c> takes it: <c>/help</c>, <c>/support/(desk)/help</c>, <c>/[tenant]/help</c>
    /// or <c>/</c>.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="prefix"/> is not a mount prefix, with the reason in the message.</exception>
    public static PagePackageMount ForTests(PagePackage package, string prefix)
    {
        ArgumentNullException.ThrowIfNull(package);
        return new PagePackageMount(package, MountPrefix.Parse(prefix));
    }

    /// <summary>The package mounted.</summary>
    public PagePackage Package { get; }

    /// <summary>The folders the package's pages go under, relative to the app directory: <c>support/(desk)/help</c>.</summary>
    public string Folder => Prefix.Folder;

    /// <summary>The URL pattern of the mount, without route groups: <c>/support/help</c>, <c>/[tenant]/help</c>.</summary>
    public string Pattern => Prefix.Pattern;

    internal MountPrefix Prefix { get; }

    /// <summary>
    /// The app's URL of <paramref name="path"/>, a URL below the mount: <c>/articles</c> is <c>/help/articles</c> for a
    /// package mounted at <c>/help</c>, and <c>/</c> is the mount itself. Like every <see cref="Netjes.Href"/>, it has no
    /// base path.
    /// </summary>
    /// <param name="path">The path below the mount, starting with <c>/</c>, with its query and fragment if any.</param>
    /// <param name="values">
    /// The values of the prefix's parameters, such as the request's route values; others are ignored. Each one is
    /// encoded as one segment of the URL.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is not a link that starts with one <c>/</c> (another host, a scheme, a relative URL, or a
    /// query or fragment on its own) or has a <c>.</c> or <c>..</c> segment, or <paramref name="values"/> has no value for
    /// one of the prefix's parameters, an empty one, or <c>.</c> or <c>..</c>.
    /// </exception>
    public Href Url(string path, RouteValueDictionary? values = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!Href.TryCreate(path, out _) || path[0] != '/' || HasADotSegment(path))
        {
            throw new ArgumentException(
                $"'{path}' is no path below the mount of '{Package.Name}': start it with '/' (but not '//' or '/\\'), as in '/' for " +
                "the mount itself, with no '.' or '..' segment, and leave out control characters and spaces at either end.",
                nameof(path));
        }

        var prefix = Prefix.Url(values ?? [], out var unfilled) ?? throw new ArgumentException(
            $"'{Package.Name}' is mounted at {Pattern}, and the values have no '{unfilled}' for it: a value is not empty, '.' or '..'.",
            nameof(values));
        return new Href(prefix == "/" ? path : path == "/" || path[1] is '?' or '#' ? prefix + path[1..] : prefix + path);
    }

    /// <summary>
    /// Render the package's page for <paramref name="path"/>, a pattern below the mount in file syntax, from any endpoint:
    /// <c>/articles/[slug]</c> is the page at <c>/help/articles/[slug]</c> for a package mounted at <c>/help</c>, and <c>/</c>
    /// is the mount's own page. It is <see cref="Page.Render"/> for the mounted pattern, which the app chose and the package
    /// does not know: a prefix's values (<c>/[tenant]/help</c>) are filled from the request, as for a page of the app. Its
    /// layout loaders run under the authorization of the endpoint that returns this, not the page's.
    /// </summary>
    /// <param name="path">The pattern below the mount, starting with <c>/</c>, without a query or a fragment.</param>
    /// <param name="props">The page's props.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is not a pattern that starts with one <c>/</c>, or has a query or a fragment.
    /// </exception>
    public PageResult Render(string path, object? props = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0 || path[0] != '/' || (path.Length > 1 && path[1] is '/' or '\\') || path.AsSpan().IndexOfAny('?', '#') >= 0)
        {
            throw new ArgumentException(
                $"'{path}' is no page pattern below the mount of '{Package.Name}': start it with one '/', as in '/' for the mount's " +
                "own page or '/articles/[slug]', and leave out a query or a fragment.",
                nameof(path));
        }

        return Page.Render(Prefix.Above(path), props);
    }

    /// <summary>
    /// Whether the path in <paramref name="url"/>, before its query or fragment, has a segment a browser resolves away and
    /// would climb out of the mount with: <c>.</c> or <c>..</c>, also written with <c>%2e</c>, or after a <c>\</c>, which
    /// browsers read as a <c>/</c>.
    /// </summary>
    private static bool HasADotSegment(string url) =>
        url.Split('?', '#')[0].Split('/', '\\').Any(segment => segment.Replace("%2e", ".", StringComparison.OrdinalIgnoreCase) is "." or "..");

    /// <summary>The full path of the package's own generated file (<c>WriteTypes</c> on the mount); null when the app writes none.</summary>
    internal string? TypesFile { get; set; }

    /// <summary>The conventions put on the mount, which its own endpoints get: its file pages among them.</summary>
    internal List<Action<EndpointBuilder>> Conventions { get; } = [];

    internal List<Action<EndpointBuilder>> FinallyConventions { get; } = [];

    /// <summary>The conventions of the not-found below the mount (<c>ConfigureNotFound</c>); null when the app gives it none.</summary>
    internal NotFoundConventions? NotFound { get; set; }
}
