// SPDX-License-Identifier: GPL-3.0-or-later

namespace DataJack.Core.Irc;

/// <summary>
/// Translates user slash commands into correctly-formatted IRC protocol lines sent via
/// <see cref="IRCConnection.SendLineAsync"/>. Validates arguments before sending.
/// See ARCHITECTURE.md §4.6 and §13.
///
/// Phase 1 commands: /join, /part, /msg, /notice, /nick, /quit, /raw.
/// Phase 3 commands: /kick, /ban, /unban, /kickban, /op, /deop, /voice, /devoice,
///   /mode, /invite, /topic, /names, /list, /whois, /who, /query, /me, /ctcp,
///   /ping, /away, /back.
/// </summary>
public sealed class IRCCommandRouter
{
    private readonly IRCConnection _connection;

    /// <param name="connection">The connection on which all outbound lines are sent.</param>
    public IRCCommandRouter(IRCConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
    }

    // ---------------------------------------------------------------------------
    // Public API — one method per command
    // ---------------------------------------------------------------------------

    /// <summary>Send JOIN for one channel, with an optional key.</summary>
    /// <exception cref="ArgumentException">If <paramref name="channel"/> is not a valid IRC channel name.</exception>
    public Task JoinAsync(string channel, string? key = null, CancellationToken ct = default)
    {
        ValidateChannelName(channel);

        var line = string.IsNullOrEmpty(key)
            ? $"JOIN {channel}"
            : $"JOIN {channel} {key}";

        return _connection.SendLineAsync(line, ct);
    }

    /// <summary>Send PART for one channel, with an optional reason.</summary>
    /// <exception cref="ArgumentException">If <paramref name="channel"/> is not a valid IRC channel name.</exception>
    public Task PartAsync(string channel, string? reason = null, CancellationToken ct = default)
    {
        ValidateChannelName(channel);

        var line = string.IsNullOrEmpty(reason)
            ? $"PART {channel}"
            : $"PART {channel} :{reason}";

        return _connection.SendLineAsync(line, ct);
    }

    /// <summary>Send PRIVMSG to a channel or nick.</summary>
    /// <exception cref="ArgumentException">If <paramref name="target"/> or <paramref name="text"/> is null or empty.</exception>
    public Task MsgAsync(string target, string text, CancellationToken ct = default)
    {
        ValidateTarget(target);
        ValidateText(text, nameof(text));
        return _connection.SendLineAsync($"PRIVMSG {target} :{text}", ct);
    }

    /// <summary>Send NOTICE to a channel or nick.</summary>
    /// <exception cref="ArgumentException">If <paramref name="target"/> or <paramref name="text"/> is null or empty.</exception>
    public Task NoticeAsync(string target, string text, CancellationToken ct = default)
    {
        ValidateTarget(target);
        ValidateText(text, nameof(text));
        return _connection.SendLineAsync($"NOTICE {target} :{text}", ct);
    }

    /// <summary>Send NICK to request a nick change.</summary>
    /// <exception cref="ArgumentException">If <paramref name="nick"/> fails basic IRC nick validation.</exception>
    public Task NickAsync(string nick, CancellationToken ct = default)
    {
        ValidateNick(nick);
        return _connection.SendLineAsync($"NICK {nick}", ct);
    }

    /// <summary>Send QUIT with an optional reason.</summary>
    public Task QuitAsync(string? reason = null, CancellationToken ct = default)
    {
        var line = string.IsNullOrEmpty(reason)
            ? "QUIT"
            : $"QUIT :{reason}";

        return _connection.SendLineAsync(line, ct);
    }

    /// <summary>
    /// Send a raw IRC line as-is. The caller is responsible for correct formatting.
    /// The line must not contain CRLF; a terminating CRLF is added by <see cref="IRCConnection"/>.
    /// </summary>
    /// <exception cref="ArgumentException">If <paramref name="line"/> contains a CR or LF.</exception>
    public Task RawAsync(string line, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(line);

        if (line.Contains('\r') || line.Contains('\n'))
            throw new ArgumentException("Raw line must not contain CR or LF.", nameof(line));

        return _connection.SendLineAsync(line, ct);
    }

    // ---------------------------------------------------------------------------
    // Phase 3 commands — channel operator actions
    // ---------------------------------------------------------------------------

