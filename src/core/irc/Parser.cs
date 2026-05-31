// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.Events;

namespace DataJack.Core.Irc;

/// <summary>
/// Fully parsed representation of one IRCv3 message line.
/// Constructed only by <see cref="IRCParser.ParseMessage"/>; exposed as internal for unit testing.
/// </summary>
internal readonly struct IrcMessage
{
    /// <summary>IRCv3 message tags, or null if none were present.</summary>
    public required IReadOnlyDictionary<string, string>? Tags { get; init; }

    /// <summary>Raw prefix string (server name or nick!user@host), or null.</summary>
    public required string? Prefix { get; init; }

    /// <summary>Nick extracted from a nick!user@host prefix, or null.</summary>
    public required string? Nick { get; init; }

    /// <summary>User/ident extracted from a nick!user@host prefix, or null.</summary>
    public required string? User { get; init; }

    /// <summary>Hostname extracted from a nick!user@host prefix, or null.</summary>
    public required string? Host { get; init; }

    /// <summary>IRC command (verb or 3-digit numeric), upper-cased for switch matching.</summary>
    public required string Command { get; init; }

    /// <summary>
    /// All parameters. The trailing parameter (introduced by ':') has its colon stripped
    /// and appears as the last element like any other param.
    /// </summary>
    public required string[] Params { get; init; }

    /// <summary>Convenience accessor; returns empty string rather than throwing on out-of-range.</summary>
    public string Param(int index) => index < Params.Length ? Params[index] : string.Empty;
}

/// <summary>
/// Subscribes to <see cref="RawLineReceived"/> events, parses each line using the IRCv3
/// message format, and dispatches typed events onto the same bus.
///
/// One instance per server connection. Filters incoming events by <c>serverId</c>.
///
/// Character encoding note: the parser receives already-decoded strings from
/// <see cref="IRCConnection"/>. Byte-level encoding detection (EncodingWarning) is a
/// future refinement once the encode/decode boundary is finalised.
/// </summary>
public sealed class IRCParser
{
    private readonly string _serverId;
    private readonly EventDispatcher _dispatcher;

    public IRCParser(string serverId, EventDispatcher dispatcher)
    {
        _serverId = serverId;
        _dispatcher = dispatcher;
        dispatcher.Subscribe<RawLineReceived>(OnRawLineReceived);
    }

    private void OnRawLineReceived(RawLineReceived evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;

        var msg = ParseMessage(evt.Line);
        if (msg is null) return;

        // Schedule the typed-event dispatch on the thread pool so the dispatch thread
        // is immediately free to process the events we are about to publish.
        // Running DispatchAsync on the dispatch thread itself would mean the published
        // events (e.g. ActionReceived) sit in the channel until the current handler
        // returns, which creates a fragile ordering dependency.
        _ = Task.Run(() => DispatchAsync(msg.Value));
    }

    // ---------------------------------------------------------------------------
    // Dispatch
    // ---------------------------------------------------------------------------

    private async Task DispatchAsync(IrcMessage msg)
    {
        switch (msg.Command)
        {
            case "PRIVMSG": await DispatchPrivMsgAsync(msg).ConfigureAwait(false); break;
            case "NOTICE":  await DispatchNoticeAsync(msg).ConfigureAwait(false);  break;
            case "JOIN":    await DispatchJoinAsync(msg).ConfigureAwait(false);    break;
            case "PART":    await DispatchPartAsync(msg).ConfigureAwait(false);    break;
            case "KICK":    await DispatchKickAsync(msg).ConfigureAwait(false);    break;
            case "QUIT":    await DispatchQuitAsync(msg).ConfigureAwait(false);    break;
            case "NICK":    await DispatchNickAsync(msg).ConfigureAwait(false);    break;
            case "TOPIC":   await DispatchTopicAsync(msg).ConfigureAwait(false);   break;
            case "INVITE":  await DispatchInviteAsync(msg).ConfigureAwait(false);  break;
            case "WALLOPS": await DispatchWallopsAsync(msg).ConfigureAwait(false); break;
            case "ERROR":   await DispatchErrorAsync(msg).ConfigureAwait(false);   break;
            case "001":     await DispatchWelcomeAsync(msg).ConfigureAwait(false); break;
            case "372":     await DispatchMotdAsync(msg).ConfigureAwait(false);    break;
            case "376":     await DispatchMotdEndAsync(msg).ConfigureAwait(false); break;
            case "433":     await DispatchNickInUseAsync(msg).ConfigureAwait(false); break;
            // All other commands/numerics are silently ignored in Phase 1.
        }
    }

