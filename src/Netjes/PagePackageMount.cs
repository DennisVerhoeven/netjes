using Microsoft.AspNetCore.Builder;
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

    /// <summary>The conventions put on the mount, which its own endpoints get: its file pages among them.</summary>
    internal List<Action<EndpointBuilder>> Conventions { get; } = [];

    internal List<Action<EndpointBuilder>> FinallyConventions { get; } = [];
}
