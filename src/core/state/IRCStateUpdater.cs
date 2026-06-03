// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.Events;

namespace DataJack.Core.State;

/// <summary>
/// Subscribes to all IRC protocol events for one server and drives
/// <see cref="IRCStateModel.Apply"/> to keep the snapshot tree current.
///
/// One instance is created per server connection. All handlers execute
/// synchronously on the event dispatch thread, which is the sole permitted
/// caller of <see cref="IRCStateModel.Apply"/>.
///
/// Phase 3 scope:
///   Connection lifecycle (established / closed / welcome);
///   ISUPPORT token accumulation;
///   IRCv3 capability active-set management;
///   Channel membership (join, part, kick, quit, nick rename);
///   Topic, mode, NAMES, creation time;
///   Per-user metadata backfill (CHGHOST, away, account, setname);
///   WHO and WHOIS user-info backfill in channel user records;
///   MONITOR online/offline status.
///
/// User-level (non-channel) MODE changes are not reflected in the snapshot
/// because <see cref="ServerState"/> has no field for them; deferred to Phase 4.
/// </summary>
public sealed class IRCStateUpdater
{
    private readonly string _serverId;
    private readonly IRCStateModel _model;

    public IRCStateUpdater(string serverId, IRCStateModel model, EventDispatcher dispatcher)
    {
        _serverId = serverId;
        _model    = model;

        dispatcher.Subscribe<ConnectionAttempted>(OnConnectionAttempted);
        dispatcher.Subscribe<ConnectionEstablished>(OnConnectionEstablished);
        dispatcher.Subscribe<ConnectionClosed>(OnConnectionClosed);
        dispatcher.Subscribe<WelcomeReceived>(OnWelcomeReceived);
        dispatcher.Subscribe<IsupportTokensReceived>(OnIsupportTokensReceived);
        dispatcher.Subscribe<CapabilityNegotiated>(OnCapabilityNegotiated);
        dispatcher.Subscribe<ServerCapabilityChanged>(OnServerCapabilityChanged);
        dispatcher.Subscribe<NickChanged>(OnNickChanged);
        dispatcher.Subscribe<JoinedChannel>(OnJoinedChannel);
        dispatcher.Subscribe<PartedChannel>(OnPartedChannel);
        dispatcher.Subscribe<KickReceived>(OnKickReceived);
        dispatcher.Subscribe<UserQuit>(OnUserQuit);
        dispatcher.Subscribe<TopicChanged>(OnTopicChanged);
        dispatcher.Subscribe<TopicWhoTime>(OnTopicWhoTime);
        dispatcher.Subscribe<ChannelCreated>(OnChannelCreated);
        dispatcher.Subscribe<ChannelModeChanged>(OnChannelModeChanged);
        dispatcher.Subscribe<NamesListReceived>(OnNamesListReceived);
        dispatcher.Subscribe<WhoReplyEntry>(OnWhoReplyEntry);
        dispatcher.Subscribe<WhoIsReply>(OnWhoIsReply);
        dispatcher.Subscribe<UserHostChanged>(OnUserHostChanged);
        dispatcher.Subscribe<UserAwayChanged>(OnUserAwayChanged);
        dispatcher.Subscribe<UserAccountChanged>(OnUserAccountChanged);
        dispatcher.Subscribe<UserRealNameChanged>(OnUserRealNameChanged);
        dispatcher.Subscribe<MonitorStatusChanged>(OnMonitorStatusChanged);
    }

    // Apply a mutation to this server's entry. No-op if the server is not yet in state.
    private void ApplyServerMutation(Func<ServerState, ServerState> mutate)
    {
        _model.Apply(w =>
        {
            if (!w.Servers.TryGetValue(_serverId, out var server)) return w;
            return w with
            {
                Servers = new Dictionary<string, ServerState>(w.Servers) { [_serverId] = mutate(server) }
            };
        });
    }

    // Return a copy of the channel dictionary with one user updated in every channel
    // they appear in. Channels that do not contain the nick are copied unchanged.
    private static Dictionary<string, ChannelState> UpdateUserInAllChannels(
        IReadOnlyDictionary<string, ChannelState> channels,
        string nick,
        Func<ChannelUser, ChannelUser> mutate)
    {
        var result = new Dictionary<string, ChannelState>(channels);
        foreach (var (name, ch) in channels)
        {
            if (!ch.Users.TryGetValue(nick, out var user)) continue;
            result[name] = ch with
            {
                Users = new Dictionary<string, ChannelUser>(ch.Users) { [nick] = mutate(user) }
            };
        }
        return result;
    }