    private Task DispatchPrivMsgAsync(IrcMessage msg)
    {
        if (msg.Nick is null || msg.Params.Length < 2) return Task.CompletedTask;

        string target = msg.Param(0);
        string text   = msg.Param(1);

        // CTCP messages are wrapped in \x01 delimiters.
        if (text.StartsWith('\x01') && text.EndsWith('\x01') && text.Length >= 2)
        {
            var body = text[1..^1]; // strip delimiters
            int space = body.IndexOf(' ');
            string ctcpCmd    = space < 0 ? body : body[..space];
            string? ctcpParam = space < 0 ? null : body[(space + 1)..];

            if (ctcpCmd.Equals("ACTION", StringComparison.OrdinalIgnoreCase))
            {
                return _dispatcher.PublishAsync(
                    new ActionReceived(_serverId, target, msg.Nick, ctcpParam ?? string.Empty, msg.Tags),
                    EventPriority.Normal).AsTask();
            }

            return _dispatcher.PublishAsync(
                new CtcpRequest(_serverId, msg.Nick, ctcpCmd, ctcpParam),
                EventPriority.Normal).AsTask();
        }

        return _dispatcher.PublishAsync(
            new MessageReceived(_serverId, target, msg.Nick, text, msg.Tags, IsSelf: false),
            EventPriority.Normal).AsTask();
    }

    private Task DispatchNoticeAsync(IrcMessage msg)
    {
        string target = msg.Param(0);
        string text   = msg.Param(1);

        // CTCP reply
        if (msg.Nick is not null && text.StartsWith('\x01') && text.EndsWith('\x01') && text.Length >= 2)
        {
            var body = text[1..^1];
            int space = body.IndexOf(' ');
            string ctcpCmd    = space < 0 ? body : body[..space];
            string? ctcpParam = space < 0 ? null : body[(space + 1)..];
            return _dispatcher.PublishAsync(
                new CtcpReply(_serverId, msg.Nick, ctcpCmd, ctcpParam),
                EventPriority.Normal).AsTask();
        }

        // Server notice (no nick prefix)
        if (msg.Nick is null)
        {
            return _dispatcher.PublishAsync(
                new ServerNoticeReceived(_serverId, text),
                EventPriority.Normal).AsTask();
        }

        return _dispatcher.PublishAsync(
            new NoticeReceived(_serverId, target, msg.Nick, text, msg.Tags),
            EventPriority.Normal).AsTask();
    }

    private Task DispatchJoinAsync(IrcMessage msg)
    {
        if (msg.Nick is null) return Task.CompletedTask;
        string channel = msg.Param(0);
        // extended-join provides account as param 1 ("*" means not identified)
        string? account = msg.Params.Length > 1 && msg.Param(1) != "*" ? msg.Param(1) : null;
        return _dispatcher.PublishAsync(
            new JoinedChannel(_serverId, channel, msg.Nick, account),
            EventPriority.Normal).AsTask();
    }

    private Task DispatchPartAsync(IrcMessage msg)
    {
        if (msg.Nick is null) return Task.CompletedTask;
        return _dispatcher.PublishAsync(
            new PartedChannel(_serverId, msg.Param(0), msg.Nick, msg.Params.Length > 1 ? msg.Param(1) : null),
            EventPriority.Normal).AsTask();
    }

