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
    /// query or fragment on its own), or <paramref name="values"/> has no value for one of the prefix's parameters, an
    /// empty one, or <c>.</c> or <c>..</c>.
    /// </exception>
    public Href Url(string path, RouteValueDictionary? values = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!Href.TryCreate(path, out _) || path[0] != '/')
        {
            throw new ArgumentException(
                $"'{path}' is no path below the mount of '{Package.Name}': start it with '/' (but not '//' or '/\\'), as in '/' for " +
                "the mount itself, and leave out control characters and spaces at either end.",
                nameof(path));
        }

        var prefix = Prefix.Url(values ?? [], out var unfilled) ?? throw new ArgumentException(
            $"'{Package.Name}' is mounted at {Pattern}, and the values have no '{unfilled}' for it: a value is not empty, '.' or '..'.",
            nameof(values));
        return new Href(prefix == "/" ? path : path == "/" || path[1] is '?' or '#' ? prefix + path[1..] : prefix + path);
    }

    /// <summary>The conventions put on the mount, which its own endpoints get: its file pages among them.</summary>
    internal List<Action<EndpointBuilder>> Conventions { get; } = [];

    internal List<Action<EndpointBuilder>> FinallyConventions { get; } = [];
}
