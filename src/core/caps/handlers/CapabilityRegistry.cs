// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.Events;

namespace DataJack.Core.Caps.Handlers;

/// <summary>
/// Central active-capability and local-nick state, shared by all capability handlers.
///
/// Subscribes to <see cref="CapabilityNegotiated"/>, <see cref="ServerCapabilityChanged"/>,
/// <see cref="WelcomeReceived"/>, and <see cref="NickChanged"/> so that all handlers can
/// answer "is cap X active?" and "what is the local nick?" without duplicating that logic.
///
/// Thread-safe: <see cref="IsActive"/> and <see cref="LocalNick"/> may be read from any thread.
/// </summary>
public sealed class CapabilityRegistry
{
    private readonly string _serverId;
    private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
    private string? _localNick;
    private readonly object _stateLock = new();

    public CapabilityRegistry(string serverId, EventDispatcher dispatcher)
    {
        _serverId = serverId;
        dispatcher.Subscribe<CapabilityNegotiated>(OnCapabilityNegotiated);
        dispatcher.Subscribe<ServerCapabilityChanged>(OnServerCapabilityChanged);
        dispatcher.Subscribe<WelcomeReceived>(OnWelcomeReceived);
        dispatcher.Subscribe<NickChanged>(OnNickChanged);
        dispatcher.Subscribe<ConnectionEstablished>(OnConnectionEstablished);
    }

    /// <summary>Returns true if <paramref name="capability"/> is currently active on this server.</summary>
    public bool IsActive(string capability)
    {
        lock (_stateLock) return _active.Contains(capability);
    }

    /// <summary>The local nick as last confirmed by 001 or a NICK command, or null before registration.</summary>
    public string? LocalNick
    {
        get { lock (_stateLock) return _localNick; }
    }

    // CapabilityNegotiated replaces the entire active set (covers reconnects with different cap grants).
    private void OnCapabilityNegotiated(CapabilityNegotiated evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        lock (_stateLock)
        {
            _active.Clear();
            foreach (var cap in evt.Granted) _active.Add(cap);
        }
    }

    // ServerCapabilityChanged (cap-notify NEW/DEL) applies incremental changes.
    private void OnServerCapabilityChanged(ServerCapabilityChanged evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        lock (_stateLock)
        {
            foreach (var cap in evt.Added)   _active.Add(cap);
            foreach (var cap in evt.Removed) _active.Remove(cap);
        }
    }

    private void OnWelcomeReceived(WelcomeReceived evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        lock (_stateLock) _localNick = evt.Nick;
    }

    private void OnNickChanged(NickChanged evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        lock (_stateLock)
        {
            if (_localNick is not null &&
                string.Equals(_localNick, evt.OldNick, StringComparison.OrdinalIgnoreCase))
            {
                _localNick = evt.NewNick;
            }
        }
    }

    // Clear active caps when a new connection starts so stale caps are not visible during
    // the window between ConnectionEstablished and the next CapabilityNegotiated.
    private void OnConnectionEstablished(ConnectionEstablished evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        lock (_stateLock) _active.Clear();
    }
}
