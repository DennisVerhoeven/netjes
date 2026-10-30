using System.Collections.Concurrent;
using Microsoft.Extensions.FileProviders;

namespace Netjes.Images;

/// <summary>
/// The file system the image endpoint reads local images from: <c>public/</c> (the client build in production), and
/// the names its directories store. A service, so that tests can stand in a file system that resolves names the way
/// another platform's does.
/// </summary>
internal class LocalImageFiles
{
    /// <summary>
    /// How many paths found spelled as stored are remembered. Only those are: a refused spelling is listed again each
    /// time, so other spellings cannot fill it, and each entry is the path of a real file. When it is full it starts over.
    /// </summary>
    internal const int Capacity = 4096;

    // Hidden and system entries too: a file under a dot directory is served, so its directory's name has to be found.
    private static readonly EnumerationOptions AllEntries = new() { AttributesToSkip = 0 };

    // The paths found spelled as stored, by the path alone: an app has one root.
    private readonly ConcurrentDictionary<string, bool> _stored = new(StringComparer.Ordinal);

    /// <summary>How many paths are remembered now.</summary>
    internal int Remembered => _stored.Count;

    /// <summary>The files under <paramref name="root"/>, through a <see cref="PhysicalFileProvider"/> (no path escapes it).</summary>
    public virtual IFileProvider Open(string root) => new PhysicalFileProvider(root);

    /// <summary>
    /// Whether each segment of <paramref name="path"/> (canonical, and found under <paramref name="root"/>) is the name
    /// its directory stores, compared ordinally. Windows and macOS open <c>/img/hero.png</c> for <c>/img/HERO.png</c>
    /// too, and Windows for an 8.3 short name (<c>HERO~1.PNG</c>) or a segment with a trailing dot or space; each
    /// spelling would be another cache entry and ETag for the same file. A path is listed once, then remembered: a
    /// file renamed while the app runs keeps its old spelling until the entries start over or the app restarts.
    /// </summary>
    public bool IsStoredSpelling(string root, string path)
    {
        if (_stored.ContainsKey(path))
        {
            return true;
        }

        var directory = root;
        foreach (var segment in path[1..].Split('/'))
        {
            if (!Holds(directory, segment))
            {
                return false;
            }

            directory = Path.Join(directory, segment);
        }

        // Full: start over rather than track which entries are used. The ones still requested come back on their next check.
        if (_stored.Count >= Capacity)
        {
            _stored.Clear();
        }

        _stored[path] = true;
        return true;
    }

    /// <summary>The names of the entries in <paramref name="directory"/>, spelled as the file system stores them.</summary>
    public virtual IEnumerable<string> NamesIn(string directory) =>
        new DirectoryInfo(directory).EnumerateFileSystemInfos("*", AllEntries).Select(entry => entry.Name);

    private bool Holds(string directory, string name)
    {
        try
        {
            return NamesIn(directory).Contains(name, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
