using Netjes.Examples.HelpCenter;

namespace Blog.Tests;

/// <summary>
/// The help center's in-memory store, which anonymous visitors write to: it keeps the newest notes and messages only,
/// so one client cannot grow the process without limit.
/// </summary>
public class HelpCenterStoreTests
{
    [Fact]
    public void TheStore_KeepsOnlyTheNewestNotes_AndCountsEveryOne()
    {
        var store = new HelpCenterStore();

        for (var i = 0; i < 1000; i++)
        {
            store.AddFeedback("privacy", $"Note {i}");
        }

        var kept = store.RecentNotes();
        Assert.Equal(HelpCenterStore.MaxNotes, kept.Count);
        Assert.Equal("Note 999", kept[0].Comment);
        Assert.DoesNotContain(kept, n => n.Comment == "Note 0");
        Assert.Equal(1000, store.FeedbackCount("privacy"));
    }

    [Fact]
    public void TheStore_KeepsOnlyTheNewestMessages_AndNumbersEveryOne()
    {
        var store = new HelpCenterStore();

        var numbers = Enumerable.Range(0, 1000).Select(i => store.AddMessage("reader@example.com", $"Message {i}")).ToList();

        var kept = store.RecentMessages();
        Assert.Equal(HelpCenterStore.MaxMessages, kept.Count);
        Assert.Equal("Message 999", kept[0].Message);
        Assert.DoesNotContain(kept, m => m.Message == "Message 0");
        Assert.Equal(Enumerable.Range(1, 1000), numbers);
    }
}
