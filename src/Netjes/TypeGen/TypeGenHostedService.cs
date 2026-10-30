using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netjes.Routing;

namespace Netjes.TypeGen;

/// <summary>How a command reports its outcome; the process exit code, replaced in tests.</summary>
internal sealed class CommandExit(Action<int> setExitCode)
{
    public CommandExit() : this(code => Environment.ExitCode = code)
    {
    }

    public void Set(int code) => setExitCode(code);
}

/// <summary>
/// Keeps the generated TypeScript current: rewrites it on startup in Development when it changed (unchanged files are
/// left alone so Vite does not reload), and runs the CLI's <c>generate</c> and <c>routes</c> commands.
/// </summary>
/// <remarks>
/// Runs once the host has started: ASP.NET Core only registers the app's endpoint data sources while building the
/// request pipeline, which happens when the web server starts.
/// </remarks>
internal sealed class TypeGenHostedService(
    IServiceProvider services,
    NetjesContext context,
    IHostApplicationLifetime lifetime,
    CommandExit exit,
    ILogger<TypeGenHostedService> logger) : IHostedLifecycleService
{
    // LF, as in the generated types: indented JSON would otherwise break lines with Environment.NewLine, CRLF on Windows.
    private static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true, NewLine = "\n" };

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        if (context.Command is { } command)
        {
            var handled = command.Name switch
            {
                "generate" => await GenerateCommandAsync(command, cancellationToken),
                "routes" => await RoutesCommandAsync(command, cancellationToken),
                _ => (int?)null,
            };
            if (handled is { } code)
            {
                exit.Set(code);
                lifetime.StopApplication();
            }
            else if (command.Name is not "export")
            {
                logger.LogError("Unknown Netjes command '{Command}'. Known commands: generate, routes, export", command.Name);
                exit.Set(2);
                lifetime.StopApplication();
            }

            return;
        }

        if (context.Mode != NetjesMode.Development || context.TypesFilePath is not { } path)
        {
            return;
        }

        try
        {
            if (await WriteIfChangedAsync(path, Generate(), cancellationToken))
            {
                logger.LogInformation("Wrote {TypesFile}", Path.GetRelativePath(context.Root, path));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Development keeps running with the previous file; the page that needs a new type will say so.
            logger.LogWarning(ex, "Could not generate {TypesFile}: {Message}", path, ex.Message);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private string Generate() => services.GetRequiredService<TypeScriptGenerator>().Generate();

    private async Task<int> GenerateCommandAsync(NetjesCommandOptions command, CancellationToken cancellationToken)
    {
        var path = command.Output is { } output ? Path.GetFullPath(output, context.Root) : context.TypesFilePath;
        if (path is null)
        {
            logger.LogError("NetjesOptions.TypesFile is null and no output was given; nothing to generate");
            return 2;
        }

        var content = Generate();
        if (command.Check)
        {
            var current = File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;
            if (current is not null && SameContent(current, content))
            {
                logger.LogInformation("{TypesFile} is up to date", path);
                return 0;
            }

            if (current is null)
            {
                logger.LogError("{TypesFile} does not exist; run `netjes generate`", path);
            }
            else
            {
                logger.LogError("{TypesFile} is out of date; run `netjes generate`\n{Differences}", path, Differences(current, content));
            }

            return 1;
        }

        await WriteIfChangedAsync(path, content, cancellationToken);
        logger.LogInformation("Generated {TypesFile}", path);
        return 0;
    }

    private async Task<int> RoutesCommandAsync(NetjesCommandOptions command, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(DescribeRoutes(), Indented);
        if (command.Output is { } output)
        {
            await File.WriteAllTextAsync(Path.GetFullPath(output, context.Root), json, cancellationToken);
        }
        else
        {
            logger.LogInformation("{Routes}", json);
        }

        return 0;
    }

    private object DescribeRoutes()
    {
        var table = services.GetRequiredService<IRouteTableProvider>().Current;
        var endpoints = services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        var loaders = services.GetRequiredService<PageLoaderRegistry>();

        IEnumerable<RouteEndpoint> PageEndpoints(string pattern, string method) => endpoints.Where(e =>
            e.Metadata.GetMetadata<PageEndpointMetadata>()?.Pattern == pattern
            && (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) ?? method == HttpMethods.Get));

        return new
        {
            Routes = table.Pages.Select(page =>
            {
                var get = PageEndpoints(page.Pattern, HttpMethods.Get).FirstOrDefault();
                return new
                {
                    page.Pattern,
                    page.Template,
                    page.File,
                    page.Layouts,
                    Loader = loaders.Contains(page.Pattern),
                    Action = PageEndpoints(page.Pattern, HttpMethods.Post).Any(),
                    Props = get?.Metadata.GetMetadata<PagePropsMetadata>()?.PropsType.Name,
                    Cached = get?.Metadata.Any(m => m is IOutputCachePolicy or OutputCacheAttribute) ?? false,
                    Authorized = get?.Metadata.GetMetadata<IAuthorizeData>() is not null && get.Metadata.GetMetadata<IAllowAnonymous>() is null,
                };
            }),
            Endpoints = endpoints
                // Pages are the Routes above; server actions carry page metadata without a pattern and belong here.
                .Where(e => e.Metadata.GetMetadata<PageEndpointMetadata>() is null || e.Metadata.GetMetadata<ServerActionMetadata>() is not null)
                .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"]).Select(method => new
                {
                    Method = method,
                    Route = e.RoutePattern.RawText,
                    Name = e.DisplayName,
                }))
                .OrderBy(e => e.Route, StringComparer.Ordinal)
                .ThenBy(e => e.Method, StringComparer.Ordinal),
            // Logged at startup otherwise; here the CLI prints them.
            Warnings = services.GetRequiredService<RouteDiagnostics>().Check().Select(w => new { w.Kind, w.Message, w.Files }),
        };
    }

    private static async Task<bool> WriteIfChangedAsync(string path, string content, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && SameContent(await File.ReadAllTextAsync(path, cancellationToken), content))
        {
            return false;
        }

        // Written next to the file and moved over it, so Vite (watching it in development) never reads half of it.
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, content, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }

        return true;
    }

    private const int MaxDifferenceLines = 20;

    /// <summary>Lines that must agree again for the excerpt to say the files line up after a change.</summary>
    private const int ResyncLines = 3;

    private const int MaxLookAhead = 400;

    /// <summary>
    /// A short unified-diff-like excerpt for the check's message: the first block of changed lines, as `- n: old`
    /// and `+ n: new` with line numbers, capped so a CI log stays readable. Where the files line up again the
    /// block ends; a last line says when they differ further down.
    /// </summary>
    internal static string Differences(string current, string generated)
    {
        var old = current.ReplaceLineEndings("\n").Split('\n');
        var fresh = generated.ReplaceLineEndings("\n").Split('\n');
        var start = 0;
        while (start < old.Length && start < fresh.Length && old[start] == fresh[start])
        {
            start++;
        }

        var end = 0;
        while (end < old.Length - start && end < fresh.Length - start && old[^(end + 1)] == fresh[^(end + 1)])
        {
            end++;
        }

        var oldEnd = old.Length - end;
        var freshEnd = fresh.Length - end;
        var (oldBlockEnd, freshBlockEnd, resynced) = FirstBlock(old, fresh, start, oldEnd, freshEnd);

        var lines = new List<string>();
        var removed = old[start..oldBlockEnd];
        var added = fresh[start..freshBlockEnd];
        if (removed.Length == 0 && added is [""] && freshBlockEnd == fresh.Length)
        {
            lines.Add("(no newline at end of file)");
        }
        else
        {
            // The side that needs less leaves the rest of the cap to the other.
            var half = MaxDifferenceLines / 2;
            var removedShown = Math.Min(removed.Length, Math.Max(half, MaxDifferenceLines - added.Length));
            var addedShown = Math.Min(added.Length, MaxDifferenceLines - removedShown);
            lines.AddRange(removed.Take(removedShown).Select((line, i) => $"- {start + i + 1}: {line}"));
            lines.AddRange(added.Take(addedShown).Select((line, i) => $"+ {start + i + 1}: {line}"));
            var hidden = removed.Length - removedShown + added.Length - addedShown;
            if (hidden > 0)
            {
                lines.Add($"... and {hidden} more differing lines");
            }
        }

        if (resynced && (oldBlockEnd < oldEnd || freshBlockEnd < freshEnd))
        {
            lines.Add($"... the file differs further down, up to line {oldEnd} (committed) / {freshEnd} (generated)");
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Where the first changed block ends: the nearest pair of positions after <paramref name="start"/> from which
    /// a few lines agree again. Without one (or beyond the look-ahead) the block is everything that differs.
    /// </summary>
    private static (int OldEnd, int FreshEnd, bool Resynced) FirstBlock(string[] old, string[] fresh, int start, int oldEnd, int freshEnd)
    {
        var maxOld = Math.Min(MaxLookAhead, old.Length - start);
        var maxFresh = Math.Min(MaxLookAhead, fresh.Length - start);
        for (var distance = 0; distance <= maxOld + maxFresh; distance++)
        {
            for (var i = Math.Max(0, distance - maxFresh); i <= Math.Min(distance, maxOld); i++)
            {
                var j = distance - i;
                if (i + start >= oldEnd && j + start >= freshEnd)
                {
                    // Both sides are into the shared end already.
                    return (oldEnd, freshEnd, false);
                }

                if (Agrees(old, start + i, fresh, start + j))
                {
                    return (start + i, start + j, true);
                }
            }
        }

        return (oldEnd, freshEnd, false);
    }

    private static bool Agrees(string[] old, int i, string[] fresh, int j)
    {
        for (var k = 0; k < ResyncLines; k++)
        {
            if (i + k >= old.Length || j + k >= fresh.Length || old[i + k] != fresh[j + k])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Equal apart from line endings: a checkout with core.autocrlf has CRLF, the generator writes LF.</summary>
    private static bool SameContent(string current, string generated) =>
        string.Equals(current.ReplaceLineEndings("\n"), generated.ReplaceLineEndings("\n"), StringComparison.Ordinal);
}
