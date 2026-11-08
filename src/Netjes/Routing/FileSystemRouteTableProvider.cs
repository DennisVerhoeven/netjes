using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Netjes.Routing;

/// <summary>
/// Development routes: scans the app directory and the pages folder of each mounted page package, and rescans whenever a
/// file in one of them changes. Each package is watched at its real path, so a linked one (an npm workspace) is too.
/// </summary>
internal sealed class FileSystemRouteTableProvider : IRouteTableProvider, IDisposable
{
    private readonly NetjesContext _context;
    private readonly RouteSources _sources;
    private readonly ILogger<FileSystemRouteTableProvider> _logger;
    private readonly PhysicalFileProvider _files;
    private readonly IDisposable _watch;
    // The packages' folders of pages being watched, by package name.
    private readonly Dictionary<string, (PhysicalFileProvider Files, IDisposable Watch)> _packageWatches = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    // One scan at a time: a rescan and a rebuild for a new mount publish in the order they scanned.
    private readonly Lock _scan = new();
    private Built _current;
    private CancellationTokenSource _changed = new();

    private sealed record Built(RouteTable Table, int SourcesVersion);

    public FileSystemRouteTableProvider(NetjesContext context, RouteSources sources, ILogger<FileSystemRouteTableProvider> logger)
    {
        _context = context;
        _sources = sources;
        _logger = logger;
        _files = new PhysicalFileProvider(context.Root);
        var version = sources.Version;
        var mounts = sources.Mounts;
        _current = new Built(Scan(mounts), version);
        Watch(mounts);
        _watch = ChangeToken.OnChange(
            () => _files.Watch($"{context.AppDirectory}/**/*"),
            Rescan);
    }

    public RouteTable Current
    {
        get
        {
            var current = Volatile.Read(ref _current);
            if (current.SourcesVersion == _sources.Version)
            {
                return current.Table;
            }

            // A package was mounted: build the table with it, throwing what is wrong with it to MapPagePackage.
            lock (_scan)
            {
                current = Volatile.Read(ref _current);
                if (current.SourcesVersion != _sources.Version)
                {
                    var version = _sources.Version;
                    var mounts = _sources.Mounts;
                    current = new Built(Scan(mounts), version);
                    Watch(mounts);
                    Publish(current);
                }

                return current.Table;
            }
        }
    }

    public IChangeToken GetChangeToken()
    {
        lock (_gate)
        {
            return new CancellationChangeToken(_changed.Token);
        }
    }

    private void Rescan()
    {
        Built built;
        lock (_scan)
        {
            try
            {
                // The version first: a package mounted in between is then scanned again by the next read of Current.
                var version = _sources.Version;
                built = new Built(Scan(_sources.Mounts), version);
                // MapPages() has thrown the conflicts it found; there is no override left to resolve one found now.
                if (_sources.PagesMapped && built.Table.PendingConflicts is [var conflict, ..])
                {
                    throw conflict.ToException();
                }
            }
            catch (Exception ex) when (ex is RouteConflictException or InvalidPageFileException or IOException)
            {
                _logger.LogError("Keeping the previous routes: {Message}", ex.Message);
                return;
            }

            Publish(built);
        }

        _logger.LogDebug("Pages changed; {Count} routes", built.Table.Pages.Count);
    }

    private void Publish(Built built)
    {
        CancellationTokenSource previous;
        lock (_gate)
        {
            Volatile.Write(ref _current, built);
            previous = _changed;
            _changed = new CancellationTokenSource();
        }

        previous.Cancel();
        previous.Dispose();
    }

    // Each package's folder of pages, at its real path: a watcher on the path through a link may not see its changes.
    private void Watch(IReadOnlyList<MountedPackage> mounts)
    {
        foreach (var mounted in mounts.Where(m => !_packageWatches.ContainsKey(m.Name)))
        {
            var pages = PagesFolder(mounted);
            if (!Directory.Exists(pages))
            {
                continue;
            }

            var files = new PhysicalFileProvider(pages);
            _packageWatches[mounted.Name] = (files, ChangeToken.OnChange(() => files.Watch("**/*"), Rescan));
        }
    }

    private RouteTable Scan(IReadOnlyList<MountedPackage> mounts)
    {
        var directory = Path.Combine(_context.Root, _context.AppDirectory);
        var exists = Directory.Exists(directory);
        if (!exists && mounts.Count == 0)
        {
            _logger.LogWarning("'{AppDirectory}' does not exist in {Root}; no pages will be served", _context.AppDirectory, _context.Root);
            return RouteTable.Empty;
        }

        var files = exists
            ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(_context.Root, file).Replace('\\', '/'))
            : [];
        return RouteTable.Build(
            [PageSource.App(files, _context.AppDirectory), .. mounts.Select(m => m.SourceOf(PackageFiles(m)))],
            _sources.Overrides);
    }

    private static string PagesFolder(MountedPackage mounted) => Path.Combine(mounted.Install.Directory!, mounted.Install.Pages);

    // As the Vite plugin lists them: leaving out node_modules and hidden folders.
    private static IEnumerable<string> PackageFiles(MountedPackage mounted)
    {
        var pages = PagesFolder(mounted);
        if (!Directory.Exists(pages))
        {
            return [];
        }

        return Directory.EnumerateFiles(pages, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(pages, file).Replace('\\', '/'))
            .Where(file => file.Split('/')[..^1].All(folder => folder != "node_modules" && !folder.StartsWith('.')))
            .Select(file => $"{mounted.Name}/{mounted.Install.Pages}/{file}");
    }

    public void Dispose()
    {
        _watch.Dispose();
        _files.Dispose();
        foreach (var (files, watch) in _packageWatches.Values)
        {
            watch.Dispose();
            files.Dispose();
        }

        _changed.Dispose();
    }
}
