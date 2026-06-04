// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.Irc;
using Xunit;

namespace DataJack.Core.Tests;

public sealed class IdleMonitorTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    // Delay factory that completes immediately (unless ct is cancelled).
    // Makes IdleTripped fire synchronously in tests.
    private static Func<int, CancellationToken, Task> InstantDelay() =>
        (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

    // Delay factory that blocks until the returned TaskCompletionSource is resolved.
    // Lets tests control exactly when the delay completes (or cancel it).
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
        var fired = new TaskCompletionSource();
        using var monitor = new IdleMonitor(1, InstantDelay());
        monitor.IdleTripped += () => fired.TrySetResult();

        await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task IdleTripped_FiredExactlyOnce_PerIdleCycle()
    {
        int count = 0;
        var fired = new TaskCompletionSource();
        using var monitor = new IdleMonitor(1, InstantDelay());
        monitor.IdleTripped += () =>
        {
            Interlocked.Increment(ref count);
            fired.TrySetResult();
        };

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
        var idleGate    = new TaskCompletionSource();
        var resumedGate = new TaskCompletionSource();

        using var monitor = new IdleMonitor(1, InstantDelay());
        monitor.IdleTripped     += () => idleGate.TrySetResult();
        monitor.ActivityResumed += () => resumedGate.TrySetResult();

        // Wait for idle to trip.
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
        var idleGate = new TaskCompletionSource();
        int resumeCount = 0;

        using var monitor = new IdleMonitor(1, InstantDelay());
        monitor.IdleTripped     += () => idleGate.TrySetResult();
        monitor.ActivityResumed += () => Interlocked.Increment(ref resumeCount);

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
        int idleCount   = 0;
        int resumeCount = 0;

        var cycle1Idle   = new TaskCompletionSource();
        var cycle1Resume = new TaskCompletionSource();
        var cycle2Idle   = new TaskCompletionSource();

        // For cycle 2, NotifyActivity needs to restart the countdown.
        // We use InstantDelay for both countdown runs.
        using var monitor = new IdleMonitor(1, InstantDelay());
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

        // Cycle 1: wait for idle, then come back.
        await cycle1Idle.Task.WaitAsync(TimeSpan.FromSeconds(5));
        monitor.NotifyActivity(); // fires ActivityResumed, restarts countdown
        await cycle1Resume.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Cycle 2: the new countdown fires a second IdleTripped.
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
