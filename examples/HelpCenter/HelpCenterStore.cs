namespace Netjes.Examples.HelpCenter;

/// <summary>A help article: its URL segment, its title and a few paragraphs.</summary>
public sealed record HelpArticle(string Slug, string Title, string Summary, IReadOnlyList<string> Body);

/// <summary>How many readers found an article helpful, and how many did not.</summary>
public sealed record HelpVotes(int Helpful, int NotHelpful);

/// <summary>A note a reader left on an article.</summary>
public sealed record HelpNote(string Slug, string Comment);

/// <summary>A message from the contact page, with the number its sender was given.</summary>
public sealed record HelpMessage(int Number, string Email, string Message);

/// <summary>
/// The articles, and what readers sent about them, in memory: it stands in for a database in this example. It keeps the
/// newest <see cref="MaxNotes"/> notes and <see cref="MaxMessages"/> messages, and counts every one: readers write to it
/// without signing in, so unbounded lists would let one client grow the process without limit.
/// </summary>
public sealed class HelpCenterStore
{
    public const int MaxNotes = 200;

    public const int MaxMessages = 200;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, (int Helpful, int NotHelpful)> _votes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _noteCounts = new(StringComparer.Ordinal);
    private readonly List<HelpNote> _notes = [];
    private readonly List<HelpMessage> _messages = [];
    private int _messageCount;

    public IReadOnlyList<HelpArticle> Articles { get; } =
    [
        new(
            "getting-started",
            "Getting started",
            "What the help center covers, and where to look first.",
            [
                "The articles here answer the questions readers ask most. Each one fits on a screen.",
                "At the end of an article, tell us whether it helped, or what was missing. We read every note.",
            ]),
        new(
            "your-account",
            "Your account",
            "Changing your name, your email address or your password.",
            [
                "Your account page lists what you can change yourself.",
                "An email address is changed once you follow the link we send to the new address.",
            ]),
        new(
            "privacy",
            "Privacy",
            "What we keep about you, for how long, and how to ask for a copy.",
            [
                "We keep what you give us and what the service needs to run, for as long as your account exists.",
                "Ask for a copy of your data through the contact page; it arrives within a month.",
            ]),
    ];

    public HelpArticle? Find(string slug) => Articles.FirstOrDefault(a => a.Slug == slug);

    public HelpVotes VotesOf(string slug)
    {
        lock (_lock)
        {
            var (helpful, notHelpful) = _votes.GetValueOrDefault(slug);
            return new(helpful, notHelpful);
        }
    }

    public HelpVotes Vote(string slug, bool helpful)
    {
        lock (_lock)
        {
            var (yes, no) = _votes.GetValueOrDefault(slug);
            _votes[slug] = helpful ? (yes + 1, no) : (yes, no + 1);
            return new(_votes[slug].Helpful, _votes[slug].NotHelpful);
        }
    }

    public int FeedbackCount(string slug)
    {
        lock (_lock)
        {
            return _noteCounts.GetValueOrDefault(slug);
        }
    }

    public void AddFeedback(string slug, string comment)
    {
        lock (_lock)
        {
            _noteCounts[slug] = _noteCounts.GetValueOrDefault(slug) + 1;
            KeepNewest(_notes, new HelpNote(slug, comment), MaxNotes);
        }
    }

    /// <summary>The notes readers left, the newest first.</summary>
    public IReadOnlyList<HelpNote> RecentNotes()
    {
        lock (_lock)
        {
            return [.. Enumerable.Reverse(_notes)];
        }
    }

    /// <summary>Keeps a message from the contact page; returns its number, for the reader to quote.</summary>
    public int AddMessage(string email, string message)
    {
        lock (_lock)
        {
            KeepNewest(_messages, new HelpMessage(++_messageCount, email, message), MaxMessages);
            return _messageCount;
        }
    }

    /// <summary>The messages from the contact page, the newest first.</summary>
    public IReadOnlyList<HelpMessage> RecentMessages()
    {
        lock (_lock)
        {
            return [.. Enumerable.Reverse(_messages)];
        }
    }

    private static void KeepNewest<T>(List<T> items, T item, int max)
    {
        items.Add(item);
        if (items.Count > max)
        {
            items.RemoveRange(0, items.Count - max);
        }
    }
}
