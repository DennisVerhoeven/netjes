namespace Netjes.Examples.HelpCenter;

/// <summary>A help article: its URL segment, its title and a few paragraphs.</summary>
public sealed record HelpArticle(string Slug, string Title, string Summary, IReadOnlyList<string> Body);

/// <summary>How many readers found an article helpful, and how many did not.</summary>
public sealed record HelpVotes(int Helpful, int NotHelpful);

/// <summary>The articles, and what readers sent about them, in memory: it stands in for a database in this example.</summary>
public sealed class HelpCenterStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (int Helpful, int NotHelpful)> _votes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _feedback = new(StringComparer.Ordinal);
    private readonly List<(string Email, string Message)> _messages = [];

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
            return _feedback.TryGetValue(slug, out var notes) ? notes.Count : 0;
        }
    }

    public void AddFeedback(string slug, string comment)
    {
        lock (_lock)
        {
            if (!_feedback.TryGetValue(slug, out var notes))
            {
                _feedback[slug] = notes = [];
            }

            notes.Add(comment);
        }
    }

    /// <summary>Keeps a message from the contact page; returns its number, for the reader to quote.</summary>
    public int AddMessage(string email, string message)
    {
        lock (_lock)
        {
            _messages.Add((email, message));
            return _messages.Count;
        }
    }
}
