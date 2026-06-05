// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.Irc;
using Xunit;

namespace DataJack.Core.Tests;

public sealed class IdleMonitorTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    // Delay factory that blocks until the returned TaskCompletionSource is resolved.
    // Lets tests subscribe to events before the countdown can fire, eliminating the
    // race where Task.CompletedTask would let the countdown fire before subscriptions
    // are registered.
    private static (Func<int, CancellationToken, Task> factory, TaskCompletionSource gate)
        BlockingDelay()
    {
        var gate = new TaskCompletionSource();
        return (async (_, ct) =>
        {
            using var reg = ct.Register(() => gate.TrySetCanceled());
            await gate.Task.ConfigureAwait(false);
        }, gate);
    }

    // Delay factory with two independent gates: gate[0] controls the first countdown,
    // gate[1] controls the second. Subsequent calls reuse gate[1] to stay in-bounds.
    private static (Func<int, CancellationToken, Task> factory, TaskCompletionSource[] gates)
        TwoGateDelay()
    {
        var gates = new[] { new TaskCompletionSource(), new TaskCompletionSource() };
        int callIndex = -1;
        return (async (_, ct) =>
        {
            int i = Math.Min(Interlocked.Increment(ref callIndex), gates.Length - 1);
            using var reg = ct.Register(() => gates[i].TrySetCanceled());
            await gates[i].Task.ConfigureAwait(false);
        }, gates);
    }

    // ---------------------------------------------------------------------------
    // Construction
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Constructor_ZeroOrNegativeDelay_Throws(int delay)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IdleMonitor(delay));
    }

    [Fact]
    public void Constructor_PositiveDelay_DoesNotThrow()
    {
        using var monitor = new IdleMonitor(60, (_, _) => Task.Delay(Timeout.Infinite));
        // simply constructed without error
    }

    // ---------------------------------------------------------------------------
    // IdleTripped
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task IdleTripped_FiredWhenDelayElapses()
    {
        // BlockingDelay lets us subscribe before the countdown can fire, eliminating
        // the race where a free thread-pool thread fires IdleTripped before the += line.
        var (factory, gate) = BlockingDelay();
        var fired = new TaskCompletionSource();

        using var monitor = new IdleMonitor(1, factory);
        monitor.IdleTripped += () => fired.TrySetResult();

        gate.TrySetResult();
        await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task IdleTripped_FiredExactlyOnce_PerIdleCycle()
    {
        var (factory, gate) = BlockingDelay();
        int count = 0;
        var fired = new TaskCompletionSource();

        using var monitor = new IdleMonitor(1, factory);
        monitor.IdleTripped += () =>
        {
            Interlocked.Increment(ref count);
            fired.TrySetResult();
        };

        gate.TrySetResult();
        await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Wait a little longer to catch spurious double-fires.
        await Task.Delay(50);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task IdleTripped_NotFired_WhenActivityBeforeDelay()
    {
        var (factory, gate) = BlockingDelay();
        bool fired = false;

        using var monitor = new IdleMonitor(1, factory);
        monitor.IdleTripped += () => fired = true;

        // Simulate user activity; this cancels the blocked delay.
        monitor.NotifyActivity();

        // The delay was cancelled; let the dust settle.
        await Task.Delay(50);

        Assert.False(fired);
        gate.TrySetCanceled();
    }

    // ---------------------------------------------------------------------------
    // ActivityResumed
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ActivityResumed_FiredWhenUserTypesAfterIdle()
    {
        var (factory, gate) = BlockingDelay();
        var idleGate    = new TaskCompletionSource();
        var resumedGate = new TaskCompletionSource();

        using var monitor = new IdleMonitor(1, factory);
        monitor.IdleTripped     += () => idleGate.TrySetResult();
        monitor.ActivityResumed += () => resumedGate.TrySetResult();

        // Release the countdown; wait for idle to trip.
        gate.TrySetResult();
        await idleGate.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Simulate user typing.
        monitor.NotifyActivity();

        await resumedGate.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ActivityResumed_NotFired_WhenNoIdleCycleOccurred()
    {
        var (factory, gate) = BlockingDelay();
        bool resumed = false;

        using var monitor = new IdleMonitor(1, factory);
        monitor.ActivityResumed += () => resumed = true;

        // User types before the delay elapses.
        monitor.NotifyActivity();
        await Task.Delay(50);

        Assert.False(resumed);
        gate.TrySetCanceled();
    }

    [Fact]
    public async Task ActivityResumed_FiredExactlyOnce_OnFirstKeystrokeAfterIdle()
    {
        var (factory, gate) = BlockingDelay();
        var idleGate = new TaskCompletionSource();
        int resumeCount = 0;

        using var monitor = new IdleMonitor(1, factory);
        monitor.IdleTripped     += () => idleGate.TrySetResult();
        monitor.ActivityResumed += () => Interlocked.Increment(ref resumeCount);

        gate.TrySetResult();
        await idleGate.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Two rapid keystrokes.
        monitor.NotifyActivity();
        monitor.NotifyActivity();

        await Task.Delay(50);

        Assert.Equal(1, resumeCount);
    }

    // ---------------------------------------------------------------------------
    // Multi-cycle: idle -> resume -> idle again
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task IdleAndResume_TwoCycles_BothFire()
    {
        // TwoGateDelay gives explicit control over when each countdown fires:
        // release gates[0] for cycle 1, gates[1] for cycle 2. This eliminates
        // the race where InstantDelay (Task.CompletedTask) fires before subscriptions.
        var (factory, gates) = TwoGateDelay();
        int idleCount   = 0;
        int resumeCount = 0;

        var cycle1Idle   = new TaskCompletionSource();
        var cycle1Resume = new TaskCompletionSource();
        var cycle2Idle   = new TaskCompletionSource();

        using var monitor = new IdleMonitor(1, factory);
        monitor.IdleTripped += () =>
        {
            if (Interlocked.Increment(ref idleCount) == 1) cycle1Idle.TrySetResult();
            else                                            cycle2Idle.TrySetResult();
        };
        monitor.ActivityResumed += () =>
        {
            Interlocked.Increment(ref resumeCount);
            cycle1Resume.TrySetResult();
        };

        // Cycle 1: release gate 0 → idle fires.
        gates[0].TrySetResult();
        await cycle1Idle.Task.WaitAsync(TimeSpan.FromSeconds(5));

        monitor.NotifyActivity(); // fires ActivityResumed, starts cycle 2 countdown
        await cycle1Resume.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Cycle 2: release gate 1 → second idle fires.
        gates[1].TrySetResult();
        await cycle2Idle.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, idleCount);
        Assert.Equal(1, resumeCount);
    }

    // ---------------------------------------------------------------------------
    // Dispose
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Dispose_StopsCountdown_NoIdleTripped()
    {
        var (factory, _) = BlockingDelay();
        bool fired = false;

        var monitor = new IdleMonitor(1, factory);
        monitor.IdleTripped += () => fired = true;

        // Dispose cancels the CTS; the Register callback inside the factory
        // will cancel the gate, causing the factory to throw OperationCanceledException.
        // Do NOT call gate.TrySetResult() here — that would race with the cancellation
        // and could let the factory complete normally before the Register fires.
        monitor.Dispose();
        await Task.Delay(50);

        Assert.False(fired);
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        using var monitor = new IdleMonitor(60, (_, _) => Task.Delay(Timeout.Infinite));
        monitor.Dispose();
        monitor.Dispose(); // second call should be a no-op
    }

    [Fact]
    public void NotifyActivity_AfterDispose_DoesNotThrow()
    {
        using var monitor = new IdleMonitor(60, (_, _) => Task.Delay(Timeout.Infinite));
        monitor.Dispose();
        monitor.NotifyActivity(); // should silently return
    }
}
