// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.Events;
using DataJack.Core.Irc;

namespace DataJack.Core.Caps;

/// <summary>
/// Drives the IRCv3 CAP LS 302 → REQ → ACK/NAK negotiation lifecycle.
/// After negotiation, handles cap-notify NEW/DEL for runtime capability changes.
/// See ARCHITECTURE.md §4.2.
///
/// Wire protocol summary:
///   client → server:  CAP LS 302
///   server → client:  CAP * LS [*] :cap1 cap2=value ...  (may be multiline)
///   client → server:  CAP REQ :cap1 cap2 ...
///   server → client:  CAP * ACK :cap1 cap2   (or NAK)
///   client → server:  CAP END
/// </summary>
public sealed class CapabilityNegotiator
{
    /// <summary>
    /// All capabilities the client wants to activate, in request order.
    /// Intersection with server-advertised caps is what gets requested.
    /// </summary>
    private static readonly string[] WantedCapabilities =
    [
        "message-tags",
        "batch",
        "labeled-response",
        "server-time",
        "away-notify",
        "chghost",
        "echo-message",
        "extended-join",
        "invite-notify",
        "monitor",
        "multi-prefix",
        "account-notify",
        "setname",
        "cap-notify",
        "sasl",
        "chathistory",
        "draft/reply",
        "draft/react",
    ];

    private readonly string _serverId;
    private readonly IRCConnection _connection;
    private readonly EventDispatcher _dispatcher;

    // Serialises concurrent Task.Run handlers so LS accumulation and state
    // transitions are always single-threaded even when events arrive quickly.
    private readonly SemaphoreSlim _lock = new(1, 1);

    private readonly HashSet<string> _serverCaps =
        new(StringComparer.OrdinalIgnoreCase);

    public CapabilityNegotiator(
        string serverId,
        IRCConnection connection,
        EventDispatcher dispatcher)
    {
        _serverId = serverId;
        _connection = connection;
        _dispatcher = dispatcher;

        dispatcher.Subscribe<ConnectionEstablished>(OnConnectionEstablished);
        dispatcher.Subscribe<RawLineReceived>(OnRawLineReceived);
    }

    // ---------------------------------------------------------------------------
    // Event handlers (always synchronous; dispatch work onto thread pool)
    // ---------------------------------------------------------------------------

    private void OnConnectionEstablished(ConnectionEstablished evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;

        _ = Task.Run(async () =>
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try { _serverCaps.Clear(); }
            finally { _lock.Release(); }

            await _connection.SendLineAsync("CAP LS 302").ConfigureAwait(false);
        });
    }

    private void OnRawLineReceived(RawLineReceived evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;

        _ = Task.Run(() => ProcessLineAsync(evt.Line));
    }

    // ---------------------------------------------------------------------------
    // CAP line processing
    // ---------------------------------------------------------------------------

    private async Task ProcessLineAsync(string line)
    {
        var msg = IRCParser.ParseMessage(line);
        if (msg is null || msg.Value.Command != "CAP") return;

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            await HandleCapAsync(msg.Value).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private Task HandleCapAsync(IrcMessage msg) =>
        msg.Param(1).ToUpperInvariant() switch
        {
            "LS"  => HandleLsAsync(msg),
            "ACK" => HandleAckAsync(msg),
            "NAK" => HandleNakAsync(msg),
            "NEW" => HandleNewAsync(msg),
            "DEL" => HandleDelAsync(msg),
            _     => Task.CompletedTask,
        };

    /// <summary>
    /// Handle a CAP LS response line. Accumulates multiline responses (marked by "*")
    /// and sends CAP REQ once all capability advertisements have been received.
    /// </summary>
    private async Task HandleLsAsync(IrcMessage msg)
    {
        bool isMultiline = msg.Param(2) == "*";
        string capsStr   = isMultiline ? msg.Param(3) : msg.Param(2);

        // Parse "capname" and "capname=value" tokens; store the name only.
        foreach (var token in capsStr.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = token.IndexOf('=');
            _serverCaps.Add(eq >= 0 ? token[..eq] : token);
        }

        if (isMultiline) return; // more LS lines coming

        // All LS data received. Request the intersection of wanted and available.
        var toRequest = WantedCapabilities
            .Where(c => _serverCaps.Contains(c))
            .ToList();

        if (toRequest.Count > 0)
        {
            await _connection.SendLineAsync($"CAP REQ :{string.Join(' ', toRequest)}")
                .ConfigureAwait(false);
        }
        else
        {
            await EndNegotiationAsync([], []).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Handle CAP ACK. Records granted capabilities, ends negotiation.
    /// </summary>
    private async Task HandleAckAsync(IrcMessage msg)
    {
        // ACK modifiers (-~=) signal sticky/ack-required flags; strip them.
        var granted = msg.Param(2)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(c => c.TrimStart('-', '~', '='))
            .Where(c => c.Length > 0)
            .ToList();

        var denied = WantedCapabilities
            .Where(c => _serverCaps.Contains(c)
                     && !granted.Contains(c, StringComparer.OrdinalIgnoreCase))
            .ToList();

        await EndNegotiationAsync(granted, denied).ConfigureAwait(false);
    }

    /// <summary>
    /// Handle CAP NAK. Server rejected the capability batch; end with nothing granted.
    /// Phase 1 does not retry individual capabilities on NAK — that is a later refinement.
    /// </summary>
    private Task HandleNakAsync(IrcMessage msg) =>
        EndNegotiationAsync([], []);

    /// <summary>
    /// Handle cap-notify NEW. Requests any newly advertised capabilities we want.
    /// </summary>
    private async Task HandleNewAsync(IrcMessage msg)
    {
        var newCaps = ParseCapNames(msg.Param(2));
        foreach (var cap in newCaps) _serverCaps.Add(cap);

        var toRequest = newCaps.Where(c => WantedCapabilities.Contains(c)).ToList();
        if (toRequest.Count > 0)
            await _connection.SendLineAsync($"CAP REQ :{string.Join(' ', toRequest)}")
                .ConfigureAwait(false);

        await _dispatcher.PublishAsync(
            new ServerCapabilityChanged(_serverId, newCaps, []),
            EventPriority.Normal).ConfigureAwait(false);
    }

    /// <summary>Handle cap-notify DEL. Updates tracking and notifies subscribers.</summary>
    private async Task HandleDelAsync(IrcMessage msg)
    {
        var deleted = msg.Param(2)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        foreach (var cap in deleted) _serverCaps.Remove(cap);

        await _dispatcher.PublishAsync(
            new ServerCapabilityChanged(_serverId, [], deleted),
            EventPriority.Normal).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private async Task EndNegotiationAsync(
        IReadOnlyList<string> granted,
        IReadOnlyList<string> denied)
    {
        await _connection.SendLineAsync("CAP END").ConfigureAwait(false);
        await _dispatcher.PublishAsync(
            new CapabilityNegotiated(_serverId, granted, denied),
            EventPriority.Normal).ConfigureAwait(false);
    }

    private static List<string> ParseCapNames(string capsStr) =>
        capsStr.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => { int e = t.IndexOf('='); return e >= 0 ? t[..e] : t; })
            .ToList();
}