    private Task DispatchKickAsync(IrcMessage msg)
    {
        if (msg.Nick is null || msg.Params.Length < 2) return Task.CompletedTask;
        return _dispatcher.PublishAsync(
            new KickReceived(_serverId, msg.Param(0), msg.Param(1), msg.Nick,
                msg.Params.Length > 2 ? msg.Param(2) : null),
            EventPriority.Normal).AsTask();
    }

    private Task DispatchQuitAsync(IrcMessage msg)
    {
        if (msg.Nick is null) return Task.CompletedTask;
        return _dispatcher.PublishAsync(
            new UserQuit(_serverId, msg.Nick, msg.Params.Length > 0 ? msg.Param(0) : null),
            EventPriority.Normal).AsTask();
    }

    private Task DispatchNickAsync(IrcMessage msg)
    {
        if (msg.Nick is null) return Task.CompletedTask;
        return _dispatcher.PublishAsync(
            new NickChanged(_serverId, msg.Nick, msg.Param(0)),
            EventPriority.Normal).AsTask();
    }

    private Task DispatchTopicAsync(IrcMessage msg)
    {
        if (msg.Nick is null || msg.Params.Length < 2) return Task.CompletedTask;
        return _dispatcher.PublishAsync(
            new TopicChanged(_serverId, msg.Param(0), msg.Param(1), msg.Nick),
            EventPriority.Normal).AsTask();
    }

    private Task DispatchInviteAsync(IrcMessage msg)
    {
        if (msg.Nick is null || msg.Params.Length < 2) return Task.CompletedTask;
        return _dispatcher.PublishAsync(
            new InviteReceived(_serverId, msg.Param(1), msg.Nick),
            EventPriority.Normal).AsTask();
    }

    private Task DispatchWallopsAsync(IrcMessage msg)
    {
        if (msg.Nick is null) return Task.CompletedTask;
        return _dispatcher.PublishAsync(
            new WallopsReceived(_serverId, msg.Nick, msg.Param(0)),
            EventPriority.Normal).AsTask();
    }

    private Task DispatchErrorAsync(IrcMessage msg) =>
        _dispatcher.PublishAsync(
            new ErrorReceived(_serverId, msg.Param(0)),
            EventPriority.Critical).AsTask();

    private Task DispatchWelcomeAsync(IrcMessage msg) =>
        _dispatcher.PublishAsync(
            new WelcomeReceived(_serverId, msg.Param(0)),
            EventPriority.Normal).AsTask();

    private Task DispatchMotdAsync(IrcMessage msg) =>
        _dispatcher.PublishAsync(
            new MOTDReceived(_serverId, msg.Param(1)),
            EventPriority.Low).AsTask();

    private Task DispatchMotdEndAsync(IrcMessage msg) =>
        _dispatcher.PublishAsync(
            new MOTDEnd(_serverId),
            EventPriority.Low).AsTask();

    private Task DispatchNickInUseAsync(IrcMessage msg) =>
        _dispatcher.PublishAsync(
            new NickInUse(_serverId, msg.Param(1)),
            EventPriority.Critical).AsTask();

