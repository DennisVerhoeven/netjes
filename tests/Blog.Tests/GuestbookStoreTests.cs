using Blog.Features.Guestbook;

namespace Blog.Tests;

public class GuestbookStoreTests
{
    [Fact]
    public void TheStore_KeepsOnlyTheNewestEntries()
    {
        var clock = new StepClock();
        var store = new GuestbookStore(clock);

        for (var i = 0; i < 1000; i++)
        {
            store.Sign($"Visitor {i}", "Hello");
        }

        var kept = store.Recent(int.MaxValue);
        Assert.Equal(GuestbookStore.MaxEntries, kept.Count);
        Assert.Equal("Visitor 999", kept[0].Name);
        Assert.DoesNotContain(kept, e => e.Name == "Visitor 0");
    }

    [Fact]
    public void TheStore_ShowsTheNewestEntriesFirst()
    {
        var store = new GuestbookStore(new StepClock());
        store.Sign("First", "a");
        store.Sign("Second", "b");

        Assert.Equal(["Second", "First"], store.Recent().Select(e => e.Name));
    }

    /// <summary>A clock that moves one second on every read, so entries have distinct times.</summary>
    private sealed class StepClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now = _now.AddSeconds(1);
    }
}
