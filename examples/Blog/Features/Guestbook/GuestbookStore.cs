namespace Blog.Features.Guestbook;

public sealed record GuestbookEntry(string Name, string Message, DateTimeOffset SignedAt);

/// <summary>
/// An in-memory stand-in for a database. It keeps the newest <see cref="MaxEntries"/> entries: signing is anonymous, so an
/// unbounded list would let one client grow the process without limit.
/// </summary>
public sealed class GuestbookStore(TimeProvider clock)
{
    public const int MaxEntries = 200;

    private readonly Lock _gate = new();
    private readonly List<GuestbookEntry> _entries = [];

    public GuestbookStore() : this(TimeProvider.System)
    {
    }

    public IReadOnlyList<GuestbookEntry> Recent(int count = 20)
    {
        lock (_gate)
        {
            return _entries.OrderByDescending(e => e.SignedAt).Take(count).ToList();
        }
    }

    public void Sign(string name, string message)
    {
        lock (_gate)
        {
            _entries.Add(new GuestbookEntry(name, message, clock.GetUtcNow()));
            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
            }
        }
    }
}