    // ---------------------------------------------------------------------------
    // Parsing — all static; no side effects; safe to call from unit tests directly
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Parse a single raw IRC line into an <see cref="IrcMessage"/>.
    /// Returns null for empty or malformed lines that cannot be represented as a message.
    /// </summary>
    internal static IrcMessage? ParseMessage(string line)
    {
        if (string.IsNullOrEmpty(line)) return null;

        var span = line.AsSpan();

        // 1. Tags  (@key=value;key2=value2 ...)
        IReadOnlyDictionary<string, string>? tags = null;
        if (span.StartsWith("@"))
        {
            span = span[1..];
            int sp = span.IndexOf(' ');
            if (sp < 0) return null;
            tags = ParseTags(span[..sp]);
            span = span[(sp + 1)..].TrimStart(' ');
        }

        // 2. Prefix  (:server or :nick!user@host)
        string? prefix = null, nick = null, user = null, host = null;
        if (!span.IsEmpty && span[0] == ':')
        {
            span = span[1..];
            int sp = span.IndexOf(' ');
            if (sp < 0) return null;
            prefix = span[..sp].ToString();
            span = span[(sp + 1)..].TrimStart(' ');
            ParseNickUserHost(prefix, out nick, out user, out host);
        }

        // 3. Command
        if (span.IsEmpty) return null;
        int cmdEnd = span.IndexOf(' ');
        string command;
        if (cmdEnd < 0)
        {
            command = span.ToString().ToUpperInvariant();
            span = default;
        }
        else
        {
            command = span[..cmdEnd].ToString().ToUpperInvariant();
            span = span[(cmdEnd + 1)..];
        }

        // 4. Params
        var @params = ParseParams(span);

        return new IrcMessage
        {
            Tags    = tags,
            Prefix  = prefix,
            Nick    = nick,
            User    = user,
            Host    = host,
            Command = command,
            Params  = @params,
        };
    }

    /// <summary>
    /// Parse IRCv3 message tags. Unescapes values per the spec:
    /// \: → ;   \s → space   \\ → \   \r → CR   \n → LF   any other \X → X
    /// </summary>
    internal static Dictionary<string, string> ParseTags(ReadOnlySpan<char> raw)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        while (!raw.IsEmpty)
        {
            int semi = raw.IndexOf(';');
            ReadOnlySpan<char> pair = semi < 0 ? raw : raw[..semi];
            raw = semi < 0 ? default : raw[(semi + 1)..];

            int eq = pair.IndexOf('=');
            if (eq < 0)
            {
                // Bare key (no value) — treated as empty string per spec
                result[pair.ToString()] = string.Empty;
            }
            else
            {
                result[pair[..eq].ToString()] = UnescapeTagValue(pair[(eq + 1)..]);
            }
        }

        return result;
    }

    private static string UnescapeTagValue(ReadOnlySpan<char> raw)
    {
        // Fast path: no backslash means nothing to unescape.
        if (raw.IndexOf('\\') < 0)
            return raw.ToString();

        var sb = new System.Text.StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\\' && i + 1 < raw.Length)
            {
                i++;
                sb.Append(raw[i] switch
                {
                    ':' => ';',
                    's' => ' ',
                    '\\' => '\\',
                    'r' => '\r',
                    'n' => '\n',
                    _ => raw[i], // undefined escape: keep the character after the backslash
                });
            }
            else
            {
                sb.Append(raw[i]);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Decompose a prefix into nick, user, and host components.
    /// If the prefix contains no '!', it is a server name and all three outputs are null.
    /// </summary>
    internal static void ParseNickUserHost(
        string prefix,
        out string? nick,
        out string? user,
        out string? host)
    {
        int bang = prefix.IndexOf('!');
        if (bang < 0)
        {
            nick = user = host = null;
            return;
        }

        nick = prefix[..bang];
        var rest = prefix[(bang + 1)..];
        int at = rest.IndexOf('@');
        if (at < 0)
        {
            user = rest;
            host = null;
        }
        else
        {
            user = rest[..at];
            host = rest[(at + 1)..];
        }
    }

    private static string[] ParseParams(ReadOnlySpan<char> span)
    {
        var result = new List<string>(15);
        while (!span.IsEmpty)
        {
            if (span[0] == ':')
            {
                // Trailing parameter: the rest of the line (may contain spaces and colons).
                result.Add(span[1..].ToString());
                break;
            }

            int sp = span.IndexOf(' ');
            if (sp < 0)
            {
                result.Add(span.ToString());
                break;
            }

            result.Add(span[..sp].ToString());
            span = span[(sp + 1)..].TrimStart(' ');
        }
        return result.ToArray();
    }
}
