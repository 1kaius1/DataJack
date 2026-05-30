// SPDX-License-Identifier: GPL-3.0-or-later
using System.Threading.Channels;

namespace DataJack.Core.Events;

/// <summary>
/// Central event bus. Every cross-component notification flows through this dispatcher;
/// no layer holds a direct reference to another layer's objects. See ARCHITECTURE.md §2 and §5.
///
/// Threading contract:
/// - Any thread may call PublishAsync.
/// - Handlers are called on the single event dispatch thread (started by Start()).
/// - UI handlers must marshal to the Avalonia UI thread themselves via Dispatcher.UIThread.
/// </summary>
public sealed class EventDispatcher : IAsyncDisposable
{
    // Capacity for each priority channel. Critical is small because it must drain fast
    // and should never accumulate a backlog under normal operation.
    private const int CriticalCapacity = 64;
    private const int NormalCapacity = 1024;
    private const int LowCapacity = 512;

    private readonly Channel<Action> _critical = Channel.CreateBounded<Action>(
        new BoundedChannelOptions(CriticalCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    private readonly Channel<Action> _normal = Channel.CreateBounded<Action>(
        new BoundedChannelOptions(NormalCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    private readonly Channel<Action> _low = Channel.CreateBounded<Action>(
        new BoundedChannelOptions(LowCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    // Handler registry: event Type → list of Action<T> delegates (stored as Delegate
    // to avoid making the dictionary generic; cast is safe because Subscribe<T> is typed).
    private readonly Dictionary<Type, List<Delegate>> _handlers = new();
    private readonly ReaderWriterLockSlim _handlersLock = new();

    private Task? _dispatchTask;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Register a handler for events of type <typeparamref name="T"/>.
    /// Handlers for the same type are called in registration order on the dispatch thread.
    /// </summary>
    public void Subscribe<T>(Action<T> handler) where T : struct
    {
        ArgumentNullException.ThrowIfNull(handler);

        _handlersLock.EnterWriteLock();
        try
        {
            if (!_handlers.TryGetValue(typeof(T), out var list))
                _handlers[typeof(T)] = list = new List<Delegate>();
            list.Add(handler);
        }
        finally
        {
            _handlersLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Remove a previously registered handler. No-op if the handler is not registered.
    /// </summary>
    public void Unsubscribe<T>(Action<T> handler) where T : struct
    {
        ArgumentNullException.ThrowIfNull(handler);

        _handlersLock.EnterWriteLock();
        try
        {
            if (_handlers.TryGetValue(typeof(T), out var list))
                list.Remove(handler);
        }
        finally
        {
            _handlersLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Enqueue an event for dispatch. If no handlers are registered the call is a no-op.
    /// Applies backpressure (waits asynchronously) when the priority channel is full.
    /// </summary>
    public async ValueTask PublishAsync<T>(
        T evt,
        EventPriority priority = EventPriority.Normal,
        CancellationToken ct = default) where T : struct
    {
        // Snapshot handlers under a read lock. Publishing with no subscribers is a no-op;
        // skipping the channel write avoids closure allocation for unsubscribed event types.
        Delegate[]? snapshot = GetHandlerSnapshot(typeof(T));
        if (snapshot is null)
            return;

        var channel = priority switch
        {
            EventPriority.Critical => _critical,
            EventPriority.Low      => _low,
            _                      => _normal,
        };

        // The closure captures evt (boxed, since it is a struct captured in a heap object)
        // and the snapshot array. Boxing on publish is acceptable; optimize if profiling shows need.
        await channel.Writer.WriteAsync(() => Dispatch(evt, snapshot), ct).ConfigureAwait(false);
    }

    private static void Dispatch<T>(T evt, Delegate[] handlers) where T : struct
    {
        foreach (var handler in handlers)
            ((Action<T>)handler)(evt);
    }

    private Delegate[]? GetHandlerSnapshot(Type type)
    {
        _handlersLock.EnterReadLock();
        try
        {
            return _handlers.TryGetValue(type, out var list) && list.Count > 0
                ? list.ToArray()
                : null;
        }
        finally
        {
            _handlersLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Start the event dispatch loop on a dedicated background task.
    /// Must be called once before publishing events. Throws if called more than once.
    /// </summary>
    public void Start(CancellationToken ct = default)
    {
        if (_dispatchTask is not null)
            throw new InvalidOperationException("Dispatch loop is already running.");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _dispatchTask = Task.Run(() => RunDispatchLoopAsync(_cts.Token), _cts.Token);
    }

    private async Task RunDispatchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Drain all critical events before processing any lower-priority work.
            while (_critical.Reader.TryRead(out Action? action))
                action();

            if (_normal.Reader.TryRead(out Action? normalAction))
            {
                normalAction();
                continue;
            }

            if (_low.Reader.TryRead(out Action? lowAction))
            {
                lowAction();
                continue;
            }

            // All channels empty. Wait for any tier to signal readiness.
            // Task.WhenAny returns as soon as any channel has data or the token fires.
            await Task.WhenAny(
                _critical.Reader.WaitToReadAsync(ct).AsTask(),
                _normal.Reader.WaitToReadAsync(ct).AsTask(),
                _low.Reader.WaitToReadAsync(ct).AsTask()
            ).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();

        if (_dispatchTask is not null)
        {
            try { await _dispatchTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _cts?.Dispose();
        _critical.Writer.Complete();
        _normal.Writer.Complete();
        _low.Writer.Complete();
        _handlersLock.Dispose();
    }
}
