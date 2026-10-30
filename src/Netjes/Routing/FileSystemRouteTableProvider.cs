using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Netjes.Routing;

/// <summary>Development routes: scans the app directory and rescans whenever a file in it changes.</summary>
internal sealed class FileSystemRouteTableProvider : IRouteTableProvider, IDisposable
{
    private readonly NetjesContext _context;
    private readonly ILogger<FileSystemRouteTableProvider> _logger;
    private readonly PhysicalFileProvider _files;
    private readonly IDisposable _watch;
    private readonly Lock _gate = new();
    private RouteTable _current;
    private CancellationTokenSource _changed = new();

    public FileSystemRouteTableProvider(NetjesContext context, ILogger<FileSystemRouteTableProvider> logger)
    {
        _context = context;
        _logger = logger;
        _files = new PhysicalFileProvider(context.Root);
        _current = Scan();
        _watch = ChangeToken.OnChange(
            () => _files.Watch($"{context.AppDirectory}/**/*"),
            Rescan);
    }

    public RouteTable Current => Volatile.Read(ref _current);

    public IChangeToken GetChangeToken()
    {
        lock (_gate)
        {
            return new CancellationChangeToken(_changed.Token);
        }
    }

    private void Rescan()
    {
        RouteTable table;
        try
        {
            table = Scan();
        }
        catch (Exception ex) when (ex is RouteConflictException or InvalidPageFileException or IOException)
        {
            _logger.LogError("Keeping the previous routes: {Message}", ex.Message);
            return;
        }

        CancellationTokenSource previous;
        lock (_gate)
        {
            Volatile.Write(ref _current, table);
            previous = _changed;
            _changed = new CancellationTokenSource();
        }

        _logger.LogDebug("Pages changed; {Count} routes", table.Pages.Count);
        previous.Cancel();
        previous.Dispose();
    }

    private RouteTable Scan()
    {
        var directory = Path.Combine(_context.Root, _context.AppDirectory);
        if (!Directory.Exists(directory))
        {
            _logger.LogWarning("'{AppDirectory}' does not exist in {Root}; no pages will be served", _context.AppDirectory, _context.Root);
            return RouteTable.Empty;
        }

        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(_context.Root, file).Replace('\\', '/'));
        return RouteTable.Build(files, _context.AppDirectory);
    }

    public void Dispose()
    {
        _watch.Dispose();
        _files.Dispose();
        _changed.Dispose();
    }
}
