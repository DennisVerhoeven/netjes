namespace Netjes.Sidecar;

/// <summary>When to restart a crashed sidecar: exponential backoff that only resets once it has stayed up.</summary>
internal static class RestartPolicy
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StableUptime = TimeSpan.FromSeconds(30);

    public static TimeSpan DelayAfter(int previousRestarts) =>
        previousRestarts >= 5 ? MaxDelay : TimeSpan.FromSeconds(Math.Min(MaxDelay.TotalSeconds, 1 << previousRestarts));

    public static bool IsStable(TimeSpan uptime) => uptime > StableUptime;
}