    /// <summary>Send KICK to remove a user from a channel, with an optional reason.</summary>
    /// <exception cref="ArgumentException">If <paramref name="channel"/> or <paramref name="nick"/> is invalid.</exception>
    public Task KickAsync(string channel, string nick, string? reason = null, CancellationToken ct = default)
    {
        ValidateChannelName(channel);
        ValidateNick(nick);

        var line = string.IsNullOrEmpty(reason)
            ? $"KICK {channel} {nick}"
            : $"KICK {channel} {nick} :{reason}";

        return _connection.SendLineAsync(line, ct);
    }

    /// <summary>Set a ban mask on a channel (<c>MODE channel +b mask</c>).</summary>
    /// <exception cref="ArgumentException">If <paramref name="channel"/> is invalid or <paramref name="mask"/> is empty.</exception>
    public Task BanAsync(string channel, string mask, CancellationToken ct = default)
    {
        ValidateChannelName(channel);
        ArgumentException.ThrowIfNullOrEmpty(mask, nameof(mask));
        return _connection.SendLineAsync($"MODE {channel} +b {mask}", ct);
    }

    /// <summary>Remove a ban mask from a channel (<c>MODE channel -b mask</c>).</summary>
    /// <exception cref="ArgumentException">If <paramref name="channel"/> is invalid or <paramref name="mask"/> is empty.</exception>
    public Task UnbanAsync(string channel, string mask, CancellationToken ct = default)
    {
        ValidateChannelName(channel);
        ArgumentException.ThrowIfNullOrEmpty(mask, nameof(mask));
        return _connection.SendLineAsync($"MODE {channel} -b {mask}", ct);
    }

    /// <summary>
    /// Ban a mask and then kick the nick in a single operation.
    /// Sends <c>MODE channel +b mask</c> followed immediately by <c>KICK channel nick [:reason]</c>.
    /// </summary>
    /// <exception cref="ArgumentException">If any argument fails validation.</exception>
    public async Task KickBanAsync(
        string channel,
        string nick,
        string mask,
        string? reason = null,
        CancellationToken ct = default)
    {
        ValidateChannelName(channel);
        ValidateNick(nick);
        ArgumentException.ThrowIfNullOrEmpty(mask, nameof(mask));

        await _connection.SendLineAsync($"MODE {channel} +b {mask}", ct).ConfigureAwait(false);

        var kickLine = string.IsNullOrEmpty(reason)
            ? $"KICK {channel} {nick}"
            : $"KICK {channel} {nick} :{reason}";

        await _connection.SendLineAsync(kickLine, ct).ConfigureAwait(false);
    }

    /// <summary>Give channel operator status to a nick (<c>MODE channel +o nick</c>).</summary>
    /// <exception cref="ArgumentException">If <paramref name="channel"/> or <paramref name="nick"/> is invalid.</exception>
    public Task OpAsync(string channel, string nick, CancellationToken ct = default)
    {
        ValidateChannelName(channel);
        ValidateNick(nick);
        return _connection.SendLineAsync($"MODE {channel} +o {nick}", ct);
    }

    /// <summary>Remove channel operator status from a nick (<c>MODE channel -o nick</c>).</summary>
    /// <exception cref="ArgumentException">If <paramref name="channel"/> or <paramref name="nick"/> is invalid.</exception>
    public Task DeopAsync(string channel, string nick, CancellationToken ct = default)
    {
        ValidateChannelName(channel);
        ValidateNick(nick);
        return _connection.SendLineAsync($"MODE {channel} -o {nick}", ct);
    }

    /// <summary>Give voice to a nick (<c>MODE channel +v nick</c>).</summary>
    /// <exception cref="ArgumentException">If <paramref name="channel"/> or <paramref name="nick"/> is invalid.</exception>
    public Task VoiceAsync(string channel, string nick, CancellationToken ct = default)
    {
        ValidateChannelName(channel);
        ValidateNick(nick);
        return _connection.SendLineAsync($"MODE {channel} +v {nick}", ct);
    }

    /// <summary>Remove voice from a nick (<c>MODE channel -v nick</c>).</summary>
    /// <exception cref="ArgumentException">If <paramref name="channel"/> or <paramref name="nick"/> is invalid.</exception>
    public Task DevoiceAsync(string channel, string nick, CancellationToken ct = default)
    {
        ValidateChannelName(channel);
        ValidateNick(nick);
        return _connection.SendLineAsync($"MODE {channel} -v {nick}", ct);
    }