    // ---------------------------------------------------------------------------
    // Connection lifecycle
    // ---------------------------------------------------------------------------

    private void OnConnectionAttempted(ConnectionAttempted evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        _model.Apply(w =>
        {
            if (w.Servers.ContainsKey(_serverId)) return w;
            var entry = ServerState.CreateDisconnected(_serverId, evt.Address, evt.Port, evt.Tls);
            return w with
            {
                Servers = new Dictionary<string, ServerState>(w.Servers) { [_serverId] = entry }
            };
        });
    }

    private void OnConnectionEstablished(ConnectionEstablished evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        ApplyServerMutation(s => s with
        {
            IsConnected = true,
            ConnectedAt = DateTimeOffset.UtcNow,
        });
    }

    private void OnConnectionClosed(ConnectionClosed evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        ApplyServerMutation(s => s with
        {
            IsConnected        = false,
            ConnectedAt        = null,
            RegisteredNick     = null,
            ActiveCapabilities = new HashSet<string>(),
            IsupportTokens     = new Dictionary<string, string>(),
            Channels           = new Dictionary<string, ChannelState>(),
            // Preserve the watchlist nicks but reset all to offline: the server will
            // re-deliver 730/731 status replies once MONITOR is re-sent after reconnect.
            MonitoredNicks     = s.MonitoredNicks.Select(m => m with { IsOnline = false }).ToArray(),
        });
    }

    private void OnWelcomeReceived(WelcomeReceived evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        ApplyServerMutation(s => s with { RegisteredNick = evt.Nick });
    }

    // ---------------------------------------------------------------------------
    // ISUPPORT and capabilities
    // ---------------------------------------------------------------------------

    private void OnIsupportTokensReceived(IsupportTokensReceived evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        ApplyServerMutation(s =>
        {
            // Tokens from 005 lines accumulate; later values for the same key win.
            var tokens = new Dictionary<string, string>(s.IsupportTokens, StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in evt.Tokens) tokens[kvp.Key] = kvp.Value;
            return s with { IsupportTokens = tokens };
        });
    }

    private void OnCapabilityNegotiated(CapabilityNegotiated evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        // Replace the entire active set; reconnects may grant a different subset.
        ApplyServerMutation(s => s with
        {
            ActiveCapabilities = new HashSet<string>(evt.Granted, StringComparer.OrdinalIgnoreCase)
        });
    }

    private void OnServerCapabilityChanged(ServerCapabilityChanged evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        ApplyServerMutation(s =>
        {
            var caps = new HashSet<string>(s.ActiveCapabilities, StringComparer.OrdinalIgnoreCase);
            foreach (var cap in evt.Added)   caps.Add(cap);
            foreach (var cap in evt.Removed) caps.Remove(cap);
            return s with { ActiveCapabilities = caps };
        });
    }

    // ---------------------------------------------------------------------------
    // Nick changes
    // ---------------------------------------------------------------------------

    private void OnNickChanged(NickChanged evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        ApplyServerMutation(s =>
        {
            string? newLocalNick =
                string.Equals(s.RegisteredNick, evt.OldNick, StringComparison.OrdinalIgnoreCase)
                    ? evt.NewNick
                    : s.RegisteredNick;

            // Rename the user entry in every channel they appear in.
            var channels = new Dictionary<string, ChannelState>(s.Channels);
            foreach (var (chName, ch) in s.Channels)
            {
                if (!ch.Users.TryGetValue(evt.OldNick, out var user)) continue;
                var users = new Dictionary<string, ChannelUser>(ch.Users);
                users.Remove(evt.OldNick);
                users[evt.NewNick] = user with { Nick = evt.NewNick };
                channels[chName] = ch with { Users = users };
            }

            return s with { RegisteredNick = newLocalNick, Channels = channels };
        });
    }

    // ---------------------------------------------------------------------------
    // Channel membership
    // ---------------------------------------------------------------------------

