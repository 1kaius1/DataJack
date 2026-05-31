// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Threading.Channels;
using DataJack.Core.Events;

namespace DataJack.Core.Irc;

/// <summary>Send priority for a rate-limited outbound IRC line.</summary>
public enum LineKind
{
    /// <summary>Regular IRC commands and messages — subject to the token bucket.</summary>
    Normal,
    /// <summary>
    /// CTCP replies and other automated low-priority responses — queued behind
    /// Normal items when both are waiting.
    /// </summary>
    Ctcp,
}

/// <summary>
/// Token-bucket flood controller for outbound IRC lines. See ARCHITECTURE.md §4.4.
///
/// Priority model:
///   Bypass path  — caller uses <see cref="SendBypassAsync"/>; no tokens consumed.
///                  Use for PONG, PING, QUIT, and server-originated NOTICEs.
///   Normal queue — regular commands; costs tokens from the bucket.
///   CTCP queue   — lowest priority; drained only when the Normal queue is empty.
///
/// Token cost per line: 1.0 + 0.1 per full 100 bytes over 200 bytes.
/// </summary>
public sealed class FloodController : IAsyncDisposable
{
    /// <summary>Token-bucket parameters. All fields configurable per server.</summary>
    public sealed record Config(
        /// <summary>Maximum burst capacity (tokens). Default: 10.</summary>
        double TokenCapacity = 10.0,
        /// <summary>Refill rate in tokens per second. Default: 2.</summary>
        double TokenDrainRate = 2.0,
        /// <summary>Maximum pending lines per queue tier before dropping. Default: 50.</summary>
        int MaxQueueDepth = 50);

    private readonly string _serverId;
    private readonly IRCConnection _connection;
    private readonly EventDispatcher _dispatcher;
    private readonly Config _config;

    private readonly Channel<string> _normalQueue;
    private readonly Channel<string> _ctcpQueue;
    private double _tokens;

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _drainTask;

    public FloodController(
        string serverId,
        IRCConnection connection,
        EventDispatcher dispatcher,
        Config? config = null)
    {
        _serverId   = serverId;
        _connection = connection;
        _dispatcher = dispatcher;
        _config     = config ?? new Config();
        _tokens     = _config.TokenCapacity; // start with a full burst

        // Wait mode: TryWrite returns false when the channel is at capacity.
        // DropWrite was avoided because in .NET 10 it returns true even when the
        // item is silently discarded, which breaks TrySend's overflow detection.
        _normalQueue = Channel.CreateBounded<string>(
            new BoundedChannelOptions(_config.MaxQueueDepth)
            { FullMode = BoundedChannelFullMode.Wait });
        _ctcpQueue = Channel.CreateBounded<string>(
            new BoundedChannelOptions(_config.MaxQueueDepth)
            { FullMode = BoundedChannelFullMode.Wait });

        _drainTask = Task.Run(() => DrainAsync(_cts.Token));
    }

    // ---------------------------------------------------------------------------
    // Public API
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Send immediately without consuming tokens. Use for PONG, PING, QUIT,
    /// and server-originated NOTICEs where latency matters more than fairness.
    /// </summary>
    public Task SendBypassAsync(string line, CancellationToken ct = default) =>
        _connection.SendLineAsync(line, ct);

    /// <summary>
    /// Enqueue a rate-limited line. Returns <see langword="true"/> when the
    /// line was accepted; <see langword="false"/> when the queue is full, in
    /// which case a <see cref="FloodQueueFull"/> event is emitted and the line
    /// is dropped.
    /// </summary>
    public bool TrySend(string line, LineKind kind = LineKind.Normal)
    {
        var queue = kind == LineKind.Ctcp ? _ctcpQueue : _normalQueue;
        if (queue.Writer.TryWrite(line)) return true;

        // Fire-and-forget: the event dispatcher is async but we're on a sync caller.
        _ = _dispatcher.PublishAsync(
            new FloodQueueFull(_serverId, 1),
            EventPriority.Normal).AsTask();
        return false;
    }

    // ---------------------------------------------------------------------------
    // Background drain loop
    // ---------------------------------------------------------------------------

    private async Task DrainAsync(CancellationToken ct)
    {
        var lastTick = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            Refill(ref lastTick);

            // Normal always takes priority; fall through to CTCP only when Normal is empty.
            if (!_normalQueue.Reader.TryRead(out var line) &&
                !_ctcpQueue.Reader.TryRead(out line))
            {
                try
                {
                    await Task.WhenAny(
                        _normalQueue.Reader.WaitToReadAsync(ct).AsTask(),
                        _ctcpQueue.Reader.WaitToReadAsync(ct).AsTask()
                    ).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                continue;
            }

            // Wait until the bucket has enough tokens for this line.
            var cost = CalculateCost(line);
            while (_tokens < cost && !ct.IsCancellationRequested)
            {
                // Sleep at most 100 ms per iteration so cancellation is responsive.
                var waitSeconds = (cost - _tokens) / _config.TokenDrainRate;
                var waitMs = Math.Max(1, (int)(Math.Min(waitSeconds, 0.1) * 1000));
                try { await Task.Delay(waitMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                Refill(ref lastTick);
            }

            if (ct.IsCancellationRequested) break;

            _tokens -= cost;
            try { await _connection.SendLineAsync(line, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Refill(ref DateTime lastTick)
    {
        var now     = DateTime.UtcNow;
        var elapsed = (now - lastTick).TotalSeconds;
        lastTick    = now;
        _tokens     = Math.Min(_config.TokenCapacity, _tokens + elapsed * _config.TokenDrainRate);
    }

    // ---------------------------------------------------------------------------
    // Token cost
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Cost in tokens for a single outbound line.
    /// 1.0 base + 0.1 per full 100 bytes beyond the first 200.
    /// </summary>
    internal static double CalculateCost(string line)
    {
        var bytes       = Encoding.UTF8.GetByteCount(line);
        var extraChunks = Math.Max(0, bytes - 200) / 100; // integer division = full chunks
        return 1.0 + extraChunks * 0.1;
    }

    // ---------------------------------------------------------------------------
    // Lifecycle
    // ---------------------------------------------------------------------------

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try { await _drainTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _cts.Dispose();
    }
}
