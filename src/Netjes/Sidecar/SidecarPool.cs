using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Netjes.Rendering;

namespace Netjes.Sidecar;

/// <summary>
/// The Node processes that render pages: <see cref="NetjesOptions.SidecarProcesses"/> in production, one in development
/// (it hosts the Vite dev server). Each render goes to the ready process with the fewest renders in flight; a streamed
/// render keeps its process until the stream is disposed, so deferred values reach the process that is waiting for them.
/// A render has its process from the moment <see cref="RenderStreamAsync"/> is called, before that call returns, which
/// is when <c>PageRenderer</c> starts delivering values. A process that does not take a render (it has exited, or
/// refused or reset the connection) stops counting as ready: it is exiting, though .NET may not have handled that yet,
/// or on Windows its accept queue is full. The render reached nobody, so it goes to the next pick, as does one whose
/// pick is not ready when it is tried: none was, or it was taken out just after it was picked. One with deferred
/// values stays, since they are sent to its process, and waits for that one to take renders again.
/// </summary>
internal sealed class SidecarPool : ISsrRenderer, IAsyncDisposable
{
    private readonly Member[] _members;
    private readonly ConcurrentDictionary<string, Member> _streaming = new(StringComparer.Ordinal);
    private int _next = -1;

    public SidecarPool(NetjesContext context, ILoggerFactory loggers)
        : this(context, loggers, killTree: null)
    {
    }

    /// <param name="killTree">
    /// Kills a process and the processes it started, for each sidecar; tests hand in their own. Internal, so the
    /// container, which only uses public constructors, never fills it in.
    /// </param>
    internal SidecarPool(NetjesContext context, ILoggerFactory loggers, Action<Process>? killTree)
    {
        if (context.Options.SidecarProcesses < 1)
        {
            throw new InvalidOperationException(
                $"Netjes:SidecarProcesses must be at least 1 (it is {context.Options.SidecarProcesses}).");
        }

        var count = context.Mode == NetjesMode.Development ? 1 : context.Options.SidecarProcesses;
        _members = [.. Enumerable.Range(0, count)
            .Select(_ => new Member(new NodeSidecar(context, loggers.CreateLogger<NodeSidecar>(), killTree: killTree)))];
    }

    public int Count => _members.Length;

    public int ReadyCount => _members.Count(m => m.Sidecar.IsReady);

    /// <summary>The first process: in development the only one, hosting the Vite dev server.</summary>
    public NodeSidecar Primary => _members[0].Sidecar;

    public Task StartAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(_members.Select(m => m.Sidecar.StartAsync(cancellationToken)));

    public Task StopAsync() => Task.WhenAll(_members.Select(m => m.Sidecar.StopAsync()));

    public async Task<string> RenderAsync(string payloadJson, CancellationToken cancellationToken) =>
        await RenderOnAsync(Pick(), sidecar => sidecar.TryRenderAsync(payloadJson, cancellationToken))
        // Not ready when tried, or did not take it: the next pick is a ready process if there is one, or else waits for
        // a restart.
        ?? await RenderOnAsync(Pick(), sidecar => sidecar.RenderAsync(payloadJson, cancellationToken));

    public async Task<Stream> RenderStreamAsync(string payloadJson, SsrStreamOptions options, CancellationToken cancellationToken)
    {
        // Synchronous up to the first await: values delivered once this has been called reach the chosen process.
        if (options.RenderId is not null)
        {
            // A render with deferred values stays on that process, where they are sent, and waits for it if need be.
            return await StreamOnAsync(Pick(), options, sidecar => sidecar.RenderStreamAsync(payloadJson, options, cancellationToken));
        }

        return await TryStreamOnAsync(Pick(), options, sidecar => sidecar.TryRenderStreamAsync(payloadJson, options, cancellationToken))
            // Not ready when tried, or did not take it: the next pick is a ready process if there is one, or else waits.
            ?? await StreamOnAsync(Pick(), options, sidecar => sidecar.RenderStreamAsync(payloadJson, options, cancellationToken));
    }

    public Task ResolveAsync(string renderId, string id, string? valueJson, string? error, CancellationToken cancellationToken) =>
        // A render that is no longer streaming has nobody waiting for the value.
        _streaming.TryGetValue(renderId, out var member)
            ? member.Sidecar.ResolveAsync(renderId, id, valueJson, error, cancellationToken)
            : Task.CompletedTask;

    /// <summary>
    /// Disposes the processes together, as <see cref="StopAsync"/> stops them: disposed without a stop first, each one
    /// slow to exit waits out its grace, and one after another those would add up.
    /// </summary>
    public async ValueTask DisposeAsync() =>
        await Task.WhenAll(_members.Select(m => m.Sidecar.DisposeAsync().AsTask()));

    private static async Task<T> RenderOnAsync<T>(Member member, Func<NodeSidecar, Task<T>> render)
    {
        Interlocked.Increment(ref member.InFlight);
        try
        {
            return await render(member.Sidecar);
        }
        finally
        {
            Interlocked.Decrement(ref member.InFlight);
        }
    }

    /// <summary>A stream rendered on <paramref name="member"/>, which keeps it until the stream is disposed.</summary>
    private async Task<Stream> StreamOnAsync(Member member, SsrStreamOptions options, Func<NodeSidecar, Task<Stream>> render) =>
        await TryStreamOnAsync(member, options, async sidecar => await render(sidecar)) ?? throw new UnreachableException();

    /// <summary>Like <see cref="StreamOnAsync"/>, or null when the process did not take the render.</summary>
    private async Task<Stream?> TryStreamOnAsync(Member member, SsrStreamOptions options, Func<NodeSidecar, Task<Stream?>> render)
    {
        Interlocked.Increment(ref member.InFlight);
        if (options.RenderId is { } renderId)
        {
            _streaming[renderId] = member;
        }

        var released = 0;
        void Release()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                Interlocked.Decrement(ref member.InFlight);
                if (options.RenderId is { } id)
                {
                    _streaming.TryRemove(new KeyValuePair<string, Member>(id, member));
                }
            }
        }

        try
        {
            if (await render(member.Sidecar) is { } stream)
            {
                return new LeasedStream(stream, Release);
            }
        }
        catch
        {
            Release();
            throw;
        }

        Release();
        return null;
    }

    /// <summary>The ready process with the fewest renders in flight; ties rotate, so idle processes share the work.</summary>
    private Member Pick()
    {
        if (_members.Length == 1)
        {
            return _members[0];
        }

        var start = (int)((uint)Interlocked.Increment(ref _next) % (uint)_members.Length);
        Member? best = null;
        var bestReady = false;
        for (var i = 0; i < _members.Length; i++)
        {
            var member = _members[(start + i) % _members.Length];
            var ready = member.Sidecar.IsReady;
            if (best is null || (ready && !bestReady) || (ready == bestReady && Volatile.Read(ref member.InFlight) < Volatile.Read(ref best.InFlight)))
            {
                best = member;
                bestReady = ready;
            }
        }

        return best!;
    }

    private sealed class Member(NodeSidecar sidecar)
    {
        public readonly NodeSidecar Sidecar = sidecar;
        public int InFlight;
    }

    /// <summary>A rendered stream that gives its process back when disposed.</summary>
    private sealed class LeasedStream(Stream inner, Action release) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                release();
            }

            base.Dispose(disposing);
        }
    }
}