    private void OnJoinedChannel(JoinedChannel evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        ApplyServerMutation(s =>
        {
            bool isSelf = string.Equals(evt.Nick, s.RegisteredNick, StringComparison.OrdinalIgnoreCase);

            if (isSelf)
            {
                if (s.Channels.ContainsKey(evt.Channel)) return s;
                var newCh = new ChannelState(
                    Name:      evt.Channel,
                    Topic:     null,
                    CreatedAt: null,
                    Modes:     ModeSet.Empty,
                    Users:     new Dictionary<string, ChannelUser>());
                return s with
                {
                    Channels = new Dictionary<string, ChannelState>(s.Channels) { [evt.Channel] = newCh }
                };
            }

            if (!s.Channels.TryGetValue(evt.Channel, out var ch)) return s;
            var user = new ChannelUser(
                Nick:         evt.Nick,
                User:         string.Empty,
                Host:         string.Empty,
                Account:      evt.Account,
                RealName:     null,
                ChannelModes: ModeSet.Empty,
                AwayMessage:  null);
            return s with
            {
                Channels = new Dictionary<string, ChannelState>(s.Channels)
                {
                    [evt.Channel] = ch with
                    {
                        Users = new Dictionary<string, ChannelUser>(ch.Users) { [evt.Nick] = user }
                    }
                }
            };
        });
    }

    private void OnPartedChannel(PartedChannel evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        ApplyServerMutation(s =>
        {
            bool isSelf = string.Equals(evt.Nick, s.RegisteredNick, StringComparison.OrdinalIgnoreCase);

            if (isSelf)
            {
                var channels = new Dictionary<string, ChannelState>(s.Channels);
                channels.Remove(evt.Channel);
                return s with { Channels = channels };
            }

            if (!s.Channels.TryGetValue(evt.Channel, out var ch)) return s;
            var users = new Dictionary<string, ChannelUser>(ch.Users);
            users.Remove(evt.Nick);
            return s with
            {
                Channels = new Dictionary<string, ChannelState>(s.Channels)
                {
                    [evt.Channel] = ch with { Users = users }
                }
            };
        });
    }

    private void OnKickReceived(KickReceived evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        ApplyServerMutation(s =>
        {
            bool selfKicked = string.Equals(
                evt.KickedNick, s.RegisteredNick, StringComparison.OrdinalIgnoreCase);

            if (selfKicked)
            {
                var channels = new Dictionary<string, ChannelState>(s.Channels);
                channels.Remove(evt.Channel);
                return s with { Channels = channels };
            }

            if (!s.Channels.TryGetValue(evt.Channel, out var ch)) return s;
            var users = new Dictionary<string, ChannelUser>(ch.Users);
            users.Remove(evt.KickedNick);
            return s with
            {
                Channels = new Dictionary<string, ChannelState>(s.Channels)
                {
                    [evt.Channel] = ch with { Users = users }
                }
            };
        });
    }

    private void OnUserQuit(UserQuit evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        ApplyServerMutation(s =>
        {
            var channels  = new Dictionary<string, ChannelState>(s.Channels);
            bool modified = false;

            foreach (var (name, ch) in s.Channels)
            {
                if (!ch.Users.ContainsKey(evt.Nick)) continue;
                var users = new Dictionary<string, ChannelUser>(ch.Users);
                users.Remove(evt.Nick);
                channels[name] = ch with { Users = users };
                modified = true;
            }

            return modified ? s with { Channels = channels } : s;
        });
    }

    // ---------------------------------------------------------------------------
    // Topic
    // ---------------------------------------------------------------------------

    private void OnTopicChanged(TopicChanged evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        ApplyServerMutation(s =>
        {
            if (!s.Channels.TryGetValue(evt.Channel, out var ch)) return s;
            // Preserve existing setter/time when not supplied by the TOPIC command.
            var topic = new Topic(
                Text:      evt.NewTopic,
                SetterNick: evt.SetterNick ?? ch.Topic?.SetterNick,
                SetAt:     ch.Topic?.SetAt);
            return s with
            {
                Channels = new Dictionary<string, ChannelState>(s.Channels)
                {
                    [evt.Channel] = ch with { Topic = topic }
                }
            };
        });
    }

    private void OnTopicWhoTime(TopicWhoTime evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        ApplyServerMutation(s =>
        {
            if (!s.Channels.TryGetValue(evt.Channel, out var ch)) return s;
            // Preserve existing topic text; only update setter and timestamp.
            var topic = new Topic(
                Text:      ch.Topic?.Text ?? string.Empty,
                SetterNick: evt.SetterNick,
                SetAt:     evt.SetAt);
            return s with
            {
                Channels = new Dictionary<string, ChannelState>(s.Channels)
                {
                    [evt.Channel] = ch with { Topic = topic }
                }
            };
        });
    }

    private void OnChannelCreated(ChannelCreated evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        ApplyServerMutation(s =>
        {
            if (!s.Channels.TryGetValue(evt.Channel, out var ch)) return s;
            return s with
            {
                Channels = new Dictionary<string, ChannelState>(s.Channels)
                {
                    [evt.Channel] = ch with { CreatedAt = evt.CreatedAt }
                }
            };
        });
    }

