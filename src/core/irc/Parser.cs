// SPDX-License-Identifier: GPL-3.0-or-later
using System.Threading.Channels;
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
/// Threading: all inbound lines are routed through a single unbounded channel and
/// processed by one consumer task (DrainAsync). This guarantees in-order dispatch,
/// which is required for multi-line reply assembly (WHOIS, NAMES, etc.).
/// </summary>
public sealed class IRCParser
{
    private readonly string _serverId;
    private readonly EventDispatcher _dispatcher;

    // All parsed messages funnel through this channel so the single consumer processes
    // them in TCP arrival order. This is the same pattern as CapabilityNegotiator.
    private readonly Channel<IrcMessage> _queue = Channel.CreateUnbounded<IrcMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    // Accumulates partial WHOIS replies (311/312/317/330) until 318 flushes them.
    // Safe to access without locks: only the single DrainAsync consumer ever touches these.
    private readonly Dictionary<string, WhoIsAccumulator> _whoisBuffer =
        new(StringComparer.OrdinalIgnoreCase);

    // Accumulates 353 NAMREPLY lines for each channel until 366 flushes them.
    private readonly Dictionary<string, List<NamesEntry>> _namesBuffer =
        new(StringComparer.OrdinalIgnoreCase);

    public IRCParser(string serverId, EventDispatcher dispatcher)
    {
        _serverId = serverId;
        _dispatcher = dispatcher;
        dispatcher.Subscribe<RawLineReceived>(OnRawLineReceived);
        _ = Task.Run(DrainAsync);
    }

    private void OnRawLineReceived(RawLineReceived evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        var msg = ParseMessage(evt.Line);
        if (msg is not null) _queue.Writer.TryWrite(msg.Value);
    }

