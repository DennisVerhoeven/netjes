using System.Text.RegularExpressions;

namespace Netjes.Routing;

/// <summary>
/// One source of convention files for the route table: the app's own directory, or the pages folder of a page package
/// mounted at a prefix. The table merges the sources by folder.
/// </summary>
/// <param name="Name"><c>app</c> for the app; a package's npm name.</param>
/// <param name="Folder">
/// The folder the source's files go under in the merged tree, relative to the app directory and in folder syntax:
/// empty for the app, <c>support/(desk)/help</c> for a package mounted at <c>/support/(desk)/help</c>.
/// </param>
/// <param name="IdPrefix">
/// What the id of each of the source's files starts with: <c>app/</c> (or <c>src/app/</c>) for the app, the npm name and
/// the pages folder for a package (<c>@acme/help-center/app/</c>).
/// </param>
/// <param name="Files">The source's files by id. The app's may hold other files, which are left out.</param>
internal sealed partial record PageSource(string Name, string Folder, string IdPrefix, IReadOnlyList<string> Files)
{
    public const string AppName = "app";

    /// <summary>Whether this is the app's own directory rather than a package.</summary>
    public bool IsApp { get; private init; }

    public static PageSource App(IEnumerable<string> files, string appDirectory = "app") =>
        new(AppName, "", appDirectory.Replace('\\', '/').Trim('/') + "/", [.. files]) { IsApp = true };

    /// <param name="name">The package's npm name.</param>
    /// <param name="pages">The package's pages folder, relative to the package (<c>app</c>).</param>
    /// <param name="prefix">Where the package is mounted.</param>
    /// <param name="files">The package's files by id: <c>@acme/help-center/app/page.tsx</c>.</param>
    public static PageSource Package(string name, string pages, MountPrefix prefix, IEnumerable<string> files) =>
        new(name, prefix.Folder, $"{name}/{pages}/", [.. files]);

    /// <summary>Whether <paramref name="name"/> is an npm package name, which is what a page package goes by.</summary>
    public static bool IsNpmName(string name) => NpmNameRegex().IsMatch(name);

    // npm's rules, with the capitals that older names have.
    [GeneratedRegex("^(?:@[A-Za-z0-9~-][A-Za-z0-9._~-]*/)?[A-Za-z0-9~-][A-Za-z0-9._~-]*$")]
    private static partial Regex NpmNameRegex();
}