    // ---------------------------------------------------------------------------
    // Channel modes
    // ---------------------------------------------------------------------------

    private void OnChannelModeChanged(ChannelModeChanged evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        ApplyServerMutation(s =>
        {
            if (!s.Channels.TryGetValue(evt.Channel, out var ch)) return s;

            var (prefixModes, typeAModes, typeBModes, typeCModes) =
                ParseIsupportModeCategories(s.IsupportTokens);

            var channelFlags  = new HashSet<char>(ch.Modes.Flags);
            var channelParams = new Dictionary<char, string>(ch.Modes.Parameters);
            var users         = new Dictionary<string, ChannelUser>(ch.Users);

            char sign     = '+';
            int  paramIdx = 0;

            foreach (char c in evt.ModeString)
            {
                if (c == '+' || c == '-') { sign = c; continue; }

                bool needsParam =
                    prefixModes.Contains(c)
                    || typeAModes.Contains(c)
                    || typeBModes.Contains(c)
                    || (typeCModes.Contains(c) && sign == '+');

                string? param = needsParam && paramIdx < evt.Params.Count
                    ? evt.Params[paramIdx++]
                    : null;

                if (prefixModes.Contains(c))
                {
                    // Apply user-level prefix mode to the nick given as parameter.
                    if (param is not null && users.TryGetValue(param, out var user))
                    {
                        var flags = new HashSet<char>(user.ChannelModes.Flags);
                        if (sign == '+') flags.Add(c); else flags.Remove(c);
                        users[param] = user with
                        {
                            ChannelModes = new ModeSet(flags, user.ChannelModes.Parameters)
                        };
                    }
                }
                else if (!typeAModes.Contains(c))
                {
                    // Channel-level mode. List modes (type A) are not stored in ChannelState.
                    if (sign == '+')
                    {
                        channelFlags.Add(c);
                        if (param is not null) channelParams[c] = param;
                    }
                    else
                    {
                        channelFlags.Remove(c);
                        channelParams.Remove(c);
                    }
                }
            }

            return s with
            {
                Channels = new Dictionary<string, ChannelState>(s.Channels)
                {
                    [evt.Channel] = ch with
                    {
                        Modes = new ModeSet(channelFlags, channelParams),
                        Users = users,
                    }
                }
            };
        });
    }

    // ---------------------------------------------------------------------------
    // NAMES list
    // ---------------------------------------------------------------------------

    private void OnNamesListReceived(NamesListReceived evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        ApplyServerMutation(s =>
        {
            if (!s.Channels.TryGetValue(evt.Channel, out var ch)) return s;

            var prefixToMode = BuildPrefixToModeMap(s.IsupportTokens);
            var users        = new Dictionary<string, ChannelUser>();

            foreach (var entry in evt.Users)
            {
                // Carry over any user/host/account/realname already known for this nick.
                ch.Users.TryGetValue(entry.Nick, out var existing);

                var modeFlags = new HashSet<char>();
                foreach (char prefix in entry.Prefixes)
                {
                    if (prefixToMode.TryGetValue(prefix, out char modeChar)) modeFlags.Add(modeChar);
                }

                users[entry.Nick] = new ChannelUser(
                    Nick:         entry.Nick,
                    User:         existing?.User ?? string.Empty,
                    Host:         existing?.Host ?? string.Empty,
                    Account:      existing?.Account,
                    RealName:     existing?.RealName,
                    ChannelModes: modeFlags.Count > 0
                        ? new ModeSet(modeFlags, new Dictionary<char, string>())
                        : ModeSet.Empty,
                    AwayMessage:  existing?.AwayMessage);
            }

            return s with
            {
                Channels = new Dictionary<string, ChannelState>(s.Channels)
                {
                    [evt.Channel] = ch with { Users = users }
                }
            };
        });
    }

    // ---------------------------------------------------------------------------
    // User metadata
    // ---------------------------------------------------------------------------

    private void OnWhoReplyEntry(WhoReplyEntry evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        ApplyServerMutation(s => s with
        {
            Channels = UpdateUserInAllChannels(s.Channels, evt.Nick, u => u with
            {
                User     = evt.User,
                Host     = evt.Host,
                Account  = evt.Account ?? u.Account,
                RealName = evt.RealName,
            })
        });
    }

    private void OnWhoIsReply(WhoIsReply evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        ApplyServerMutation(s => s with
        {
            Channels = UpdateUserInAllChannels(s.Channels, evt.Nick, u => u with
            {
                User     = evt.User,
                Host     = evt.Host,
                Account  = evt.Account ?? u.Account,
                RealName = evt.RealName,
            })
        });
    }