    /// <summary>
    /// Send a general MODE command. Parameters are appended space-separated after the mode string.
    /// </summary>
    /// <param name="target">Channel name or nick.</param>
    /// <param name="modeString">The mode string, e.g. <c>+mn</c> or <c>-k</c>.</param>
    /// <param name="parameters">Optional list of mode parameters.</param>
    /// <exception cref="ArgumentException">If <paramref name="target"/> or <paramref name="modeString"/> is empty.</exception>
    public Task ModeAsync(
        string target,
        string modeString,
        IReadOnlyList<string>? parameters = null,
        CancellationToken ct = default)
    {
        ValidateTarget(target);
        ArgumentException.ThrowIfNullOrEmpty(modeString, nameof(modeString));

        var line = parameters is { Count: > 0 }
            ? $"MODE {target} {modeString} {string.Join(' ', parameters)}"
            : $"MODE {target} {modeString}";

        return _connection.SendLineAsync(line, ct);
    }

    // ---------------------------------------------------------------------------
    // Phase 3 commands — channel info
    // ---------------------------------------------------------------------------

    /// <summary>Send INVITE to ask the server to invite a nick to a channel.</summary>
    /// <exception cref="ArgumentException">If <paramref name="nick"/> or <paramref name="channel"/> is invalid.</exception>
    public Task InviteAsync(string nick, string channel, CancellationToken ct = default)
    {
        ValidateNick(nick);
        ValidateChannelName(channel);
        return _connection.SendLineAsync($"INVITE {nick} {channel}", ct);
    }

    /// <summary>
    /// Set or clear the topic for a channel.
    /// Passing null or empty <paramref name="text"/> sends a bare <c>TOPIC channel</c>
    /// which clears the topic on servers that allow it.
    /// </summary>
    /// <exception cref="ArgumentException">If <paramref name="channel"/> is invalid.</exception>
    public Task TopicAsync(string channel, string? text = null, CancellationToken ct = default)
    {
        ValidateChannelName(channel);

        var line = string.IsNullOrEmpty(text)
            ? $"TOPIC {channel}"
            : $"TOPIC {channel} :{text}";

        return _connection.SendLineAsync(line, ct);
    }

    /// <summary>
    /// Request the names list for a channel, or a server-wide NAMES if no channel is specified.
    /// </summary>
    /// <exception cref="ArgumentException">If a non-empty <paramref name="channel"/> fails channel name validation.</exception>
    public Task NamesAsync(string? channel = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(channel)) ValidateChannelName(channel);

