using Netjes.Routing;

namespace Netjes;

/// <summary>
/// The page packages the app mounted, for a package's own services: the URLs in its emails, the path of its cookie. A
/// singleton; it lists a mount once <c>MapPagePackage</c> has run.
/// </summary>
public sealed class PagePackageMounts
{
    private readonly RouteSources _sources;

    internal PagePackageMounts(RouteSources sources) => _sources = sources;

    /// <summary>Every mount, in the order the app mounted them.</summary>
    public IReadOnlyList<PagePackageMount> All => [.. _sources.Mounts.Select(m => m.Mount)];

    /// <summary>The mount of the package named <paramref name="name"/>, its npm name.</summary>
    /// <exception cref="InvalidOperationException">The app did not mount that package.</exception>
    public PagePackageMount Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return All.FirstOrDefault(m => m.Package.Name == name) ?? throw new InvalidOperationException(
            $"The page package '{name}' is not mounted. Mount it with app.MapPagePackage(...) before app.MapPages()" +
            (All.Count == 0 ? "." : $"; mounted: {string.Join(", ", All.Select(m => $"'{m.Package.Name}'"))}."));
    }
}
