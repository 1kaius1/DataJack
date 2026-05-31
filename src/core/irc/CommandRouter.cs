// SPDX-License-Identifier: GPL-3.0-or-later

namespace DataJack.Core.Irc;

/// <summary>
/// Translates user slash commands into correctly-formatted IRC protocol lines sent via
/// <see cref="IRCConnection.SendLineAsync"/>. Validates arguments before sending.
/// See ARCHITECTURE.md §4.6 and §13.
///
/// Phase 1 minimum: /join, /part, /msg, /notice, /nick, /quit, /raw.
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
