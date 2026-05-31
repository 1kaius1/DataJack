// SPDX-License-Identifier: GPL-3.0-or-later

namespace DataJack.Core.State;

/// <summary>
/// Read-only synchronous query interface for IRC world state. See ARCHITECTURE.md §5.3.
///
/// This is the one permitted bypass of the event bus: any thread may call these methods
/// without going through an async event round-trip. All reads are against the snapshot
/// captured when the query object was created, so a single caller sees a consistent
/// point-in-time view across multiple method calls.
///
/// Obtain an instance via <see cref="IRCStateModel.CreateQuery"/>.
/// </summary>
public interface IRCStateQuery
{
    /// <summary>
    /// Returns the nick the client is currently registered as on the given server,
    /// or null if the server is unknown or the client has not completed registration.
    /// </summary>
    string? GetCurrentNick(string server);

    /// <summary>
    /// Returns all users currently in the channel, or an empty list if the server
    /// or channel is unknown.
    /// </summary>
    IReadOnlyList<ChannelUser> GetChannelUsers(string server, string channel);

    /// <summary>
    /// Returns the current mode set for the channel, or null if unknown.
    /// </summary>
    ModeSet? GetChannelModes(string server, string channel);

    /// <summary>
    /// Returns the current topic for the channel, or null if no topic is set or the
    /// channel is unknown.
    /// </summary>
    Topic? GetChannelTopic(string server, string channel);

    /// <summary>
    /// Returns the channel-level modes for a specific user, or null if the server,
    /// channel, or user is unknown.
    /// </summary>
    ModeSet? GetUserModes(string server, string channel, string nick);

    /// <summary>
    /// Returns true if the client currently has an active, registered connection to
    /// the given server.
    /// </summary>
    bool IsConnected(string server);

    /// <summary>
    /// Returns the set of IRCv3 capabilities currently active on the given server,
    /// or an empty list if the server is unknown.
    /// </summary>
    IReadOnlyList<string> GetActiveCapabilities(string server);
}

/// <summary>
/// <see cref="IRCStateQuery"/> implementation bound to an <see cref="IrcWorldSnapshot"/>.
/// All reads are served from the snapshot captured at construction time.
/// </summary>
internal sealed class StateQuery(IrcWorldSnapshot snapshot) : IRCStateQuery
{
    public string? GetCurrentNick(string server) =>
        snapshot.Servers.TryGetValue(server, out var s) ? s.RegisteredNick : null;

    public IReadOnlyList<ChannelUser> GetChannelUsers(string server, string channel)
    {
        if (!snapshot.Servers.TryGetValue(server, out var s)) return Array.Empty<ChannelUser>();
        if (!s.Channels.TryGetValue(channel, out var ch)) return Array.Empty<ChannelUser>();
        return ch.Users.Values.ToArray();
    }

    public ModeSet? GetChannelModes(string server, string channel)
    {
        if (!snapshot.Servers.TryGetValue(server, out var s)) return null;
        return s.Channels.TryGetValue(channel, out var ch) ? ch.Modes : null;
    }

    public Topic? GetChannelTopic(string server, string channel)
    {
        if (!snapshot.Servers.TryGetValue(server, out var s)) return null;
        return s.Channels.TryGetValue(channel, out var ch) ? ch.Topic : null;
    }

    public ModeSet? GetUserModes(string server, string channel, string nick)
    {
        if (!snapshot.Servers.TryGetValue(server, out var s)) return null;
        if (!s.Channels.TryGetValue(channel, out var ch)) return null;
        return ch.Users.TryGetValue(nick, out var user) ? user.ChannelModes : null;
    }

    public bool IsConnected(string server) =>
        snapshot.Servers.TryGetValue(server, out var s) && s.IsConnected;

    public IReadOnlyList<string> GetActiveCapabilities(string server)
    {
        if (!snapshot.Servers.TryGetValue(server, out var s)) return Array.Empty<string>();
        return s.ActiveCapabilities.ToArray();
    }
}
