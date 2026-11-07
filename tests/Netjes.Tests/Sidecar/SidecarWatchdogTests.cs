using Netjes.Sidecar;

namespace Netjes.Tests.Sidecar;

public class SidecarWatchdogTests
{
    [Theory]
    [InlineData(1, 5)]
    [InlineData(5, 5)]
    [InlineData(8, 8)]
    [InlineData(15, 15)]
    public void TimeoutFor_APingWaitsRenderTimeout_ButNeverLessThanFiveSeconds(int renderTimeoutSeconds, int expectedSeconds)
    {
        var options = new NetjesOptions { RenderTimeout = TimeSpan.FromSeconds(renderTimeoutSeconds) };

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), SidecarWatchdog.Default.TimeoutFor(options));
    }

    [Fact]
    public void Default_LooksAtTheProcessEveryFiveSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), SidecarWatchdog.Default.Interval);
        Assert.False(SidecarWatchdog.Default.IsOff);
    }
}
