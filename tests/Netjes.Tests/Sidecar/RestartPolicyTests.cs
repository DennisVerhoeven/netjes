using Netjes.Sidecar;

namespace Netjes.Tests.Sidecar;

public class RestartPolicyTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(3, 8)]
    [InlineData(5, 30)]
    [InlineData(50, 30)]
    public void DelayAfter_ConsecutiveCrashes_GrowsExponentiallyUpToThirtySeconds(int previousRestarts, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), RestartPolicy.DelayAfter(previousRestarts));
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(29, false)]
    [InlineData(31, true)]
    public void IsStable_OnlyAfterRunningLongEnough_ResetsTheBackoff(int upSeconds, bool expected)
    {
        Assert.Equal(expected, RestartPolicy.IsStable(TimeSpan.FromSeconds(upSeconds)));
    }
}
