// SPDX-License-Identifier: GPL-3.0-or-later
// Owns all per-server components for one active IRC connection and manages
// their lifetime as a unit. One instance per /connect or auto-connect entry.
using DataJack.Core.Caps;
using DataJack.Core.Caps.Handlers;
using DataJack.Core.Events;
using DataJack.Core.Irc;
using DataJack.Core.State;
using DataJack.Core.Storage.Config;
using DataJack.Net;
using CapsSasl = DataJack.Core.Caps.SaslCredentials;

namespace DataJack;

/// <summary>
/// Assembles and owns the full per-server IRC component stack:
/// network transport, raw connection, IRC parser, capability negotiation,
/// SASL authentication, flood control, state tracking, and reconnect management.
///
/// Sends NICK + USER on every <see cref="ConnectionEstablished"/> event so that
/// both the initial connect and automatic reconnects complete registration.
/// </summary>
internal sealed class ServerSession : IAsyncDisposable
{
    private readonly EventDispatcher              _dispatcher;
    private readonly IRCConnection                _connection;
    private readonly FloodController              _flood;
    private readonly ReconnectController          _reconnect;
    private readonly Action<ConnectionEstablished> _onEstablished;
    private bool _disposed;

    /// <summary>Routes structured IRC commands (JOIN, PART, MSG, etc.) for this session.</summary>
    public IRCCommandRouter Router { get; }

    private ServerSession(
        EventDispatcher dispatcher,
        IRCConnection connection,
        FloodController flood,
        ReconnectController reconnect,
        IRCCommandRouter router,
        Action<ConnectionEstablished> onEstablished)
    {
        _dispatcher    = dispatcher;
        _connection    = connection;
        _flood         = flood;
        _reconnect     = reconnect;
        Router         = router;
        _onEstablished = onEstablished;
    }

    // ---------------------------------------------------------------------------
    // Factory
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Assembles all per-server components, connects to the server, and returns
    /// when the TCP/TLS handshake is complete. NICK + USER are sent asynchronously
    /// after <see cref="ConnectionEstablished"/> is dispatched.
    /// </summary>
    /// <exception cref="Exception">Re-throws network errors; a ConnectionFailed event
    /// is also published on the event bus before the exception propagates.</exception>
    public static async Task<ServerSession> ConnectAsync(
        ServerEntry entry,
        AppConfig config,
        EventDispatcher dispatcher,
        CancellationToken ct = default)
    {
        string serverId = entry.NetworkName;

        INetworkProvider provider = entry.Proxy is not null
            ? new Socks5Transport(entry.Proxy)
            : entry.Tls
                ? (INetworkProvider)new TlsTransport()
                : new TcpTransport();

        var connection = new IRCConnection(serverId, provider, dispatcher);

        // Protocol components self-register by subscribing to the event bus.
        _ = new IRCParser(serverId, dispatcher);
        _ = new CapabilityNegotiator(serverId, connection, dispatcher);

        if (entry.Sasl is { } saslConf)
            _ = new SaslAuthenticator(serverId, connection, dispatcher,
                new CapsSasl(
                    AccountName: saslConf.Account,
                    Password:    saslConf.Password,
                    TryExternal: saslConf.Mechanism.Equals("EXTERNAL", StringComparison.OrdinalIgnoreCase)));

        var registry = new CapabilityRegistry(serverId, dispatcher);
        _ = new ServerTimeHandler(registry);
        _ = new EchoMessageHandler(registry);
        _ = new MonitorHandler(serverId, connection, registry, dispatcher);
        _ = new BatchHandler(serverId, dispatcher);
        _ = new LabeledResponseHandler(registry);

        var adv   = config.Advanced;
        var flood = new FloodController(serverId, connection, dispatcher,
            new FloodController.Config(
                TokenCapacity:  adv.FloodTokenCapacity,
                TokenDrainRate: adv.FloodDrainRate));

        _ = new IRCStateUpdater(serverId, new IRCStateModel(), dispatcher);

        var router = new IRCCommandRouter(connection);

        var endpoint  = new NetworkEndpoint(entry.Address, entry.Port, entry.Tls);
        var reconnect = new ReconnectController(serverId, connection, dispatcher, endpoint,
            new ReconnectController.Config(
                InitialDelaySeconds: adv.ReconnectInitialDelaySec,
                MaxDelaySeconds:     adv.ReconnectMaxDelaySec,
                MaxAttempts:         adv.ReconnectMaxAttempts));

        // Subscribe before ConnectAsync so that the initial ConnectionEstablished
        // fires the handler along with every subsequent reconnect.
        var scope = new SettingsScope(config, entry.NetworkName);
        Action<ConnectionEstablished> onEstablished = e =>
        {
            if (!e.Server.Equals(serverId, StringComparison.Ordinal)) return;
            _ = SendRegistrationAsync(connection, entry, scope);
        };
        dispatcher.Subscribe(onEstablished);

        await connection.ConnectAsync(endpoint, ct).ConfigureAwait(false);

        return new ServerSession(dispatcher, connection, flood, reconnect, router, onEstablished);
    }

    // ---------------------------------------------------------------------------
    // Registration
    // ---------------------------------------------------------------------------

    // Sends PASS (if configured), NICK, and USER after every ConnectionEstablished.
    // Fire-and-forget; errors surface via ConnectionClosed events.
    private static async Task SendRegistrationAsync(
        IRCConnection connection,
        ServerEntry entry,
        SettingsScope scope)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(entry.Password))
                await connection.SendLineAsync($"PASS :{entry.Password}").ConfigureAwait(false);
            await connection.SendLineAsync($"NICK {scope.Nick}").ConfigureAwait(false);
            await connection.SendLineAsync($"USER {scope.Username} 0 * :{scope.Realname}")
                .ConfigureAwait(false);
        }
        catch { /* stream may have closed; ConnectionClosed event will surface the error */ }
    }

    // ---------------------------------------------------------------------------
    // Disposal
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Unsubscribes registration handler, stops the reconnect controller, and
    /// disposes flood control and the connection. Idempotent.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _dispatcher.Unsubscribe(_onEstablished);
        await _reconnect.DisposeAsync().ConfigureAwait(false);
        await _flood.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
