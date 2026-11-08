using System.Reflection;
using Netjes.Routing;

namespace Netjes;

/// <summary>
/// The C# half of a page package: a NuGet package that pairs with an npm package of pages, layouts and boundaries, which
/// an app mounts at a prefix with <c>app.MapPagePackage(package, "/help")</c>. The package keeps one in a static and
/// wraps that call in an extension of its own.
/// </summary>
public sealed class PagePackage
{
    /// <param name="name">The npm package's name, which starts the id of each of its files: <c>@acme/help-center</c>.</param>
    /// <param name="assembly">The package's assembly, whose informational version is the package's.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not the name of an npm package.</exception>
    public PagePackage(string name, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(assembly);
        if (!PageSource.IsNpmName(name))
        {
            throw new ArgumentException($"'{name}' is not an npm package name, which is what a page package goes by.", nameof(name));
        }

        Name = name;
        Version = VersionOf(assembly);
        AssemblyName = assembly.GetName().Name ?? name;
    }

    /// <summary>The npm package's name.</summary>
    public string Name { get; }

    /// <summary>The assembly's informational version without its <c>+</c> suffix (the commit), as NuGet versions it.</summary>
    public string Version { get; }

    /// <summary>
    /// Whether the package's layout draws a whole page around its pages: a header, navigation and footer of its own, as a
    /// back-office package does. Netjes then warns (<c>package-shell</c>) when the app's root layout wraps every page and
    /// the app has no route group layout below it for its own chrome, since the package's pages would render inside the
    /// app's chrome too.
    /// </summary>
    public bool DrawsItsOwnShell { get; init; }

    /// <summary>
    /// Whether the package's pages stream (<c>Page.Defer</c>, or a Suspense boundary still waiting when the shell goes
    /// out), which a Content-Security-Policy allows only with a nonce. Netjes then warns (<c>package-nonce</c>) when
    /// <c>NetjesOptions.ContentSecurityPolicy</c> is a policy without <c>{nonce}</c>.
    /// </summary>
    public bool RequiresNonce { get; init; }

    /// <summary>The name of the package's assembly, which errors give for its NuGet half when its npm half names none.</summary>
    internal string AssemblyName { get; }

    /// <summary>The version of an assembly, as <see cref="Version"/> gives a package's.</summary>
    internal static string VersionOf(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { Length: > 0 } version
            ? version.Split('+')[0]
            : assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
