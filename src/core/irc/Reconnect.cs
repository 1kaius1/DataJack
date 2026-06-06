// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.Events;
using DataJack.Net;

namespace DataJack.Core.Irc;

/// <summary>
/// Automatically reconnects an <see cref="IRCConnection"/> after an unexpected
/// <see cref="ConnectionClosed"/> event, using exponential backoff with ±20% jitter.
/// See ARCHITECTURE.md §4.5.
///
/// The <see cref="CapabilityNegotiator"/> and <see cref="SaslAuthenticator"/> re-run
/// their protocols automatically because they subscribe to <see cref="ConnectionEstablished"/>,
/// which fires on each successful reconnect.
///
/// Channel rejoin is deferred to <c>IRCCommandRouter</c>; Phase 1 emits
/// <see cref="ReconnectSucceeded"/> with an empty channel list.
///
/// To suppress reconnection for an intentional disconnect, call
/// <see cref="DisposeAsync"/> before closing the connection.
/// </summary>
public sealed class ReconnectController : IAsyncDisposable
{
    /// <summary>Backoff parameters. All fields are configurable per server.</summary>
    public sealed record Config(
        /// <summary>Initial delay between the disconnect and the first reconnect attempt.</summary>
        double InitialDelaySeconds = 2.0,
        /// <summary>Multiplier applied to the delay after each failed attempt.</summary>
        double Multiplier = 2.0,
        /// <summary>Hard cap on the computed delay before jitter is applied.</summary>
        double MaxDelaySeconds = 300.0,
        /// <summary>Fractional ±jitter applied to each delay (0.2 = ±20%).</summary>
        double JitterFraction = 0.2,
        /// <summary>Maximum reconnect attempts before giving up. 0 = unlimited.</summary>
        int MaxAttempts = 0);

    private readonly string _serverId;
    private readonly IRCConnection _connection;
    private readonly EventDispatcher _dispatcher;
    private readonly NetworkEndpoint _endpoint;
    private readonly Config _config;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    // Ensures at most one reconnect loop runs concurrently.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    public ReconnectController(
        string serverId,
        IRCConnection connection,
        EventDispatcher dispatcher,
        NetworkEndpoint endpoint,
        Config? config = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _serverId   = serverId;
        _connection = connection;
        _dispatcher = dispatcher;
        _endpoint   = endpoint;
        _config     = config ?? new Config();
        _delay      = delay ?? ((ts, ct) => Task.Delay(ts, ct));

        dispatcher.Subscribe<ConnectionClosed>(OnConnectionClosed);
    }

    // ---------------------------------------------------------------------------
    // Event handler
    // ---------------------------------------------------------------------------

    private void OnConnectionClosed(ConnectionClosed evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        if (_cts.IsCancellationRequested) return;

        _ = Task.Run(() => ReconnectLoopAsync(_cts.Token));
    }

    // ---------------------------------------------------------------------------
    // Reconnect logic
    // ---------------------------------------------------------------------------

    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        // Skip if another reconnect loop is already in progress.
        if (!_gate.Wait(0)) return;
        try
        {
            await DoReconnectAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DoReconnectAsync(CancellationToken ct)
    {
        var delay   = _config.InitialDelaySeconds;
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            attempt++;

            // Apply jitter: delay × (1 ± JitterFraction), then cap at MaxDelaySeconds.
            var jitter     = 1.0 + _config.JitterFraction * (Random.Shared.NextDouble() * 2.0 - 1.0);
            var actualSecs = Math.Min(delay * jitter, _config.MaxDelaySeconds);

            await _dispatcher.PublishAsync(
                new ReconnectScheduled(_serverId, actualSecs, attempt),
                EventPriority.Normal).ConfigureAwait(false);

            try
            {
                await _delay(TimeSpan.FromSeconds(actualSecs), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }

            if (ct.IsCancellationRequested) return;

            // Reset stream/task state so ConnectAsync can be called again.
            await _connection.PrepareForReconnectAsync().ConfigureAwait(false);

            try
            {
                await _connection.ConnectAsync(_endpoint, ct).ConfigureAwait(false);

                // CapabilityNegotiator and SaslAuthenticator re-run automatically via
                // their ConnectionEstablished subscriptions.  Channel rejoin is deferred.
                await _dispatcher.PublishAsync(
                    new ReconnectSucceeded(_serverId, []),
                    EventPriority.Normal).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception)
            {
                // Scale up delay (capped) for the next attempt.
                delay = Math.Min(delay * _config.Multiplier, _config.MaxDelaySeconds);

                if (_config.MaxAttempts > 0 && attempt >= _config.MaxAttempts)
                {
                    await _dispatcher.PublishAsync(
                        new ReconnectFailed(_serverId, $"All {attempt} reconnect attempt(s) failed"),
                        EventPriority.Normal).ConfigureAwait(false);
                    return;
                }
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Lifecycle
    // ---------------------------------------------------------------------------

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _dispatcher.Unsubscribe<ConnectionClosed>(OnConnectionClosed);
        await _cts.CancelAsync().ConfigureAwait(false);

        // Wait for any running reconnect loop to observe the cancellation and exit.
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();

        _cts.Dispose();
        _gate.Dispose();
    }
}
