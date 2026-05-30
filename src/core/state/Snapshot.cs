// SPDX-License-Identifier: GPL-3.0-or-later
// Immutable snapshot types for the IRC world state tree. See ARCHITECTURE.md §9.1.
//
// All types are sealed records so callers can use "with" expressions for immutable updates:
//   var next = existing with { RegisteredNick = newNick };
//
// Collection fields use IReadOnly* interfaces. Callers must not cast them to mutable types.
// Phase 1 may upgrade inner collections to ImmutableDictionary if allocation profiling warrants it.

namespace DataJack.Core.State;

/// <summary>An IRC mode character set with optional per-mode parameters.</summary>
public sealed record ModeSet(
    IReadOnlySet<char> Flags,
    IReadOnlyDictionary<char, string> Parameters)
{
    /// <summary>A mode set with no flags and no parameters.</summary>
    public static ModeSet Empty { get; } = new(
        new HashSet<char>(),
        new Dictionary<char, string>());
}

/// <summary>A channel topic with the nick that set it and when it was set.</summary>
public sealed record Topic(
    string Text,
    string? SetterNick,
    DateTimeOffset? SetAt);

/// <summary>A user's presence in a specific channel, including their channel-level modes.</summary>
public sealed record ChannelUser(
    string Nick,
    string User,
    string Host,
    string? Account,
    string? RealName,
    ModeSet ChannelModes,
    string? AwayMessage);

/// <summary>A private message conversation (query) with another user.</summary>
public sealed record QueryState(
    string Nick,
    string? User,
    string? Host);

/// <summary>A nick being tracked via the IRC MONITOR command.</summary>
public sealed record MonitoredNick(string Nick, bool IsOnline);

/// <summary>The state of a joined channel.</summary>
public sealed record ChannelState(
    string Name,
    Topic? Topic,
    DateTimeOffset? CreatedAt,
    ModeSet Modes,
    IReadOnlyDictionary<string, ChannelUser> Users);

/// <summary>
/// The state of one server connection. The string key used in the parent dictionary
/// is the server identifier (network name or address as configured by the user).
/// </summary>
public sealed record ServerState(
    string Id,
    string Address,
    int Port,
    bool Tls,
    bool IsConnected,
    DateTimeOffset? ConnectedAt,
    string? RegisteredNick,
    string? UserName,
    IReadOnlySet<string> ActiveCapabilities,
    IReadOnlyDictionary<string, string> IsupportTokens,
    IReadOnlyDictionary<string, ChannelState> Channels,
    IReadOnlyDictionary<string, QueryState> Queries,
    IReadOnlyList<MonitoredNick> MonitoredNicks)
{
    /// <summary>
    /// Create a disconnected server entry before any connection attempt has been made.
    /// </summary>
    public static ServerState CreateDisconnected(string id, string address, int port, bool tls) =>
        new(id, address, port, tls,
            IsConnected:        false,
            ConnectedAt:        null,
            RegisteredNick:     null,
            UserName:           null,
            ActiveCapabilities: new HashSet<string>(),
            IsupportTokens:     new Dictionary<string, string>(),
            Channels:           new Dictionary<string, ChannelState>(),
            Queries:            new Dictionary<string, QueryState>(),
            MonitoredNicks:     Array.Empty<MonitoredNick>());
}

/// <summary>
/// An immutable, point-in-time snapshot of the full IRC world state.
/// Obtained via <see cref="IRCStateModel.CreateQuery"/> and replaced atomically
/// after each event dispatch cycle. See ARCHITECTURE.md §9.3.
/// </summary>
public sealed record IrcWorldSnapshot(IReadOnlyDictionary<string, ServerState> Servers)
{
    /// <summary>An empty snapshot with no server entries.</summary>
    public static IrcWorldSnapshot Empty { get; } =
        new(new Dictionary<string, ServerState>());
}