    private void OnUserHostChanged(UserHostChanged evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        ApplyServerMutation(s => s with
        {
            Channels = UpdateUserInAllChannels(s.Channels, evt.Nick,
                u => u with { User = evt.NewUser, Host = evt.NewHost })
        });
    }

    private void OnUserAwayChanged(UserAwayChanged evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        string? msg = evt.IsAway ? evt.Message : null;
        ApplyServerMutation(s => s with
        {
            Channels = UpdateUserInAllChannels(s.Channels, evt.Nick,
                u => u with { AwayMessage = msg })
        });
    }

    private void OnUserAccountChanged(UserAccountChanged evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        ApplyServerMutation(s => s with
        {
            Channels = UpdateUserInAllChannels(s.Channels, evt.Nick,
                u => u with { Account = evt.Account })
        });
    }

    private void OnUserRealNameChanged(UserRealNameChanged evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        ApplyServerMutation(s => s with
        {
            Channels = UpdateUserInAllChannels(s.Channels, evt.Nick,
                u => u with { RealName = evt.NewRealName })
        });
    }

    // ---------------------------------------------------------------------------
    // MONITOR
    // ---------------------------------------------------------------------------

    private void OnMonitorStatusChanged(MonitorStatusChanged evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        ApplyServerMutation(s =>
        {
            var list = s.MonitoredNicks.ToList();
            int idx  = list.FindIndex(
                m => string.Equals(m.Nick, evt.Nick, StringComparison.OrdinalIgnoreCase));

            if (idx >= 0) list[idx] = list[idx] with { IsOnline = evt.IsOnline };
            else          list.Add(new MonitoredNick(evt.Nick, evt.IsOnline));

            return s with { MonitoredNicks = list };
        });
    }

    // ---------------------------------------------------------------------------
    // ISUPPORT parsing helpers
    // ---------------------------------------------------------------------------

    // Classify mode chars from ISUPPORT PREFIX and CHANMODES tokens.
    // Returns (prefix modes, type-A list modes, type-B always-param modes, type-C set-only-param modes).
    private static (HashSet<char> prefix, HashSet<char> typeA, HashSet<char> typeB, HashSet<char> typeC)
        ParseIsupportModeCategories(IReadOnlyDictionary<string, string> isupport)
    {
        var prefix = new HashSet<char> { 'o', 'h', 'v' };        // IRC default PREFIX=(ohv)@%+
        var typeA  = new HashSet<char> { 'b', 'e', 'I' };        // ban, exception, invex
        var typeB  = new HashSet<char> { 'k' };                   // key
        var typeC  = new HashSet<char> { 'l' };                   // limit

        if (isupport.TryGetValue("PREFIX", out var prefixToken))
        {
            int close = prefixToken.IndexOf(')');
            if (prefixToken.Length >= 2 && prefixToken[0] == '(' && close >= 0)
            {
                prefix.Clear();
                foreach (char c in prefixToken[1..close]) prefix.Add(c);
            }
        }

        if (isupport.TryGetValue("CHANMODES", out var chanmodes))
        {
            var parts = chanmodes.Split(',');
            if (parts.Length >= 1) { typeA.Clear(); foreach (char c in parts[0]) typeA.Add(c); }
            if (parts.Length >= 2) { typeB.Clear(); foreach (char c in parts[1]) typeB.Add(c); }
            if (parts.Length >= 3) { typeC.Clear(); foreach (char c in parts[2]) typeC.Add(c); }
        }

        return (prefix, typeA, typeB, typeC);
    }

    // Build a prefix-symbol → mode-char map from the PREFIX ISUPPORT token.
    private static Dictionary<char, char> BuildPrefixToModeMap(
        IReadOnlyDictionary<string, string> isupport)
    {
        var map = new Dictionary<char, char> { ['@'] = 'o', ['%'] = 'h', ['+'] = 'v' };

        if (!isupport.TryGetValue("PREFIX", out var prefixToken)) return map;

        int close = prefixToken.IndexOf(')');
        if (prefixToken.Length < 2 || prefixToken[0] != '(' || close < 0) return map;

        string modes    = prefixToken[1..close];
        string prefixes = prefixToken[(close + 1)..];

        map.Clear();
        int count = Math.Min(modes.Length, prefixes.Length);
        for (int i = 0; i < count; i++) map[prefixes[i]] = modes[i];

        return map;
    }
}
