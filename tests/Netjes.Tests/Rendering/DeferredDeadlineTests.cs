using Netjes.Rendering;

namespace Netjes.Tests.Rendering;

public class DeferredDeadlineTests
{
    // The sidecar ends a crawler's complete document at StreamTimeout; values that never arrived are reported as failed
    // before then, so the document shows them failed instead of being cut off.
    [Theory]
    [InlineData(600, 540)]
    [InlineData(5_000, 4_500)]
    [InlineData(10_000, 9_000)]
    [InlineData(60_000, 59_000)]
    public void DeferredDeadline_IsATenthBeforeTheStreamTimeout_AtMostASecond(int streamTimeoutMs, int expectedMs)
    {
        var deadline = PageRenderer.DeferredDeadline(TimeSpan.FromMilliseconds(streamTimeoutMs));

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), deadline);
        Assert.True(deadline < TimeSpan.FromMilliseconds(streamTimeoutMs));
    }
}