    private async Task DrainAsync()
    {
        await foreach (var msg in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            await DispatchAsync(msg).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------
    // Dispatch
    // ---------------------------------------------------------------------------

    private async Task DispatchAsync(IrcMessage msg)
    {
        switch (msg.Command)
        {
            // Commands
            case "PRIVMSG": await DispatchPrivMsgAsync(msg).ConfigureAwait(false);      break;
            case "NOTICE":  await DispatchNoticeAsync(msg).ConfigureAwait(false);       break;
            case "JOIN":    await DispatchJoinAsync(msg).ConfigureAwait(false);         break;
            case "PART":    await DispatchPartAsync(msg).ConfigureAwait(false);         break;
            case "KICK":    await DispatchKickAsync(msg).ConfigureAwait(false);         break;
            case "QUIT":    await DispatchQuitAsync(msg).ConfigureAwait(false);         break;
            case "NICK":    await DispatchNickAsync(msg).ConfigureAwait(false);         break;
            case "TOPIC":   await DispatchTopicAsync(msg).ConfigureAwait(false);        break;
            case "INVITE":  await DispatchInviteAsync(msg).ConfigureAwait(false);       break;
            case "WALLOPS": await DispatchWallopsAsync(msg).ConfigureAwait(false);      break;
            case "ERROR":   await DispatchErrorAsync(msg).ConfigureAwait(false);        break;
            case "MODE":    await DispatchModeAsync(msg).ConfigureAwait(false);         break;
            case "AWAY":    await DispatchAwayAsync(msg).ConfigureAwait(false);         break;
            case "CHGHOST": await DispatchChghostAsync(msg).ConfigureAwait(false);      break;
            case "ACCOUNT": await DispatchAccountAsync(msg).ConfigureAwait(false);      break;
            case "SETNAME": await DispatchSetnameAsync(msg).ConfigureAwait(false);      break;
            // Numerics
            case "001":     await DispatchWelcomeAsync(msg).ConfigureAwait(false);      break;
            case "005":     await DispatchIsupportAsync(msg).ConfigureAwait(false);     break;
            case "311":     DispatchWhoisUser(msg);                                     break;
            case "312":     DispatchWhoisServer(msg);                                   break;
            case "315":     await DispatchWhoEndAsync(msg).ConfigureAwait(false);       break;
            case "317":     DispatchWhoisIdle(msg);                                     break;
            case "318":     await DispatchWhoisEndAsync(msg).ConfigureAwait(false);     break;
            case "322":     await DispatchChannelListAsync(msg).ConfigureAwait(false);  break;
            case "323":     await DispatchChannelListEndAsync(msg).ConfigureAwait(false); break;
            case "329":     await DispatchChannelCreatedAsync(msg).ConfigureAwait(false); break;
            case "330":     DispatchWhoisAccount(msg);                                  break;
            case "332":     await DispatchTopicReplyAsync(msg).ConfigureAwait(false);   break;
            case "333":     await DispatchTopicWhoTimeAsync(msg).ConfigureAwait(false); break;
            case "352":     await DispatchWhoReplyAsync(msg).ConfigureAwait(false);     break;
            case "353":     DispatchNamReply(msg);                                      break;
            case "366":     await DispatchEndOfNamesAsync(msg).ConfigureAwait(false);   break;
            case "367":     await DispatchBanListAsync(msg).ConfigureAwait(false);      break;
            case "368":     await DispatchBanListEndAsync(msg).ConfigureAwait(false);   break;
            case "372":     await DispatchMotdAsync(msg).ConfigureAwait(false);         break;
            case "376":     await DispatchMotdEndAsync(msg).ConfigureAwait(false);      break;
            case "433":     await DispatchNickInUseAsync(msg).ConfigureAwait(false);    break;
            case "730":     await DispatchMonitorOnlineAsync(msg).ConfigureAwait(false); break;
            case "731":     await DispatchMonitorOfflineAsync(msg).ConfigureAwait(false); break;
        }
    }

    // ---------------------------------------------------------------------------
    // Command handlers
    // ---------------------------------------------------------------------------

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

    /// <summary>
    /// MODE: route to channel or user handler based on the first parameter.
    /// Channel modes start with a recognized channel prefix char (#, &amp;, +, !).
    /// </summary>
    private Task DispatchModeAsync(IrcMessage msg)
    {
        if (msg.Params.Length < 2) return Task.CompletedTask;

        string target  = msg.Param(0);
        string modeStr = msg.Param(1);

        if (target.Length > 0 && IsChannelPrefix(target[0]))
        {
            var modeParams = new ArraySegment<string>(msg.Params, 2, msg.Params.Length - 2).ToArray();
            string setter  = msg.Nick ?? msg.Prefix ?? string.Empty;
            return _dispatcher.PublishAsync(
                new ChannelModeChanged(_serverId, target, modeStr, modeParams, setter),
                EventPriority.Normal).AsTask();
        }

        return _dispatcher.PublishAsync(
            new UserModeChanged(_serverId, target, modeStr),
            EventPriority.Normal).AsTask();
    }

    /// <summary>
    /// AWAY (IRCv3 away-notify): presence of a param means the user went away;
    /// absence means they came back.
    /// </summary>
    private Task DispatchAwayAsync(IrcMessage msg)
    {
        if (msg.Nick is null) return Task.CompletedTask;
        bool isAway    = msg.Params.Length > 0 && !string.IsNullOrEmpty(msg.Param(0));
        string? awayMsg = isAway ? msg.Param(0) : null;
        return _dispatcher.PublishAsync(
            new UserAwayChanged(_serverId, msg.Nick, isAway, awayMsg),
            EventPriority.Normal).AsTask();
    }

    /// <summary>CHGHOST (IRCv3): user changed ident or host without a QUIT/JOIN cycle.</summary>
    private Task DispatchChghostAsync(IrcMessage msg)
    {
        if (msg.Nick is null || msg.Params.Length < 2) return Task.CompletedTask;
        return _dispatcher.PublishAsync(
            new UserHostChanged(_serverId, msg.Nick, msg.Param(0), msg.Param(1)),
            EventPriority.Normal).AsTask();
    }

    /// <summary>ACCOUNT (IRCv3 account-notify): user logged in or out of a services account.</summary>
    private Task DispatchAccountAsync(IrcMessage msg)
    {
        if (msg.Nick is null) return Task.CompletedTask;
        // "*" means logged out; any other value is the account name.
        string? account = msg.Param(0) == "*" ? null : msg.Param(0);
        return _dispatcher.PublishAsync(
            new UserAccountChanged(_serverId, msg.Nick, account),
            EventPriority.Normal).AsTask();
    }

    /// <summary>SETNAME (IRCv3): user changed their realname.</summary>
    private Task DispatchSetnameAsync(IrcMessage msg)
    {
        if (msg.Nick is null) return Task.CompletedTask;
        return _dispatcher.PublishAsync(
            new UserRealNameChanged(_serverId, msg.Nick, msg.Param(0)),
            EventPriority.Normal).AsTask();
    }

    // ---------------------------------------------------------------------------
    // Numeric handlers
    // ---------------------------------------------------------------------------

    private Task DispatchWelcomeAsync(IrcMessage msg) =>
        _dispatcher.PublishAsync(
            new WelcomeReceived(_serverId, msg.Param(0)),
            EventPriority.Normal).AsTask();

    /// <summary>
    /// 005 RPL_ISUPPORT: extract key=value token pairs (excluding the first param
    /// which is the client nick and the last which is the human-readable "are supported" text).
    /// </summary>
    private Task DispatchIsupportAsync(IrcMessage msg)
    {
        // Minimum: client_nick + at_least_one_token + "are supported" trailing = 3 params.
        if (msg.Params.Length < 3) return Task.CompletedTask;

        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int last = msg.Params.Length - 1; // trailing "are supported..." text
        for (int i = 1; i < last; i++)
        {
            var token = msg.Params[i];
            int eq    = token.IndexOf('=');
            tokens[eq < 0 ? token : token[..eq]] = eq < 0 ? string.Empty : token[(eq + 1)..];
        }

        if (tokens.Count == 0) return Task.CompletedTask;

        return _dispatcher.PublishAsync(
            new IsupportTokensReceived(_serverId, tokens),
            EventPriority.Normal).AsTask();
    }

    // WHOIS accumulation: 311 -> 312 -> 317 -> 330 -> 318 (flush)

    /// <summary>311 RPL_WHOISUSER: start accumulating a WHOIS reply.</summary>
    private void DispatchWhoisUser(IrcMessage msg)
    {
        // :server 311 me target user host * :realname
        if (msg.Params.Length < 5) return;
        string nick = msg.Param(1);
        _whoisBuffer[nick] = new WhoIsAccumulator
        {
            Nick     = nick,
            User     = msg.Param(2),
            Host     = msg.Param(3),
            // Param(4) is always "*"; param(5) is realname.
            RealName = msg.Param(5),
        };
    }

    /// <summary>312 RPL_WHOISSERVER: add server name to the in-progress WHOIS reply.</summary>
    private void DispatchWhoisServer(IrcMessage msg)
    {
        // :server 312 me target servername :server info
        if (_whoisBuffer.TryGetValue(msg.Param(1), out var acc))
            acc.ServerName = msg.Param(2);
    }

    /// <summary>317 RPL_WHOISIDLE: add idle seconds to the in-progress WHOIS reply.</summary>
    private void DispatchWhoisIdle(IrcMessage msg)
    {
        // :server 317 me target idleseconds signontime :idle and signon time
        if (_whoisBuffer.TryGetValue(msg.Param(1), out var acc)
            && int.TryParse(msg.Param(2), out int idle))
        {
            acc.IdleSeconds = idle;
        }
    }

    /// <summary>330 RPL_WHOISACCOUNT: add account name to the in-progress WHOIS reply.</summary>
    private void DispatchWhoisAccount(IrcMessage msg)
    {
        // :server 330 me target account :is logged in as
        if (_whoisBuffer.TryGetValue(msg.Param(1), out var acc))
            acc.Account = msg.Param(2);
    }

    /// <summary>318 RPL_ENDOFWHOIS: flush the accumulated reply and emit both events.</summary>
    private async Task DispatchWhoisEndAsync(IrcMessage msg)
    {
        // :server 318 me target :End of /WHOIS list
        string nick = msg.Param(1);

        _whoisBuffer.TryGetValue(nick, out var acc);
        if (acc is not null) _whoisBuffer.Remove(nick);

        if (acc is not null)
        {
            await _dispatcher.PublishAsync(
                new WhoIsReply(_serverId, acc.Nick, acc.User, acc.Host, acc.RealName,
                    acc.ServerName, acc.IdleSeconds, acc.Account),
                EventPriority.Low).AsTask().ConfigureAwait(false);
        }

        await _dispatcher.PublishAsync(
            new WhoIsEnd(_serverId, nick),
            EventPriority.Low).AsTask().ConfigureAwait(false);
    }

    /// <summary>315 RPL_ENDOFWHO: WHO reply for a target is complete.</summary>
    private Task DispatchWhoEndAsync(IrcMessage msg) =>
        _dispatcher.PublishAsync(
            new WhoEnd(_serverId, msg.Param(1)),
            EventPriority.Low).AsTask();

    /// <summary>352 RPL_WHOREPLY: one entry from a WHO query.</summary>
    private Task DispatchWhoReplyAsync(IrcMessage msg)
    {
        // :server 352 me channel user host server nick H/G :hops realname
        if (msg.Params.Length < 7) return Task.CompletedTask;

        string channel = msg.Param(1);

        // The last param is "hops realname" - split on first space.
        var hopRealname = msg.Param(7);
        int sp = hopRealname.IndexOf(' ');
        string realname = sp >= 0 ? hopRealname[(sp + 1)..] : hopRealname;

        return _dispatcher.PublishAsync(
            new WhoReplyEntry(
                _serverId,
                channel == "*" ? null : channel,
                msg.Param(5),   // nick
                msg.Param(2),   // user
                msg.Param(3),   // host
                null,           // account not in standard WHO reply
                realname),
            EventPriority.Low).AsTask();
    }

    /// <summary>322 RPL_LIST: one channel entry from a LIST query.</summary>
    private Task DispatchChannelListAsync(IrcMessage msg)
    {
        // :server 322 me #channel usercount :topic
        if (msg.Params.Length < 3) return Task.CompletedTask;
        int.TryParse(msg.Param(2), out int count);
        return _dispatcher.PublishAsync(
            new ChannelListEntry(_serverId, msg.Param(1), count, msg.Param(3)),
            EventPriority.Low).AsTask();
    }

    /// <summary>323 RPL_LISTEND: LIST query is complete.</summary>
    private Task DispatchChannelListEndAsync(IrcMessage msg) =>
        _dispatcher.PublishAsync(
            new ChannelListEnd(_serverId),
            EventPriority.Low).AsTask();

    /// <summary>329 RPL_CREATIONTIME: channel creation timestamp.</summary>
    private Task DispatchChannelCreatedAsync(IrcMessage msg)
    {
        // :server 329 me #channel timestamp
        if (msg.Params.Length < 3) return Task.CompletedTask;
        if (!long.TryParse(msg.Param(2), out long ts)) return Task.CompletedTask;
        return _dispatcher.PublishAsync(
            new ChannelCreated(_serverId, msg.Param(1), DateTimeOffset.FromUnixTimeSeconds(ts)),
            EventPriority.Low).AsTask();
    }

    /// <summary>
    /// 332 RPL_TOPIC: topic text received on channel join or TOPIC query.
    /// SetterNick is null because the setter is provided separately in 333.
    /// </summary>
    private Task DispatchTopicReplyAsync(IrcMessage msg)
    {
        // :server 332 me #channel :topic text
        if (msg.Params.Length < 3) return Task.CompletedTask;
        return _dispatcher.PublishAsync(
            new TopicChanged(_serverId, msg.Param(1), msg.Param(2), null),
            EventPriority.Normal).AsTask();
    }

    /// <summary>333 RPL_TOPICWHOTIME: topic setter and time.</summary>
    private Task DispatchTopicWhoTimeAsync(IrcMessage msg)
    {
        // :server 333 me #channel nick timestamp
        if (msg.Params.Length < 4) return Task.CompletedTask;
        if (!long.TryParse(msg.Param(3), out long ts)) return Task.CompletedTask;
        return _dispatcher.PublishAsync(
            new TopicWhoTime(_serverId, msg.Param(1), msg.Param(2),
                DateTimeOffset.FromUnixTimeSeconds(ts)),
            EventPriority.Low).AsTask();
    }

    /// <summary>
    /// 353 RPL_NAMREPLY: accumulate one batch of nicks for the channel.
    /// The complete list is emitted on 366.
    /// </summary>
    private void DispatchNamReply(IrcMessage msg)
    {
        // :server 353 me = #channel :@nick1 +nick2 nick3
        // Params: [0]=me, [1]=channeltype(=/*/+), [2]=channel, [3]=nicks
        if (msg.Params.Length < 4) return;

        string channel = msg.Param(2);
        if (!_namesBuffer.TryGetValue(channel, out var list))
        {
            list = [];
            _namesBuffer[channel] = list;
        }

        foreach (var token in msg.Param(3).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var (nick, prefixes) = SplitNickPrefixes(token);
            list.Add(new NamesEntry(nick, prefixes));
        }
    }

    /// <summary>366 RPL_ENDOFNAMES: flush accumulated NAMES list for the channel.</summary>
    private Task DispatchEndOfNamesAsync(IrcMessage msg)
    {
        // :server 366 me #channel :End of /NAMES list
        string channel = msg.Param(1);

        _namesBuffer.TryGetValue(channel, out var list);
        if (list is not null) _namesBuffer.Remove(channel);

        if (list is null || list.Count == 0) return Task.CompletedTask;

        return _dispatcher.PublishAsync(
            new NamesListReceived(_serverId, channel, list),
            EventPriority.Normal).AsTask();
    }

    /// <summary>367 RPL_BANLIST: one entry from a ban list query.</summary>
    private Task DispatchBanListAsync(IrcMessage msg)
    {
        // :server 367 me #channel mask setter timestamp
        if (msg.Params.Length < 4) return Task.CompletedTask;

        var setAt = DateTimeOffset.MinValue;
        if (msg.Params.Length >= 5 && long.TryParse(msg.Param(4), out long ts))
            setAt = DateTimeOffset.FromUnixTimeSeconds(ts);

        return _dispatcher.PublishAsync(
            new BanListEntry(_serverId, msg.Param(1), msg.Param(2), msg.Param(3), setAt),
            EventPriority.Low).AsTask();
    }

    /// <summary>368 RPL_ENDOFBANLIST: ban list query is complete.</summary>
    private Task DispatchBanListEndAsync(IrcMessage msg) =>
        _dispatcher.PublishAsync(
            new BanListEnd(_serverId, msg.Param(1)),
            EventPriority.Low).AsTask();

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

    /// <summary>
    /// 730 RPL_MONONLINE: one or more nicks on the MONITOR list are now online.
    /// The param is a comma-separated list of nick!user@host entries.
    /// </summary>
    private async Task DispatchMonitorOnlineAsync(IrcMessage msg)
    {
        // :server 730 me :nick!user@host[,nick!user@host...]
        if (msg.Params.Length < 2) return;

        foreach (var entry in msg.Param(1).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int bang = entry.IndexOf('!');
            string nick = bang >= 0 ? entry[..bang] : entry;
            await _dispatcher.PublishAsync(
                new MonitorStatusChanged(_serverId, nick, IsOnline: true),
                EventPriority.Normal).AsTask().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 731 RPL_MONOFFLINE: one or more nicks on the MONITOR list are offline.
    /// The param is a comma-separated list of nicks (no user@host).
    /// </summary>
    private async Task DispatchMonitorOfflineAsync(IrcMessage msg)
    {
        // :server 731 me :nick[,nick...]
        if (msg.Params.Length < 2) return;

        foreach (var nick in msg.Param(1).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            await _dispatcher.PublishAsync(
                new MonitorStatusChanged(_serverId, nick, IsOnline: false),
                EventPriority.Normal).AsTask().ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------------------
    // Parsing -- all static; no side effects; safe to call from unit tests directly
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
    /// \: => ;   \s => space   \\ => \   \r => CR   \n => LF   any other \X => X
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
                // Bare key (no value) -- treated as empty string per spec
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

    /// <summary>
    /// Split a NAMES token (e.g. "@+alice") into its prefix chars and bare nick.
    /// Recognises the standard IRC mode prefix characters: ~ &amp; @ % +.
    /// Full support for non-standard prefix chars requires an ISUPPORT PREFIX lookup,
    /// which is handled by the state model layer after ISUPPORT tokens are received.
    /// </summary>
    private static (string Nick, char[] Prefixes) SplitNickPrefixes(string token)
    {
        const string KnownPrefixes = "~&@%+";
        int i = 0;
        while (i < token.Length && KnownPrefixes.Contains(token[i])) i++;
        return (token[i..], token[..i].ToCharArray());
    }

    private static bool IsChannelPrefix(char c) => c is '#' or '&' or '+' or '!';

    // ---------------------------------------------------------------------------
    // WHOIS accumulator
    // ---------------------------------------------------------------------------

    private sealed class WhoIsAccumulator
    {
        public string Nick       = string.Empty;
        public string User       = string.Empty;
        public string Host       = string.Empty;
        public string RealName   = string.Empty;
        public string ServerName = string.Empty;
        public int    IdleSeconds;
        public string? Account;
    }
}
