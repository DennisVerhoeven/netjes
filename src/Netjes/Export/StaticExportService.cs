using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netjes.Rendering;
using Netjes.Routing;
using Netjes.TypeGen;

namespace Netjes.Export;

/// <summary>
/// <c>netjes export</c> (<c>NETJES_COMMAND=export</c>): renders every page through the running app into static
/// HTML files, like Next.js <c>output: 'export'</c>. Dynamic pages are rendered for their <c>.StaticParams(...)</c>;
/// pages that need a server (form actions, authorization) fail the export unless excluded.
/// </summary>
internal sealed class StaticExportService(
    IServiceProvider services,
    NetjesContext context,
    IHostApplicationLifetime lifetime,
    CommandExit exit,
    ILogger<StaticExportService> logger) : IHostedLifecycleService
{
    /// <summary>A URL no page serves, so the app answers with its 404 page.</summary>
    private const string NotFoundProbe = "/__netjes_export_404";

    /// <summary>Left in the output so a later export knows the directory is its own to replace.</summary>
    internal const string Marker = ".netjes-export";

    /// <summary>Stands in for the exported site when checking where a redirect goes.</summary>
    private static readonly Uri SiteBase = new("https://site.invalid/");

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        // Nor when the app could not start: it says why in one line, and stops (NodeSidecarHostedService).
        if (context.Command is not { Name: "export" } command
            || services.GetRequiredService<PendingStartupFailure>().Exception is not null)
        {
            return;
        }

        int code;
        try
        {
            code = await ExportAsync(Path.GetFullPath(command.Output ?? "out", context.Root), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The export failed: {Message}", ex.Message);
            code = 1;
        }

        exit.Set(code);
        lifetime.StopApplication();
    }

    private async Task<int> ExportAsync(string output, CancellationToken cancellationToken)
    {
        var (pages, problems) = await PlanAsync();
        problems.AddRange(CollisionProblems(pages));
        problems.AddRange(DeviceNameProblems(pages));
        problems.AddRange(OutputProblems(output));

        if (problems.Count > 0)
        {
            foreach (var problem in problems)
            {
                logger.LogError("Cannot export: {Problem}", problem);
            }

            return 1;
        }

        if (Directory.Exists(output))
        {
            Directory.Delete(output, recursive: true);
        }

        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, Marker), "Written by netjes export; the next export replaces this directory.\n", cancellationToken);
        CopyClientBuild(output);
        using var client = services.GetRequiredService<ExportClient>().Create();
        var failures = 0;
        foreach (var (url, _) in pages)
        {
            var html = await RenderAsync(client, url, StatusCodes.Status200OK, cancellationToken);
            if (html is null)
            {
                failures++;
                continue;
            }

            await WriteAsync(FileFor(output, url), html, cancellationToken);
        }

        if (await RenderAsync(client, NotFoundProbe, StatusCodes.Status404NotFound, cancellationToken) is { } notFound)
        {
            await WriteAsync(Path.Combine(output, "404.html"), notFound, cancellationToken);
        }
        else
        {
            failures++;
        }

        if (failures > 0)
        {
            return 1;
        }

        logger.LogInformation("Exported {Count} pages to {Output}", pages.Count, output);
        return 0;
    }

    /// <summary>
    /// Why the export must not write to <paramref name="output"/>: it is replaced wholesale, so it has to be a directory
    /// of the app's own that holds nothing else (missing, empty, or a previous export).
    /// </summary>
    private List<string> OutputProblems(string output)
    {
        var problems = new List<string>();
        if (Contains(output, context.Root) || !Contains(context.Root, output))
        {
            problems.Add($"the output directory {output} must be inside the app ({context.Root}), such as 'out'");
            return problems;
        }

        var reserved = new List<string>
        {
            Path.Combine(context.Root, context.AppDirectory),
            context.PublicDirectory,
            context.ClientDist,
            context.ServerDist,
        };
        if (context.TypesFilePath is { } typesFile && Path.GetDirectoryName(typesFile) is { } typesDirectory)
        {
            reserved.Add(typesDirectory);
        }

        foreach (var directory in reserved.Select(d => Path.GetFullPath(d)))
        {
            if (Contains(directory, output) || Contains(output, directory))
            {
                problems.Add($"the output directory {output} overlaps {directory}, which the app needs; choose another, such as 'out'");
            }
        }

        if (problems.Count == 0 && Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()
            && !File.Exists(Path.Combine(output, Marker)))
        {
            problems.Add($"the output directory {output} is not empty and not a previous export; empty it or choose another");
        }

        return problems;
    }

    /// <summary>A URL to render and the page pattern it comes from.</summary>
    private sealed record PlannedPage(string Url, string Pattern);

    /// <summary>The URL of every page to render, or why some page cannot be exported.</summary>
    private async Task<(List<PlannedPage> Pages, List<string> Problems)> PlanAsync()
    {
        var table = services.GetRequiredService<IRouteTableProvider>().Current;
        var endpoints = services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        var planned = new List<PlannedPage>();
        var problems = new List<string>();
        // A fallback policy protects every endpoint without authorization metadata of its own.
        var fallback = services.GetService<IAuthorizationPolicyProvider>() is { } policies
            ? await policies.GetFallbackPolicyAsync()
            : null;

        foreach (var page in table.Pages)
        {
            var forPage = endpoints.Where(e => e.Metadata.GetMetadata<PageEndpointMetadata>()?.Pattern == page.Pattern).ToList();
            var get = forPage.FirstOrDefault(e => e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(HttpMethods.Get) ?? true);
            // Excluding the loader, the action or the file pages (MapPages()) all leave the page out.
            if (forPage.Any(e => e.Metadata.GetMetadata<ExcludeFromExportMetadata>() is not null))
            {
                continue;
            }

            if (forPage.Any(e => e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(HttpMethods.Post) ?? false))
            {
                problems.Add($"{page.Pattern} has a page action; form posts need a server. Exclude it with .ExcludeFromExport().");
                continue;
            }

            if (get?.Metadata.GetMetadata<IAuthorizeData>() is not null && get.Metadata.GetMetadata<IAllowAnonymous>() is null)
            {
                problems.Add($"{page.Pattern} requires authorization, which a static file cannot check. Exclude it with .ExcludeFromExport().");
                continue;
            }

            if (fallback is not null && get?.Metadata.GetMetadata<IAuthorizeData>() is null && get?.Metadata.GetMetadata<IAllowAnonymous>() is null)
            {
                problems.Add(
                    $"{page.Pattern} requires authorization through the fallback policy, which a static file cannot check. " +
                    "Mark it .AllowAnonymous() or exclude it with .ExcludeFromExport().");
                continue;
            }

            if (page.Parameters.Count == 0)
            {
                planned.Add(new(page.Pattern, page.Pattern));
                continue;
            }

            if (get?.Metadata.GetMetadata<StaticParamsMetadata>() is not { } staticParams)
            {
                problems.Add($"{page.Pattern} is dynamic; list the pages to export with .StaticParams(...) on its loader, or .ExcludeFromExport().");
                continue;
            }

            await using var scope = services.CreateAsyncScope();
            foreach (var values in staticParams.Parameters(scope.ServiceProvider))
            {
                try
                {
                    planned.Add(new(UrlFor(page, new RouteValueDictionary(values)), page.Pattern));
                }
                catch (ArgumentException ex)
                {
                    problems.Add($"{page.Pattern}: {ex.Message}");
                }
            }
        }

        return (planned, problems);
    }

    /// <summary>
    /// Pages that would write one file. Windows and macOS treat paths that differ only in case or Unicode normalization
    /// as one, and Windows drops trailing dots and spaces, so the export refuses what one file system would merge.
    /// </summary>
    private static IEnumerable<string> CollisionProblems(IEnumerable<PlannedPage> pages)
    {
        var seen = new Dictionary<string, PlannedPage>(StringComparer.Ordinal);
        foreach (var page in pages.Distinct())
        {
            var key = PortableKey(page.Url);
            if (seen.TryGetValue(key, out var other))
            {
                var why = other.Url == page.Url
                    ? ""
                    : " (on Windows and macOS, paths that differ only in case, Unicode normalization or trailing dots and spaces are one file)";
                yield return $"{other.Url} ({other.Pattern}) and {page.Url} ({page.Pattern}) map to the same output file, " +
                    $"{Path.Join(PathOf(other.Url), "index.html")}{why}; give one of them another path or exclude it with .ExcludeFromExport().";
                continue;
            }

            seen.Add(key, page);
        }
    }

    /// <summary>
    /// Pages with a path segment Windows reserves for a device (<c>con</c>, <c>nul.txt</c>, <c>com1</c>): there it names
    /// the device in any case and with any extension, so no file or directory can have it.
    /// </summary>
    private static IEnumerable<string> DeviceNameProblems(IEnumerable<PlannedPage> pages) =>
        pages.Distinct()
            .Where(page => page.Url.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => IsDeviceName(Uri.UnescapeDataString(segment))))
            .Select(page => $"{page.Url} ({page.Pattern}) has a path segment that names a Windows device (CON, PRN, AUX, NUL, COM0-9, LPT0-9), " +
                "which cannot be a file or directory there; give it another path or exclude it with .ExcludeFromExport().");

    private static bool IsDeviceName(string segment)
    {
        var name = segment.TrimEnd('.', ' ').Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL"
            || (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal))
                && (char.IsAsciiDigit(name[3]) || name[3] is '\u00b9' or '\u00b2' or '\u00b3'));
    }

    private static string PortableKey(string url) => string.Join('/', url.Split('/', StringSplitOptions.RemoveEmptyEntries)
        .Select(segment => Uri.UnescapeDataString(segment).Normalize(NormalizationForm.FormC).TrimEnd('.', ' ').ToUpperInvariant())
        .Where(segment => segment.Length > 0));

    private static string UrlFor(PageRoute page, RouteValueDictionary values)
    {
        var parameters = page.Parameters.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var segments = new List<string>();
        foreach (var segment in page.Pattern.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = segment.Trim('[', ']').TrimStart('.');
            if (!segment.StartsWith('[') || !parameters.TryGetValue(name, out var parameter))
            {
                segments.Add(segment);
                continue;
            }

            values.TryGetValue(parameter.Name, out var value);
            var parts = value switch
            {
                null => [],
                string text when parameter.Kind != RouteParameterKind.Single => text.Split('/', StringSplitOptions.RemoveEmptyEntries),
                string text => [text],
                IEnumerable<object?> items => items.Select(i => Convert.ToString(i, System.Globalization.CultureInfo.InvariantCulture) ?? "").ToArray(),
                System.Collections.IEnumerable items => items.Cast<object?>().Select(i => Convert.ToString(i, System.Globalization.CultureInfo.InvariantCulture) ?? "").ToArray(),
                _ => [Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""],
            };
            if (parts.Length == 0 && parameter.Kind != RouteParameterKind.OptionalCatchAll)
            {
                throw new ArgumentException($"a static params entry has no value for '{parameter.Name}'.");
            }

            // Each value becomes a directory name: nothing that could climb out of the output or name another path.
            if (parts.FirstOrDefault(part => !IsSafeSegment(part)) is { } unsafePart)
            {
                throw new ArgumentException(
                    $"'{unsafePart.Replace("\0", "\\0", StringComparison.Ordinal)}' for '{parameter.Name}' is not a safe path segment " +
                    "(empty, '.', '..', or containing '/', '\\', ':' or NUL).");
            }

            segments.AddRange(parts.Select(Uri.EscapeDataString));
        }

        return "/" + string.Join('/', segments);
    }

    private async Task<string?> RenderAsync(HttpClient client, string url, int expected, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("text/html");
        using var response = await client.SendAsync(request, cancellationToken);
        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
        {
            // Only a page's own Page.Redirect becomes a refresh page: a login, HTTPS or other middleware redirect
            // means the page needs a server, and exporting it as a stub would hide that.
            if (!response.Headers.Contains(PageRenderer.ExportRedirectHeader) || response.Headers.Location is not { } location)
            {
                logger.LogError("Cannot export {Url}: the app answered with a {Status} redirect that is not the page's own (Page.Redirect)", url, status);
                return null;
            }

            if (location.IsAbsoluteUri && client.BaseAddress is { } server
                && string.Equals(location.Authority, server.Authority, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError("Cannot export {Url}: it redirects to the export server itself ({Location}); use a relative URL", url, location);
                return null;
            }

            // A meta refresh drops a quote after url=, so it would go where the link and the live server do not.
            var sent = location.OriginalString;
            var normalized = Page.Normalize(sent);
            if (normalized.StartsWith('\'') || normalized.StartsWith('"'))
            {
                logger.LogError(
                    "Cannot export {Url}: it redirects to {Location}, which starts with a quote that a meta refresh drops; start a relative URL with '/' or './'",
                    url, sent);
                return null;
            }

            // Resolved as a browser reads it (tabs, newlines and leading spaces dropped), a path stays on the site.
            if (!Uri.TryCreate(SiteBase, normalized, out var resolved)
                || (resolved.Scheme != Uri.UriSchemeHttp && resolved.Scheme != Uri.UriSchemeHttps))
            {
                logger.LogError("Cannot export {Url}: it redirects to {Location}, which is neither a path on the site nor an http(s) URL", url, sent);
                return null;
            }

            // As sent: decoding would turn javascript%3A into a scheme and /%2Fhost into another site.
            var target = HtmlEncoder.Default.Encode(sent);
            return $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"0; url={target}\">" +
                $"<link rel=\"canonical\" href=\"{target}\"></head><body><a href=\"{target}\">{target}</a></body></html>";
        }

        if (status != expected)
        {
            logger.LogError("Cannot export {Url}: the app answered {Status} instead of {Expected}", url, status, expected);
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private void CopyClientBuild(string output)
    {
        if (!Directory.Exists(context.ClientDist))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(context.ClientDist, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(context.ClientDist, file);
            if (relative.Split(Path.DirectorySeparatorChar)[0] == ".vite")
            {
                continue;
            }

            var destination = Path.Combine(output, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static bool IsSafeSegment(string part) =>
        part.Length > 0 && part is not ("." or "..") && part.IndexOfAny(['/', '\\', ':', '\0']) < 0;

    /// <summary>The directory for a URL path, decoded the way static file servers map URLs to files.</summary>
    private static string PathOf(string url) =>
        Path.Combine([.. url.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString)]);

    /// <summary>The file for a page, refusing anything that would land outside the output directory.</summary>
    private static string FileFor(string output, string url)
    {
        var target = Path.GetFullPath(Path.Combine(output, PathOf(url), "index.html"));
        var root = Path.EndsInDirectorySeparator(output) ? output : output + Path.DirectorySeparatorChar;
        return target.StartsWith(root, StringComparison.Ordinal)
            ? target
            : throw new InvalidOperationException($"{url} would be written outside the output directory ({target}).");
    }

    private static async Task WriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, cancellationToken);
    }

    private static bool Contains(string directory, string path)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative == "." || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }
}
