namespace Netjes.Routing;

/// <summary>
/// Netjes routes <c>app/</c> only. Page files in a <c>pages/</c> folder (next to the app directory, or in the app's
/// root) would otherwise answer 404 without a word, so startup refuses them and names their App Router files, and the
/// API routes in <c>pages/api/</c> as minimal API endpoints to write.
/// </summary>
internal static class PagesFolder
{
    private const int Listed = 5;

    // The Next.js pages router's special files, at the root of pages/, by the App Router file that takes their place.
    private static readonly Dictionary<string, string> SpecialFiles = new(StringComparer.Ordinal)
    {
        ["_app"] = "layout",
        ["_document"] = "layout",
        ["_error"] = "error",
        ["404"] = "not-found",
        ["500"] = "error",
    };

    private enum Kind { None, Page, ApiRoute }

    public static void ThrowIfItHasPageFiles(NetjesContext context)
    {
        var appDirectory = Path.GetFullPath(Path.Combine(context.Root, context.AppDirectory));
        var app = Path.GetRelativePath(context.Root, appDirectory).Replace('\\', '/');
        var files = new[] { context.Root, Path.GetDirectoryName(appDirectory) ?? context.Root }
            .Select(parent => Path.GetFullPath(Path.Combine(parent, "pages")))
            .Distinct(StringComparer.Ordinal)
            .Where(directory => !directory.Equals(appDirectory, StringComparison.OrdinalIgnoreCase) && Directory.Exists(directory))
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(file => (
                    File: Path.GetRelativePath(context.Root, file).Replace('\\', '/'),
                    InPages: Path.GetRelativePath(directory, file).Replace('\\', '/'))))
            .ToLookup(file => KindOf(file.InPages));
        var moves = files[Kind.Page]
            .Select(file => (file.File, Target: AppName(file.InPages), Special: IsSpecialFile(file.InPages)))
            // The special files first: the rest are counted as moving the same way, and they do not.
            .OrderBy(move => !move.Special)
            .ThenBy(move => move.File, StringComparer.Ordinal)
            .ToList();
        var apiRoutes = files[Kind.ApiRoute].Select(file => file.File).Order(StringComparer.Ordinal).ToList();
        if (moves.Count == 0 && apiRoutes.Count == 0)
        {
            return;
        }

        // Every special file, since each has its own App Router file, and five other files in all, one of them an API
        // route when there are any.
        var specials = moves.Count(move => move.Special);
        var pagesListed = Math.Min(moves.Count - specials, apiRoutes.Count > 0 ? Listed - 1 : Listed);
        var message = $"Netjes routes {app}/ only, so it would not serve the pages router files in pages/.";
        if (moves.Count > 0)
        {
            var listed = Listing(
                [.. moves.Select(move => $"{move.File} to {app}/{move.Target}")],
                specials + pagesListed,
                " the same way");
            message += $" Move them to their App Router names ({listed}), or out of pages/ if they are not pages.";
            // Only _app and _document become a layout: what wrapped every page, and the document Netjes renders itself.
            if (moves.Any(move => Path.GetFileNameWithoutExtension(move.Target) == "layout"))
            {
                message += " The root layout wraps every page. Netjes renders <html>, <head> and <body> around it, " +
                    "and the <title>, <meta> and <link> tags a layout renders end up in <head>.";
            }
        }

        if (apiRoutes.Count > 0)
        {
            var listed = Listing(apiRoutes, Listed - pagesListed);
            message += $" Map the API routes ({listed}) as C# minimal API endpoints instead, with app.MapGet or app.MapPost.";
        }

        throw new InvalidPageFileException(message);
    }

    // "a, b, c, and 2 more": the first items, and a count of the rest.
    private static string Listing(IReadOnlyList<string> items, int listed, string rest = "") =>
        string.Join(", ", items.Take(listed))
        + (items.Count > listed ? $"{(listed > 1 ? "," : "")} and {items.Count - listed} more{rest}" : "");

    // What the pages router served, by the path inside pages/: .tsx, .jsx and .js pages, and .ts, .tsx, .js and .jsx
    // API routes in api/. Not .ts files outside api/, which hold no JSX and are helpers far more often than pages, nor
    // co-located tests or declaration files, files in _ folders, or _ files other than the special files at the root.
    private static Kind KindOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var folders = path.Split('/')[..^1];
        if (Path.GetExtension(name) is ".test" or ".spec" or ".d"
            || (name.StartsWith('_') && !IsSpecialFile(path))
            || folders.Any(folder => folder.StartsWith('_')))
        {
            return Kind.None;
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        return folders is ["api", ..]
            ? extension is ".ts" or ".tsx" or ".js" or ".jsx" ? Kind.ApiRoute : Kind.None
            : extension is ".tsx" or ".jsx" or ".js" ? Kind.Page : Kind.None;
    }

    private static bool IsSpecialFile(string path) =>
        !path.Contains('/') && SpecialFiles.ContainsKey(Path.GetFileNameWithoutExtension(path));

    // The App Router name of a pages router file, by its path inside pages/: about.tsx is about/page.tsx, an index file
    // is the page of its folder, and a special file at the root is the root segment file that takes its place. app/
    // routes .tsx and .jsx only, so a .js page becomes a .jsx file.
    private static string AppName(string path)
    {
        var folder = path[..(path.LastIndexOf('/') + 1)];
        var name = Path.GetFileNameWithoutExtension(path);
        var appName = IsSpecialFile(path) ? SpecialFiles[name] : name == "index" ? "page" : $"{name}/page";
        var extension = Path.GetExtension(path);
        if (extension.Equals(".js", StringComparison.OrdinalIgnoreCase))
        {
            extension += char.IsUpper(extension[^1]) ? "X" : "x";
        }

        return folder + appName + extension;
    }
}
