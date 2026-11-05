namespace Netjes.Sidecar;

/// <summary>
/// How a production sidecar checks that its Node process still answers: every <paramref name="Interval"/>, a process
/// that has answered no render or value in that time is sent a health ping, and one that gives no answer within
/// <paramref name="Timeout"/> is killed and restarted, as a crashed one is. Tests hand in their own; <see cref="Off"/>
/// checks nothing.
/// </summary>
/// <param name="Interval">How often the process is looked at, and how long it may be silent before it is pinged.</param>
/// <param name="Timeout">How long a ping waits for an answer; by default the larger of RenderTimeout and five seconds.</param>
internal sealed record SidecarWatchdog(TimeSpan Interval, TimeSpan? Timeout = null)
{
    /// <summary>A render may take <see cref="NetjesOptions.RenderTimeout"/>, and a ping never waits less than this.</summary>
    private static readonly TimeSpan MinimumTimeout = TimeSpan.FromSeconds(5);

    public static SidecarWatchdog Default { get; } = new(TimeSpan.FromSeconds(5));

    public static SidecarWatchdog Off { get; } = new(System.Threading.Timeout.InfiniteTimeSpan);

    public bool IsOff => Interval == System.Threading.Timeout.InfiniteTimeSpan;

    /// <summary>How long a ping waits. Read at each ping, as each render reads its own time limit.</summary>
    public TimeSpan TimeoutFor(NetjesOptions options) =>
        Timeout ?? (options.RenderTimeout > MinimumTimeout ? options.RenderTimeout : MinimumTimeout);
}
