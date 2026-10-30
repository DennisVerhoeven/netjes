using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Netjes;

/// <summary><see cref="NetjesOptions"/> resolved against the host environment into absolute paths and a concrete mode.</summary>
internal sealed class NetjesContext
{
    public NetjesContext(IOptions<NetjesOptions> options, IHostEnvironment environment)
        : this(options, environment, Environment.GetEnvironmentVariable)
    {
    }

    /// <param name="environmentVariable">Reads the process environment (replaced in tests).</param>
    internal NetjesContext(IOptions<NetjesOptions> options, IHostEnvironment environment, Func<string, string?> environmentVariable)
    {
        Options = options.Value;
        Root = Path.GetFullPath(Options.Root ?? ".", environment.ContentRootPath);
        Command = CommandFrom(environmentVariable) ?? (string.IsNullOrEmpty(Options.Command?.Name) ? null : Options.Command);
        TypesFilePath = Options.TypesFile is { } typesFile ? Path.GetFullPath(typesFile, Root) : null;
        Mode = Options.Mode ?? (environment.IsDevelopment() ? NetjesMode.Development : NetjesMode.Production);
        IsTheProgram = Assembly.GetEntryAssembly()?.GetName().Name is { } entry
            && string.Equals(entry, environment.ApplicationName, StringComparison.Ordinal);
        IncludeErrorDetails = Options.IncludeErrorDetails ?? environment.IsDevelopment();
        DraftFrameAncestors = FrameSourcesFrom(Options.SecurityHeaders.DraftFrameAncestors);
        AppDirectory = Options.AppDirectory.Replace('\\', '/').Trim('/');
        PublicDirectory = Path.Combine(Root, Options.PublicDirectory);
        ClientDist = Path.Combine(Root, Options.DistDirectory, "client");
        ServerDist = Path.Combine(Root, Options.DistDirectory, "server");
        ManifestPath = Path.Combine(ClientDist, ".vite", "manifest.json");
        ServerEntry = Path.Combine(ServerDist, "server-entry.js");
        SidecarScript = Options.SidecarScript is { } script
            ? Path.GetFullPath(script, Root)
            : Mode == NetjesMode.Development
                ? FindInNodeModules(Root, Path.Combine("netjes", "sidecar", "server.mjs"))
                : Path.Combine(ServerDist, "netjes-sidecar.mjs");
    }

    // The netjes CLI passes its command in the process environment of the app it starts. Only there: configuration
    // (appsettings, command-line args) must never turn a normal run into an export that deletes a directory.
    private static NetjesCommandOptions? CommandFrom(Func<string, string?> environmentVariable) =>
        environmentVariable("NETJES_COMMAND") is { Length: > 0 } name
            ? new NetjesCommandOptions
            {
                Name = name,
                Output = environmentVariable("NETJES_OUTPUT") is { Length: > 0 } output ? output : null,
                Check = environmentVariable("NETJES_CHECK") is "1" or "true",
            }
            : null;

    // Each goes into the Content-Security-Policy as is, so it must be one source: a ';' would add directives to it.
    private static string? FrameSourcesFrom(string[] sources)
    {
        foreach (var source in sources)
        {
            if (source.Length == 0 || source.Any(c => c <= ' ' || c >= '\x7f' || c is ';' or ',' or '\''))
            {
                throw new InvalidOperationException(
                    $"Netjes:SecurityHeaders:DraftFrameAncestors has '{source}', which is not a site such as https://cms.example.");
            }
        }

        return sources.Length > 0 ? string.Join(' ', sources) : null;
    }

    // Node looks for packages in node_modules of the directory and each ancestor (npm workspaces hoist them up).
    private static string FindInNodeModules(string start, string relativePath)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "node_modules", relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(start, "node_modules", relativePath);
    }

    public NetjesOptions Options { get; }
    public NetjesMode Mode { get; }

    /// <summary>
    /// Whether the app is the program its process runs (dotnet run, an IDE, the netjes CLI, a container), so that its
    /// exit code is the process's: the app's name is that of the entry assembly, as the host sets it by default.
    /// WebApplicationFactory runs the app in the test runner's process, under the app's own name.
    /// </summary>
    public bool IsTheProgram { get; }

    /// <summary>A tooling command to run instead of serving. The Node sidecar starts only for export, which renders the pages.</summary>
    public NetjesCommandOptions? Command { get; }

    /// <summary>Routes come from the page files (development, and commands that run before any build) rather than the manifest.</summary>
    public bool ScansPageFiles => Mode == NetjesMode.Development || Command?.Name is "generate" or "routes";

    public string? TypesFilePath { get; }
    public bool IncludeErrorDetails { get; }

    /// <summary><see cref="NetjesSecurityHeaderOptions.DraftFrameAncestors"/> as <c>frame-ancestors</c> sources, or null.</summary>
    public string? DraftFrameAncestors { get; }
    public string Root { get; }
    public string AppDirectory { get; }
    public string PublicDirectory { get; }
    public string ClientDist { get; }
    public string ServerDist { get; }
    public string ManifestPath { get; }
    public string ServerEntry { get; }
    public string SidecarScript { get; }
}
