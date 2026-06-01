// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.Events;
using DataJack.Core.Irc;

namespace DataJack.Core.Caps.Handlers;

/// <summary>
/// Manages the MONITOR watchlist for nick online/offline tracking.
///
/// Callers add and remove nicks via <see cref="AddNickAsync"/> and <see cref="RemoveNickAsync"/>.
/// When the <c>monitor</c> capability is active, changes are sent to the server immediately.
/// On reconnect (new <see cref="CapabilityNegotiated"/> with monitor granted) the full watchlist
/// is re-sent because the server-side list does not survive a TCP reconnect.
///
/// MONITOR online/offline events (730/731) are handled upstream by <see cref="IRCParser"/>
/// and arrive as <see cref="MonitorStatusChanged"/> on the event bus.
/// </summary>
public sealed class MonitorHandler
{
    // IRC servers cap the MONITOR list per user (the MONITORSIZE ISUPPORT token).
    // Send watchlist additions in chunks to stay inside any per-command length limit.
    private const int ChunkSize = 100;

    private readonly string _serverId;
    private readonly IRCConnection _connection;
    private readonly CapabilityRegistry _registry;
    private readonly HashSet<string> _watchlist = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _listLock = new(1, 1);

    public MonitorHandler(
        string serverId,
        IRCConnection connection,
        CapabilityRegistry registry,
        EventDispatcher dispatcher)
    {
        _serverId   = serverId;
        _connection = connection;
        _registry   = registry;
        dispatcher.Subscribe<CapabilityNegotiated>(OnCapabilityNegotiated);
    }

    /// <summary>True when the <c>monitor</c> capability is currently active.</summary>
    public bool IsActive => _registry.IsActive("monitor");

    /// <summary>
    /// Add <paramref name="nick"/> to the watchlist. If monitor is active, sends
    /// <c>MONITOR + nick</c> to the server. No-op if the nick is already in the list.
    /// </summary>
    public async Task AddNickAsync(string nick, CancellationToken ct = default)
    {
        await _listLock.WaitAsync(ct).ConfigureAwait(false);
        bool added;
        try { added = _watchlist.Add(nick); }
        finally { _listLock.Release(); }

        if (added && IsActive)
            await _connection.SendLineAsync($"MONITOR + {nick}", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Remove <paramref name="nick"/> from the watchlist. If monitor is active, sends
    /// <c>MONITOR - nick</c> to the server. No-op if the nick is not in the list.
    /// </summary>
    public async Task RemoveNickAsync(string nick, CancellationToken ct = default)
    {
        await _listLock.WaitAsync(ct).ConfigureAwait(false);
        bool removed;
        try { removed = _watchlist.Remove(nick); }
        finally { _listLock.Release(); }

        if (removed && IsActive)
            await _connection.SendLineAsync($"MONITOR - {nick}", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Clear the entire watchlist. If monitor is active, sends <c>MONITOR C</c> to the server.
    /// </summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _listLock.WaitAsync(ct).ConfigureAwait(false);
        try { _watchlist.Clear(); }
        finally { _listLock.Release(); }

        if (IsActive)
            await _connection.SendLineAsync("MONITOR C", ct).ConfigureAwait(false);
    }

    /// <summary>Returns a snapshot of the current watchlist.</summary>
    public async Task<IReadOnlyList<string>> GetWatchlistAsync(CancellationToken ct = default)
    {
        await _listLock.WaitAsync(ct).ConfigureAwait(false);
        try { return [.. _watchlist]; }
        finally { _listLock.Release(); }
    }

    // Re-send the watchlist when monitor is granted (initial connect or reconnect).
    private void OnCapabilityNegotiated(CapabilityNegotiated evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        if (!evt.Granted.Contains("monitor", StringComparer.OrdinalIgnoreCase)) return;
        _ = ResubscribeAsync();
    }

    private async Task ResubscribeAsync()
    {
        await _listLock.WaitAsync().ConfigureAwait(false);
        List<string> nicks;
        try { nicks = [.. _watchlist]; }
        finally { _listLock.Release(); }

        if (nicks.Count == 0) return;

        for (int i = 0; i < nicks.Count; i += ChunkSize)
        {
            var chunk = string.Join(',', nicks.Skip(i).Take(ChunkSize));
            await _connection.SendLineAsync($"MONITOR + {chunk}").ConfigureAwait(false);
        }
    }
}