        var line = string.IsNullOrEmpty(channel) ? "NAMES" : $"NAMES {channel}";
        return _connection.SendLineAsync(line, ct);
    }

    /// <summary>
    /// Request the server's channel list, optionally filtered.
    /// The <paramref name="filter"/> string is passed verbatim (e.g. <c>"&gt;50"</c> for channels
    /// with more than 50 users on servers that support ELIST).
    /// </summary>
    public Task ListAsync(string? filter = null, CancellationToken ct = default)
    {
        var line = string.IsNullOrEmpty(filter) ? "LIST" : $"LIST {filter}";
        return _connection.SendLineAsync(line, ct);
    }

    // ---------------------------------------------------------------------------
    // Phase 3 commands — user queries
    // ---------------------------------------------------------------------------

    /// <summary>Send WHOIS to request information about a nick.</summary>
    /// <exception cref="ArgumentException">If <paramref name="nick"/> is null or empty.</exception>
    public Task WhoisAsync(string nick, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(nick, nameof(nick));
        return _connection.SendLineAsync($"WHOIS {nick}", ct);
    }

    /// <summary>
    /// Send WHO to query user information. Sends a bare <c>WHO</c> (list all visible users)
    /// when <paramref name="mask"/> is omitted.
    /// </summary>
    public Task WhoAsync(string? mask = null, CancellationToken ct = default)
    {
        var line = string.IsNullOrEmpty(mask) ? "WHO" : $"WHO {mask}";
        return _connection.SendLineAsync(line, ct);
    }

    // ---------------------------------------------------------------------------
    // Phase 3 commands — user interaction
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Open a private query with <paramref name="nick"/> and optionally send an opening message.
    /// If <paramref name="message"/> is null or empty no IRC line is sent (the UI opens
    /// the buffer without an initial PRIVMSG).
    /// </summary>
    /// <exception cref="ArgumentException">If <paramref name="nick"/> is invalid.</exception>
    public Task QueryAsync(string nick, string? message = null, CancellationToken ct = default)
    {
        ValidateTarget(nick);

        if (string.IsNullOrEmpty(message))
            return Task.CompletedTask;

        return _connection.SendLineAsync($"PRIVMSG {nick} :{message}", ct);
    }

    /// <summary>
    /// Send a CTCP ACTION (/me) to a channel or nick.
    /// Produces <c>PRIVMSG target :\x01ACTION text\x01</c>.
    /// </summary>
    /// <exception cref="ArgumentException">If <paramref name="target"/> or <paramref name="text"/> is empty.</exception>
    public Task MeAsync(string target, string text, CancellationToken ct = default)
    {
        ValidateTarget(target);
        ValidateText(text, nameof(text));
        return _connection.SendLineAsync($"PRIVMSG {target} :ACTION {text}", ct);
    }

    /// <summary>
    /// Send an arbitrary CTCP request to a nick.
    /// Produces <c>PRIVMSG nick :\x01COMMAND\x01</c> or
    /// <c>PRIVMSG nick :\x01COMMAND params\x01</c>.
    /// </summary>
    /// <exception cref="ArgumentException">If <paramref name="nick"/> or <paramref name="command"/> is empty.</exception>
    public Task CtcpAsync(
        string nick,
        string command,
        string? parameters = null,
        CancellationToken ct = default)
    {
        ValidateTarget(nick);
        ArgumentException.ThrowIfNullOrEmpty(command, nameof(command));

        var line = string.IsNullOrEmpty(parameters)
            ? $"PRIVMSG {nick} :\x01{command}\x01"
            : $"PRIVMSG {nick} :\x01{command} {parameters}\x01";

        return _connection.SendLineAsync(line, ct);
    }

    /// <summary>
    /// Send a CTCP PING to a nick with the current UTC millisecond timestamp as the payload.
    /// The remote client echoes the payload back in a CTCP PING reply, enabling round-trip time measurement.
    /// </summary>
    /// <exception cref="ArgumentException">If <paramref name="nick"/> is invalid.</exception>
    public Task PingAsync(string nick, CancellationToken ct = default)
    {
        ValidateTarget(nick);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return _connection.SendLineAsync($"PRIVMSG {nick} :\x01PING {timestamp}\x01", ct);
    }

    // ---------------------------------------------------------------------------
    // Phase 3 commands — away / back
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Set the away status. Passing a non-empty <paramref name="message"/> marks the user as away;
    /// passing null or empty clears away status (equivalent to <see cref="BackAsync"/>).
    /// </summary>
    public Task AwayAsync(string? message = null, CancellationToken ct = default)
    {
        var line = string.IsNullOrEmpty(message)
            ? "AWAY"
            : $"AWAY :{message}";

        return _connection.SendLineAsync(line, ct);
    }

    /// <summary>Clear away status by sending a bare <c>AWAY</c> command.</summary>
    public Task BackAsync(CancellationToken ct = default) =>
        _connection.SendLineAsync("AWAY", ct);

    // ---------------------------------------------------------------------------
    // Validation helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Basic channel name validation: must start with a recognised channel prefix character
    /// (#, &amp;, +, !) and contain no spaces, commas, or ASCII NUL/BEL.
    /// </summary>
    private static void ValidateChannelName(string channel)
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);

        if (!IsChannelPrefix(channel[0]))
            throw new ArgumentException(
                $"Channel name must start with a channel prefix character (#, &, +, !); got: '{channel}'.",
                nameof(channel));

        foreach (char c in channel)
        {
            if (c is ' ' or ',' or '\0' or '\a')
                throw new ArgumentException(
                    $"Channel name contains an invalid character (U+{(int)c:X4}).", nameof(channel));
        }
    }

    private static bool IsChannelPrefix(char c) => c is '#' or '&' or '+' or '!';

    /// <summary>Target is a channel name or a nick; must be non-empty with no spaces.</summary>
    private static void ValidateTarget(string target)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);

        if (target.Contains(' '))
            throw new ArgumentException("Target must not contain spaces.", nameof(target));
    }

    /// <summary>
    /// Basic nick validation: non-empty, starts with a letter or special character,
    /// no spaces or commas.
    /// </summary>
    private static void ValidateNick(string nick)
    {
        ArgumentException.ThrowIfNullOrEmpty(nick);

        if (nick.Contains(' ') || nick.Contains(','))
            throw new ArgumentException("Nick must not contain spaces or commas.", nameof(nick));

        // First character: letter or one of the RFC-defined special chars ([]\\^_`{|})
        if (!char.IsLetter(nick[0]) && !"[]\\^_`{|}".Contains(nick[0]))
            throw new ArgumentException(
                $"Nick must start with a letter or one of []\\^_`{{|}}; got '{nick[0]}'.",
                nameof(nick));
    }

    private static void ValidateText(string text, string paramName)
    {
        ArgumentException.ThrowIfNullOrEmpty(text, paramName);
    }
}
