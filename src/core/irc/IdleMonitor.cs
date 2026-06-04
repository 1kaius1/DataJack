// SPDX-License-Identifier: GPL-3.0-or-later
// Idle-time monitor for auto-away. Tracks the last user input timestamp and fires
// events when the idle threshold is crossed in either direction.
// See ARCHITECTURE.md §13.

namespace DataJack.Core.Irc;

/// <summary>
/// Monitors user input activity and fires events for auto-away management.
/// <see cref="NotifyActivity"/> must be called each time the user types in the input box.
/// <see cref="IdleTripped"/> fires once per idle cycle when the configured delay elapses
/// without any activity.
/// <see cref="ActivityResumed"/> fires the first time <see cref="NotifyActivity"/> is called
/// after <see cref="IdleTripped"/> has fired, signalling that away status should be cleared.
/// Both events fire on thread-pool threads; subscribers that need to touch the UI must
/// marshal back to the UI thread via <c>Dispatcher.UIThread.Post</c>.
/// </summary>
public sealed class IdleMonitor : IDisposable
{
    private readonly int _delaySeconds;

    // Optional injectable delay function for deterministic unit tests.
    // Receives (delaySeconds, cancellationToken); a synchronous return completes immediately.
    private readonly Func<int, CancellationToken, Task>? _delayFactory;

    // Replaced atomically by NotifyActivity so in-flight countdown tasks observe cancellation.
    private CancellationTokenSource _cts = new();

    // 0 = active, 1 = idle. Written only via Interlocked so the timer task and
    // the UI thread (NotifyActivity) can race safely.
    private int _idle;

    private bool _disposed;

    /// <summary>
    /// Fired (once per idle cycle) when the idle delay elapses without user input.
    /// The client should send an AWAY command on all connected servers.
    /// </summary>
    public event Action? IdleTripped;

    /// <summary>
    /// Fired when the user types after an idle cycle.
    /// The client should send a bare AWAY (back) command on all connected servers.
    /// </summary>
    public event Action? ActivityResumed;

    /// <summary>
    /// Creates a new <see cref="IdleMonitor"/> and starts the countdown immediately.
    /// </summary>
    /// <param name="delaySeconds">
    /// Seconds of inactivity before <see cref="IdleTripped"/> fires. Must be positive.
    /// </param>
    /// <param name="delayFactory">
    /// Optional injectable delay for unit tests. When <see langword="null"/> the production
    /// <c>Task.Delay</c> path is used. See the xUnit test file for usage examples.
    /// </param>
    public IdleMonitor(int delaySeconds, Func<int, CancellationToken, Task>? delayFactory = null)
    {
        if (delaySeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(delaySeconds), "Delay must be positive.");
        _delaySeconds = delaySeconds;
        _delayFactory = delayFactory;
        StartCountdown(_cts.Token);
    }

    /// <summary>
    /// Signals user input activity. Resets the idle countdown. If the monitor was in the
    /// idle state, fires <see cref="ActivityResumed"/> on a thread-pool thread.
    /// Safe to call from the Avalonia UI thread. Does nothing after <see cref="Dispose"/>.
    /// </summary>
    public void NotifyActivity()
    {
        if (_disposed) return;

        // Swap in a fresh CTS; the old countdown task will observe the cancellation.
        var old = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();

        // If we were idle, signal that the user is back before resetting the flag.
        if (Interlocked.Exchange(ref _idle, 0) != 0)
            Task.Run(() => ActivityResumed?.Invoke());

        StartCountdown(_cts.Token);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }

    // Launches a background task that delays for _delaySeconds, then marks idle and
    // fires IdleTripped. Only the first task to reach the end fires (Interlocked ensures
    // exactly-once semantics per idle cycle).
    private void StartCountdown(CancellationToken ct)
    {
        int delay   = _delaySeconds;
        var factory = _delayFactory;

        Task.Run(async () =>
        {
            try
            {
                if (factory is not null)
                    await factory(delay, ct).ConfigureAwait(false);
                else
                    await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);

                // Transition 0 -> 1: only the countdown that wins this exchange fires the event.
                if (Interlocked.Exchange(ref _idle, 1) == 0)
                    IdleTripped?.Invoke();
            }
            catch (OperationCanceledException)
            {
                // Normal: countdown was reset by NotifyActivity() or stopped by Dispose().
            }
        });
    }
}
