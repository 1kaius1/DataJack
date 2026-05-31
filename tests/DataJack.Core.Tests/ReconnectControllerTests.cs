// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.Events;
using DataJack.Core.Irc;
using DataJack.Net;
using Xunit;

namespace DataJack.Core.Tests;

public sealed class ReconnectControllerTests : IAsyncDisposable
{
    private readonly EventDispatcher _dispatcher = new();

    public ReconnectControllerTests()
    {
        _dispatcher.Start();
    }

    public async ValueTask DisposeAsync()
    {
        await _dispatcher.DisposeAsync();
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Builds an IRCConnection backed by a stream that immediately closes (simulates server
    /// dropping the connection) or can be replaced with a stream that works on reconnect.
    /// </summary>
    private static IRCConnection MakeConnection(
        EventDispatcher dispatcher,
        string serverId,
        Func<NetworkEndpoint, CancellationToken, Task<Stream>>? connectFactory = null)
    {
        INetworkProvider provider = connectFactory is not null
            ? new LambdaNetworkProvider(connectFactory)
            : new FakeNetworkProvider(new ClosedStream());
        return new IRCConnection(serverId, provider, dispatcher);
    }

    /// <summary>Records delays requested by the controller (injected delay function).</summary>
    private static (List<double> delays, Func<TimeSpan, CancellationToken, Task> fn) MakeDelayCapture()
    {
        var delays = new List<double>();
        return (delays, (ts, ct) =>
        {
            delays.Add(ts.TotalSeconds);
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
    }

    // ---------------------------------------------------------------------------
    // ReconnectScheduled / backoff progression
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ReconnectScheduled_EmittedBeforeEachAttempt()
    {
        // Use MaxAttempts=2 with an always-failing connection so the sequence is fully
        // deterministic: ReconnectScheduled×2 → ReconnectFailed, no success that would
        // trigger a second loop from the ClosedStream EOF.
        var scheduled = new System.Collections.Concurrent.ConcurrentBag<ReconnectScheduled>();
        _dispatcher.Subscribe<ReconnectScheduled>(e => scheduled.Add(e));
        var failed = new TaskCompletionSource<ReconnectFailed>();
        _dispatcher.Subscribe<ReconnectFailed>(e => failed.TrySetResult(e));

        var connection = MakeConnection(_dispatcher, "s", (_, _) =>
            throw new IOException("refused"));

        var cfg = new ReconnectController.Config(
            InitialDelaySeconds: 2.0,
            Multiplier: 2.0,
            MaxDelaySeconds: 300.0,
            JitterFraction: 0.0,
            MaxAttempts: 2);

        await using var rc = new ReconnectController("s", connection, _dispatcher,
            new NetworkEndpoint("h", 6667, false), cfg,
            (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });

        await _dispatcher.PublishAsync(new ConnectionClosed("s", null), EventPriority.Critical);

        // Wait for the loop to exhaust both attempts.
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var snap = scheduled.OrderBy(s => s.AttemptNumber).ToList();
        Assert.Equal(2, snap.Count);
        Assert.All(snap, s => Assert.Equal("s", s.Server));
        Assert.Equal(1, snap[0].AttemptNumber);
        Assert.Equal(2, snap[1].AttemptNumber);
        // Delay doubles on the second attempt (no jitter).
        Assert.InRange(snap[0].DelaySeconds, 1.9, 2.1);
        Assert.InRange(snap[1].DelaySeconds, 3.9, 4.1);

        await connection.DisposeAsync();
    }

    [Fact]
    public async Task ReconnectSucceeded_EmittedOnSuccessfulReconnect()
    {
        var succeeded = new TaskCompletionSource<ReconnectSucceeded>();
        _dispatcher.Subscribe<ReconnectSucceeded>(e => succeeded.TrySetResult(e));

        var connection = MakeConnection(_dispatcher, "s", (_, _) =>
            Task.FromResult<Stream>(new ClosedStream()));

        var cfg = new ReconnectController.Config(JitterFraction: 0.0);
        await using var rc = new ReconnectController("s", connection, _dispatcher,
            new NetworkEndpoint("h", 6667, false), cfg,
            (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });

        await _dispatcher.PublishAsync(new ConnectionClosed("s", null), EventPriority.Critical);

        var result = await succeeded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("s", result.Server);
        Assert.Empty(result.RejoinedChannels);

        await connection.DisposeAsync();
    }

    [Fact]
    public async Task ReconnectFailed_EmittedAfterMaxAttempts()
    {
        var failed = new TaskCompletionSource<ReconnectFailed>();
        _dispatcher.Subscribe<ReconnectFailed>(e => failed.TrySetResult(e));

        // Always refuse.
        var connection = MakeConnection(_dispatcher, "s", (_, _) =>
            throw new IOException("refused"));

        var cfg = new ReconnectController.Config(
            InitialDelaySeconds: 0.001,
            Multiplier: 1.0,
            MaxDelaySeconds: 300.0,
            JitterFraction: 0.0,
            MaxAttempts: 3);

        await using var rc = new ReconnectController("s", connection, _dispatcher,
            new NetworkEndpoint("h", 6667, false), cfg,
            (ts, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });

        await _dispatcher.PublishAsync(new ConnectionClosed("s", null), EventPriority.Critical);

        var result = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("s", result.Server);
        Assert.Contains("3", result.Reason);

        await connection.DisposeAsync();
    }

    // ---------------------------------------------------------------------------
    // Jitter
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Delay_AppliesJitterWithinExpectedBounds()
    {
        var scheduled = new TaskCompletionSource<ReconnectScheduled>();
        _dispatcher.Subscribe<ReconnectScheduled>(e => scheduled.TrySetResult(e));

        // Always refuse; we only care about the first ReconnectScheduled delay value.
        var connection = MakeConnection(_dispatcher, "s", (_, _) =>
            throw new IOException("refused"));

        var cfg = new ReconnectController.Config(
            InitialDelaySeconds: 10.0,
            Multiplier: 2.0,
            MaxDelaySeconds: 300.0,
            JitterFraction: 0.2,
            MaxAttempts: 1);

        await using var rc = new ReconnectController("s", connection, _dispatcher,
            new NetworkEndpoint("h", 6667, false), cfg,
            (ts, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });

        await _dispatcher.PublishAsync(new ConnectionClosed("s", null), EventPriority.Critical);

        var result = await scheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // ±20% of 10.0 = [8.0, 12.0]
        Assert.InRange(result.DelaySeconds, 8.0, 12.0);

        await connection.DisposeAsync();
    }

    // ---------------------------------------------------------------------------
    // Cancellation via DisposeAsync
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task DisposeAsync_CancelsInflightReconnectLoop()
    {
        var failed = new TaskCompletionSource<ReconnectFailed>();
        _dispatcher.Subscribe<ReconnectFailed>(e => failed.TrySetResult(e));

        // Delay that blocks until cancelled.
        var connection = MakeConnection(_dispatcher, "s", (_, _) =>
            throw new IOException("refused"));

        var cfg = new ReconnectController.Config(
            InitialDelaySeconds: 60.0,
            JitterFraction: 0.0,
            MaxAttempts: 0);

        var rc = new ReconnectController("s", connection, _dispatcher,
            new NetworkEndpoint("h", 6667, false), cfg,
            async (ts, ct) => await Task.Delay(ts, ct));

        await _dispatcher.PublishAsync(new ConnectionClosed("s", null), EventPriority.Critical);

        // Give the loop a moment to start and enter the delay.
        await Task.Delay(50);

        // Dispose should return promptly (the delay is cancelled).
        var disposeTask = rc.DisposeAsync().AsTask();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(3));

        // ReconnectFailed must NOT have been emitted — we cancelled before exhausting attempts.
        Assert.False(failed.Task.IsCompleted);

        await connection.DisposeAsync();
    }

    // ---------------------------------------------------------------------------
    // Multi-server isolation
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ConnectionClosed_ForDifferentServer_DoesNotTriggerReconnect()
    {
        var scheduled = new List<ReconnectScheduled>();
        _dispatcher.Subscribe<ReconnectScheduled>(e => scheduled.Add(e));

        var connection = MakeConnection(_dispatcher, "server-a", (_, _) =>
            Task.FromResult<Stream>(new ClosedStream()));

        var cfg = new ReconnectController.Config(JitterFraction: 0.0);
        await using var rc = new ReconnectController("server-a", connection, _dispatcher,
            new NetworkEndpoint("h", 6667, false), cfg,
            (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });

        // Publish a close for a different server.
        await _dispatcher.PublishAsync(new ConnectionClosed("server-b", null), EventPriority.Critical);

        await Task.Delay(100);

        Assert.Empty(scheduled);

        await connection.DisposeAsync();
    }

    // ---------------------------------------------------------------------------
    // No concurrent loops
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task MultipleConnectionClosed_DoesNotStartConcurrentLoops()
    {
        int connectAttempts = 0;
        var connection = MakeConnection(_dispatcher, "s", (_, _) =>
        {
            Interlocked.Increment(ref connectAttempts);
            throw new IOException("refused");
        });

        // loopInDelay fires the moment the delay function is entered, guaranteeing
        // the first loop holds the gate when the second event is published.
        var loopInDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var delayGate = new SemaphoreSlim(0, 1);

        var failed = new TaskCompletionSource<ReconnectFailed>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.Subscribe<ReconnectFailed>(e => failed.TrySetResult(e));

        var cfg = new ReconnectController.Config(
            InitialDelaySeconds: 1.0,
            Multiplier: 1.0,
            MaxDelaySeconds: 1.0,
            JitterFraction: 0.0,
            MaxAttempts: 1);

        await using var rc = new ReconnectController("s", connection, _dispatcher,
            new NetworkEndpoint("h", 6667, false), cfg,
            async (ts, ct) =>
            {
                loopInDelay.TrySetResult();
                await delayGate.WaitAsync(ct);
            });

        // Fire the first close; the loop starts and blocks inside the delay.
        await _dispatcher.PublishAsync(new ConnectionClosed("s", null), EventPriority.Critical);
        await loopInDelay.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Fire the second close while the loop holds the gate -- must be dropped.
        await _dispatcher.PublishAsync(new ConnectionClosed("s", null), EventPriority.Critical);

        // Release the delay so the first loop can attempt to connect and fail.
        delayGate.Release();
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Brief pause to let any second loop run if the gate was not properly held.
        await Task.Delay(50);

        Assert.Equal(1, connectAttempts);

        await connection.DisposeAsync();
    }

    // ---------------------------------------------------------------------------
    // MaxDelaySeconds cap
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Delay_CappedAtMaxDelaySeconds()
    {
        var delays = new List<double>();

        var connection = MakeConnection(_dispatcher, "s", (_, _) =>
            throw new IOException("refused"));

        var cfg = new ReconnectController.Config(
            InitialDelaySeconds: 2.0,
            Multiplier: 100.0,    // aggressive growth
            MaxDelaySeconds: 5.0,
            JitterFraction: 0.0,
            MaxAttempts: 3);

        await using var rc = new ReconnectController("s", connection, _dispatcher,
            new NetworkEndpoint("h", 6667, false), cfg,
            (ts, ct) =>
            {
                delays.Add(ts.TotalSeconds);
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        var failed = new TaskCompletionSource<ReconnectFailed>();
        _dispatcher.Subscribe<ReconnectFailed>(e => failed.TrySetResult(e));

        await _dispatcher.PublishAsync(new ConnectionClosed("s", null), EventPriority.Critical);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // First delay: 2.0 (below cap). Subsequent delays should not exceed 5.0.
        Assert.All(delays, d => Assert.InRange(d, 0.0, 5.0));
        Assert.True(delays.Count >= 2);
        // Second delay would be 200 without the cap; must be 5.0.
        Assert.InRange(delays[1], 4.9, 5.0);

        await connection.DisposeAsync();
    }
}

// ---------------------------------------------------------------------------
// Test doubles
// ---------------------------------------------------------------------------

/// <summary>A stream that reports EOF immediately on read (simulates server closing connection).</summary>
internal sealed class ClosedStream : Stream
{
    public override bool CanRead  => true;
    public override bool CanWrite => true;
    public override bool CanSeek  => false;
    public override long Length   => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value)                => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => 0;
    public override void Write(byte[] buffer, int offset, int count) { }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        => ValueTask.FromResult(0);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        => ValueTask.CompletedTask;
}

/// <summary>NetworkProvider whose ConnectAsync is driven by a caller-supplied factory.</summary>
internal sealed class LambdaNetworkProvider(
    Func<NetworkEndpoint, CancellationToken, Task<Stream>> factory) : INetworkProvider
{
    public Task<Stream> ConnectAsync(NetworkEndpoint endpoint, CancellationToken ct = default)
        => factory(endpoint, ct);
}
