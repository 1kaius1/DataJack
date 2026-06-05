// SPDX-License-Identifier: GPL-3.0-or-later
// BufferManager: creates and destroys buffers in response to IRC events, and routes
// incoming messages to the correct buffer. See ARCHITECTURE.md §6.4 BufferManager.
//
// Threading: Subscribe handlers are called on the event dispatch thread.
// BufferCreated / BufferDestroyed / MessageAdded are raised on that same thread;
// callers that update Avalonia controls must marshal to the UI thread themselves.

using DataJack.Core.Events;

namespace DataJack.Ui.Buffers;

/// <summary>
/// Owns the ordered list of open buffers and routes IRC events to the correct buffer.
/// One instance lives for the lifetime of the application.
/// </summary>
public sealed class BufferManager : IDisposable
{
    private readonly Core.Events.EventDispatcher _dispatcher;
    private readonly List<IBuffer> _buffers = new();
    private bool _disposed;

    // ---------------------------------------------------------------------------
    // Events
    // ---------------------------------------------------------------------------

    /// <summary>A new buffer was opened.</summary>
    public event Action<IBuffer>? BufferCreated;

    /// <summary>A buffer was closed and removed from the list.</summary>
    public event Action<IBuffer>? BufferDestroyed;

    /// <summary>A message was added to a buffer.</summary>
    public event Action<IBuffer, MessageEntry>? MessageAdded;

    // ---------------------------------------------------------------------------
    // Public state
    // ---------------------------------------------------------------------------

    /// <summary>All currently open buffers in display order.</summary>
    public IReadOnlyList<IBuffer> Buffers => _buffers;

    // ---------------------------------------------------------------------------
    // Construction
    // ---------------------------------------------------------------------------

    public BufferManager(Core.Events.EventDispatcher dispatcher)
    {
        _dispatcher = dispatcher;

        // Create the two singletons that always exist.
        AddBuffer(new NetworkStatusBuffer());
        AddBuffer(new HighlightsBuffer());

        // Subscribe to the IRC events that drive buffer creation and message routing.
        _dispatcher.Subscribe<ConnectionEstablished>(OnConnectionEstablished);
        _dispatcher.Subscribe<ConnectionClosed>(OnConnectionClosed);
        _dispatcher.Subscribe<ConnectionFailed>(OnConnectionFailed);
        _dispatcher.Subscribe<ReconnectScheduled>(OnReconnectScheduled);
        _dispatcher.Subscribe<ReconnectSucceeded>(OnReconnectSucceeded);
        _dispatcher.Subscribe<ReconnectFailed>(OnReconnectFailed);
        _dispatcher.Subscribe<WelcomeReceived>(OnWelcomeReceived);
        _dispatcher.Subscribe<MOTDReceived>(OnMotdReceived);
        _dispatcher.Subscribe<MOTDEnd>(OnMotdEnd);
        _dispatcher.Subscribe<SASLStarted>(OnSaslStarted);
        _dispatcher.Subscribe<SASLSucceeded>(OnSaslSucceeded);
        _dispatcher.Subscribe<SASLFailed>(OnSaslFailed);
        _dispatcher.Subscribe<JoinedChannel>(OnJoinedChannel);
        _dispatcher.Subscribe<PartedChannel>(OnPartedChannel);
        _dispatcher.Subscribe<KickReceived>(OnKickReceived);
        _dispatcher.Subscribe<NamesListReceived>(OnNamesListReceived);
        _dispatcher.Subscribe<ChannelModeChanged>(OnChannelModeChanged);
        _dispatcher.Subscribe<UserModeChanged>(OnUserModeChanged);
        _dispatcher.Subscribe<TopicChanged>(OnTopicChanged);
        _dispatcher.Subscribe<InviteReceived>(OnInviteReceived);
        _dispatcher.Subscribe<MessageReceived>(OnMessageReceived);
        _dispatcher.Subscribe<ActionReceived>(OnActionReceived);
        _dispatcher.Subscribe<NoticeReceived>(OnNoticeReceived);
        _dispatcher.Subscribe<ServerNoticeReceived>(OnServerNoticeReceived);
        _dispatcher.Subscribe<WallopsReceived>(OnWallopsReceived);
        _dispatcher.Subscribe<CtcpRequest>(OnCtcpRequest);
        _dispatcher.Subscribe<CtcpReply>(OnCtcpReply);
        _dispatcher.Subscribe<NickChanged>(OnNickChanged);
        _dispatcher.Subscribe<NickInUse>(OnNickInUse);
        _dispatcher.Subscribe<UserQuit>(OnUserQuit);
        _dispatcher.Subscribe<WhoIsReply>(OnWhoIsReply);
        _dispatcher.Subscribe<WhoIsEnd>(OnWhoIsEnd);
        _dispatcher.Subscribe<WhoReplyEntry>(OnWhoReplyEntry);
        _dispatcher.Subscribe<WhoEnd>(OnWhoEnd);
        _dispatcher.Subscribe<ChannelListEntry>(OnChannelListEntry);
        _dispatcher.Subscribe<ChannelListEnd>(OnChannelListEnd);
        _dispatcher.Subscribe<BanListEntry>(OnBanListEntry);
        _dispatcher.Subscribe<BanListEnd>(OnBanListEnd);
        _dispatcher.Subscribe<PrivilegeError>(OnPrivilegeError);
        _dispatcher.Subscribe<RawLineReceived>(OnRawLineReceived);
        _dispatcher.Subscribe<RawLineSent>(OnRawLineSent);
        _dispatcher.Subscribe<ErrorReceived>(OnErrorReceived);
    }

    // ---------------------------------------------------------------------------
    // Buffer management helpers
    // ---------------------------------------------------------------------------

    private T AddBuffer<T>(T buffer) where T : IBuffer
    {
        _buffers.Add(buffer);
        buffer.MessageAdded += msg => MessageAdded?.Invoke(buffer, msg);
        BufferCreated?.Invoke(buffer);
        return buffer;
    }

    private void RemoveBuffer(IBuffer buffer)
    {
        _buffers.Remove(buffer);
        BufferDestroyed?.Invoke(buffer);
    }

    private ServerStatusBuffer GetOrCreateServerStatus(string server)
    {
        var existing = _buffers.OfType<ServerStatusBuffer>().FirstOrDefault(b => b.Server == server);
        if (existing is not null) return existing;
        return AddBuffer(new ServerStatusBuffer(server));
    }

    private ChannelBuffer? GetChannel(string server, string channel) =>
        _buffers.OfType<ChannelBuffer>().FirstOrDefault(
            b => b.Server == server && b.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase));

    private ChannelBuffer GetOrCreateChannel(string server, string channel)
    {
        var existing = GetChannel(server, channel);
        if (existing is not null) return existing;
        return AddBuffer(new ChannelBuffer(server, channel));
    }

    private QueryBuffer GetOrCreateQuery(string server, string nick)
    {
        var existing = _buffers.OfType<QueryBuffer>().FirstOrDefault(
            b => b.Server == server && b.TargetNick.Equals(nick, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        return AddBuffer(new QueryBuffer(server, nick));
    }

    private IBuffer? GetOrCreateTarget(string server, string target)
    {
        if (target.Length > 0 && (target[0] == '#' || target[0] == '&'
                                   || target[0] == '+' || target[0] == '!'))
            return GetOrCreateChannel(server, target);
        return GetOrCreateQuery(server, target);
    }

    private static MessageEntry Now(MessageKind kind, string text, string? nick = null,
        IReadOnlyDictionary<string, string>? tags = null)
    {
        DateTimeOffset ts = tags is not null && tags.TryGetValue("time", out var t)
            && DateTimeOffset.TryParse(t, out var parsed) ? parsed : DateTimeOffset.Now;
        return new MessageEntry(ts, nick, kind, text, tags);
    }

    // ---------------------------------------------------------------------------
    // Connection events
    // ---------------------------------------------------------------------------

    private void OnConnectionEstablished(ConnectionEstablished e)
    {
        var buf = GetOrCreateServerStatus(e.Server);
        buf.AddMessage(Now(MessageKind.Info, $"Connecting to {e.Server}..."));
        AddBuffer(new RawLogBuffer(e.Server));
    }

    private void OnConnectionClosed(ConnectionClosed e)
    {
        var buf = GetOrCreateServerStatus(e.Server);
        string reason = e.Reason is null ? string.Empty : $": {e.Reason}";
        buf.AddMessage(Now(MessageKind.Error, $"Disconnected{reason}"));
    }

    private void OnConnectionFailed(ConnectionFailed e)
    {
        GetOrCreateServerStatus(e.Server)
            .AddMessage(Now(MessageKind.Error, $"Connection failed: {e.Reason}"));
    }

    private void OnReconnectScheduled(ReconnectScheduled e)
    {
        GetOrCreateServerStatus(e.Server)
            .AddMessage(Now(MessageKind.Info,
                $"Reconnect attempt {e.AttemptNumber} in {e.DelaySeconds:F1}s..."));
    }

    private void OnReconnectSucceeded(ReconnectSucceeded e)
    {
        GetOrCreateServerStatus(e.Server)
            .AddMessage(Now(MessageKind.Info, "Reconnected."));
    }

    private void OnReconnectFailed(ReconnectFailed e)
    {
        GetOrCreateServerStatus(e.Server)
            .AddMessage(Now(MessageKind.Error, $"Reconnect failed: {e.Reason}"));
    }

    // ---------------------------------------------------------------------------
    // Registration events
    // ---------------------------------------------------------------------------

    private void OnWelcomeReceived(WelcomeReceived e)
    {
        var buf = GetOrCreateServerStatus(e.Server);
        buf.AddMessage(Now(MessageKind.Info, $"Connected as {e.Nick}"));
    }

    private void OnMotdReceived(MOTDReceived e)
    {
        var buf = GetOrCreateServerStatus(e.Server);
        buf.AddMessage(Now(MessageKind.Motd, e.Text));
    }

    private void OnMotdEnd(MOTDEnd e)
    {
        GetOrCreateServerStatus(e.Server);
    }

    private void OnSaslStarted(SASLStarted e)
    {
        GetOrCreateServerStatus(e.Server)
            .AddMessage(Now(MessageKind.Info, $"SASL: authenticating with {e.Mechanism}"));
    }

    private void OnSaslSucceeded(SASLSucceeded e)
    {
        GetOrCreateServerStatus(e.Server)
            .AddMessage(Now(MessageKind.Info, "SASL: authentication successful"));
    }

    private void OnSaslFailed(SASLFailed e)
    {
        GetOrCreateServerStatus(e.Server)
            .AddMessage(Now(MessageKind.Error, $"SASL: authentication failed: {e.Reason}"));
    }

    // ---------------------------------------------------------------------------
    // Channel events
    // ---------------------------------------------------------------------------

    private void OnJoinedChannel(JoinedChannel e)
    {
        var buf = GetOrCreateChannel(e.Server, e.Channel);
        buf.AddMessage(Now(MessageKind.Join, $"{e.Nick} has joined {e.Channel}"));
        if (!buf.Members.Any(m => m.Nick.Equals(e.Nick, StringComparison.OrdinalIgnoreCase)))
            buf.Members.Add(new ChannelMember(e.Nick, string.Empty));
    }

    private void OnPartedChannel(PartedChannel e)
    {
        var buf = GetChannel(e.Server, e.Channel);
        if (buf is null) return;
        string reason = e.Reason is null ? string.Empty : $" ({e.Reason})";
        buf.AddMessage(Now(MessageKind.Part, $"{e.Nick} has left {e.Channel}{reason}"));
        buf.Members.RemoveAll(m => m.Nick.Equals(e.Nick, StringComparison.OrdinalIgnoreCase));
    }

    private void OnKickReceived(KickReceived e)
    {
        var buf = GetChannel(e.Server, e.Channel);
        if (buf is null) return;
        string reason = e.Reason is null ? string.Empty : $" ({e.Reason})";
        buf.AddMessage(Now(MessageKind.Kick,
            $"{e.KickedNick} was kicked from {e.Channel} by {e.KickerNick}{reason}"));
        buf.Members.RemoveAll(m => m.Nick.Equals(e.KickedNick, StringComparison.OrdinalIgnoreCase));
    }

    // NAMES list replaces the entire member list for the channel. No message is added
    // since this fires on every join and would be noisy; nicklist updates silently.
    private void OnNamesListReceived(NamesListReceived e)
    {
        var buf = GetChannel(e.Server, e.Channel);
        if (buf is null) return;
        buf.Members.Clear();
        foreach (var entry in e.Users)
            buf.Members.Add(new ChannelMember(entry.Nick, new string(entry.Prefixes.ToArray())));
    }

    private void OnChannelModeChanged(ChannelModeChanged e)
    {
        var buf = GetChannel(e.Server, e.Channel);
        if (buf is null) return;
        string paramStr = e.Params.Count > 0 ? " " + string.Join(' ', e.Params) : string.Empty;
        buf.AddMessage(Now(MessageKind.Mode,
            $"{e.SetterNick} sets mode {e.ModeString}{paramStr} on {e.Channel}"));
    }

    private void OnUserModeChanged(UserModeChanged e)
    {
        GetOrCreateServerStatus(e.Server)
            .AddMessage(Now(MessageKind.Mode, $"Mode {e.ModeString} set on {e.Nick}"));
    }

    private void OnTopicChanged(TopicChanged e)
    {
        var buf = GetChannel(e.Server, e.Channel);
        if (buf is null) return;
        buf.Topic = e.NewTopic;
        buf.AddMessage(Now(MessageKind.Topic,
            $"{e.SetterNick} changed the topic to: {e.NewTopic}"));
    }

    private void OnInviteReceived(InviteReceived e)
    {
        GetOrCreateServerStatus(e.Server)
            .AddMessage(Now(MessageKind.Notice,
                $"{e.FromNick} has invited you to {e.Channel}"));
    }

    // ---------------------------------------------------------------------------
    // Message events
    // ---------------------------------------------------------------------------

    private void OnMessageReceived(MessageReceived e)
    {
        var buf = GetOrCreateTarget(e.Server, e.Target);
        buf?.AddMessage(Now(MessageKind.Normal, e.Text, e.FromNick, e.Tags));
    }

    private void OnActionReceived(ActionReceived e)
    {
        var buf = GetOrCreateTarget(e.Server, e.Target);
        buf?.AddMessage(Now(MessageKind.Action, e.Text, e.FromNick, e.Tags));
    }

    private void OnNoticeReceived(NoticeReceived e)
    {
        // Route to the target buffer if it already exists, otherwise to server status.
        var buf = _buffers.FirstOrDefault(b => b.Id == $"{e.Server}::{e.Target}")
            ?? (IBuffer)GetOrCreateServerStatus(e.Server);
        buf.AddMessage(Now(MessageKind.Notice, e.Text, e.FromNick, e.Tags));
    }

    private void OnServerNoticeReceived(ServerNoticeReceived e)
    {
        GetOrCreateServerStatus(e.Server)
            .AddMessage(Now(MessageKind.ServerNotice, e.Text));
    }

    private void OnWallopsReceived(WallopsReceived e)
    {
        GetOrCreateServerStatus(e.Server)
            .AddMessage(Now(MessageKind.Notice, $"WALLOPS from {e.FromNick}: {e.Text}"));
    }

    private void OnCtcpRequest(CtcpRequest e)
    {
        string detail = e.Params is not null ? $": {e.Params}" : string.Empty;
        GetOrCreateServerStatus(e.Server)
            .AddMessage(Now(MessageKind.Notice,
                $"CTCP {e.Command} request from {e.FromNick}{detail}"));
    }

    private void OnCtcpReply(CtcpReply e)
    {
        string detail = e.Params is not null ? $": {e.Params}" : string.Empty;
        GetOrCreateServerStatus(e.Server)
            .AddMessage(Now(MessageKind.Notice,
                $"CTCP {e.Command} reply from {e.FromNick}{detail}"));
    }

    // ---------------------------------------------------------------------------
    // User events
    // ---------------------------------------------------------------------------

    private void OnNickChanged(NickChanged e)
    {
        bool inAnyChannel = false;
        foreach (var ch in _buffers.OfType<ChannelBuffer>().Where(b => b.Server == e.Server))
        {
            var member = ch.Members.FirstOrDefault(m =>
                m.Nick.Equals(e.OldNick, StringComparison.OrdinalIgnoreCase));
            if (member.Nick is null) continue;

            inAnyChannel = true;
            int idx = ch.Members.IndexOf(member);
            ch.Members[idx] = member with { Nick = e.NewNick };
            ch.AddMessage(Now(MessageKind.NickChange, $"{e.OldNick} is now known as {e.NewNick}"));
        }
        // Show in server status when not in any channel (e.g. /nick at connection time).
        if (!inAnyChannel)
            GetOrCreateServerStatus(e.Server)
                .AddMessage(Now(MessageKind.NickChange, $"{e.OldNick} is now known as {e.NewNick}"));
    }

    private void OnNickInUse(NickInUse e)
    {
        GetOrCreateServerStatus(e.Server)
            .AddMessage(Now(MessageKind.Error, $"Nickname '{e.Nick}' is already in use."));
    }

    private void OnUserQuit(UserQuit e)
    {
        string reason = e.Reason is null ? string.Empty : $" ({e.Reason})";
        foreach (var ch in _buffers.OfType<ChannelBuffer>().Where(b => b.Server == e.Server))
        {
            bool inChannel = ch.Members.Any(m =>
                m.Nick.Equals(e.Nick, StringComparison.OrdinalIgnoreCase));
            if (!inChannel) continue;

            ch.Members.RemoveAll(m => m.Nick.Equals(e.Nick, StringComparison.OrdinalIgnoreCase));
            ch.AddMessage(Now(MessageKind.Quit, $"{e.Nick} has quit{reason}"));
        }
    }

    // ---------------------------------------------------------------------------
    // WHOIS / WHO
    // ---------------------------------------------------------------------------

    private void OnWhoIsReply(WhoIsReply e)
    {
        var buf = GetOrCreateServerStatus(e.Server);
        buf.AddMessage(Now(MessageKind.Info, $"[{e.Nick}] {e.Nick}!{e.User}@{e.Host} ({e.RealName})"));
        buf.AddMessage(Now(MessageKind.Info, $"[{e.Nick}] Server: {e.ServerName}"));
        if (e.Account is not null)
            buf.AddMessage(Now(MessageKind.Info, $"[{e.Nick}] Account: {e.Account}"));
        if (e.IdleSeconds > 0)
        {
            var ts = TimeSpan.FromSeconds(e.IdleSeconds);
            buf.AddMessage(Now(MessageKind.Info,
                $"[{e.Nick}] Idle: {ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"));
        }
    }

    private void OnWhoIsEnd(WhoIsEnd e)
    {
        GetOrCreateServerStatus(e.Server)
            .AddMessage(Now(MessageKind.Info, $"[{e.Nick}] End of WHOIS"));
    }

    private void OnWhoReplyEntry(WhoReplyEntry e)
    {
        string channel = e.Channel ?? "*";
        string account = e.Account is not null ? $" ({e.Account})" : string.Empty;
        GetOrCreateServerStatus(e.Server)
            .AddMessage(Now(MessageKind.Info,
                $"{e.Nick} ({e.User}@{e.Host}){account} [{channel}] {e.RealName}"));
    }

    private void OnWhoEnd(WhoEnd e)
    {
        GetOrCreateServerStatus(e.Server)
            .AddMessage(Now(MessageKind.Info, $"End of /WHO {e.Target}"));
    }

    // ---------------------------------------------------------------------------
    // Channel list (/list)
    // ---------------------------------------------------------------------------

    private void OnChannelListEntry(ChannelListEntry e)
    {
        GetOrCreateServerStatus(e.Server)
            .AddMessage(Now(MessageKind.Info, $"  {e.Channel} ({e.UserCount}) {e.Topic}"));
    }

    private void OnChannelListEnd(ChannelListEnd e)
    {
        GetOrCreateServerStatus(e.Server)
            .AddMessage(Now(MessageKind.Info, "End of /LIST"));
    }

    // ---------------------------------------------------------------------------
    // Ban list
    // ---------------------------------------------------------------------------

    private void OnBanListEntry(BanListEntry e)
    {
        GetOrCreateServerStatus(e.Server)
            .AddMessage(Now(MessageKind.Info,
                $"Ban: {e.Channel} {e.Mask} (set by {e.Setter} on {e.SetAt:yyyy-MM-dd HH:mm:ss})"));
    }

    private void OnBanListEnd(BanListEnd e)
    {
        GetOrCreateServerStatus(e.Server)
            .AddMessage(Now(MessageKind.Info, $"End of ban list for {e.Channel}"));
    }

    // ---------------------------------------------------------------------------
    // Error events
    // ---------------------------------------------------------------------------

    private void OnPrivilegeError(PrivilegeError e)
    {
        GetOrCreateServerStatus(e.Server)
            .AddMessage(Now(MessageKind.Error, $"{e.Command}: {e.Reason}"));
    }

    // ---------------------------------------------------------------------------
    // Raw log
    // ---------------------------------------------------------------------------

    private void OnRawLineReceived(RawLineReceived e)
    {
        var rawLog = _buffers.OfType<RawLogBuffer>().FirstOrDefault(b => b.Server == e.Server);
        rawLog?.AddMessage(Now(MessageKind.RawLine, $"<< {e.Line}"));
    }

    private void OnRawLineSent(RawLineSent e)
    {
        var rawLog = _buffers.OfType<RawLogBuffer>().FirstOrDefault(b => b.Server == e.Server);
        rawLog?.AddMessage(Now(MessageKind.RawLine, $">> {e.Line}"));
    }

    private void OnErrorReceived(ErrorReceived e)
    {
        GetOrCreateServerStatus(e.Server)
            .AddMessage(Now(MessageKind.Error, $"ERROR: {e.Message}"));
    }

    // ---------------------------------------------------------------------------
    // Disposal
    // ---------------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _dispatcher.Unsubscribe<ConnectionEstablished>(OnConnectionEstablished);
        _dispatcher.Unsubscribe<ConnectionClosed>(OnConnectionClosed);
        _dispatcher.Unsubscribe<ConnectionFailed>(OnConnectionFailed);
        _dispatcher.Unsubscribe<ReconnectScheduled>(OnReconnectScheduled);
        _dispatcher.Unsubscribe<ReconnectSucceeded>(OnReconnectSucceeded);
        _dispatcher.Unsubscribe<ReconnectFailed>(OnReconnectFailed);
        _dispatcher.Unsubscribe<WelcomeReceived>(OnWelcomeReceived);
        _dispatcher.Unsubscribe<MOTDReceived>(OnMotdReceived);
        _dispatcher.Unsubscribe<MOTDEnd>(OnMotdEnd);
        _dispatcher.Unsubscribe<SASLStarted>(OnSaslStarted);
        _dispatcher.Unsubscribe<SASLSucceeded>(OnSaslSucceeded);
        _dispatcher.Unsubscribe<SASLFailed>(OnSaslFailed);
        _dispatcher.Unsubscribe<JoinedChannel>(OnJoinedChannel);
        _dispatcher.Unsubscribe<PartedChannel>(OnPartedChannel);
        _dispatcher.Unsubscribe<KickReceived>(OnKickReceived);
        _dispatcher.Unsubscribe<NamesListReceived>(OnNamesListReceived);
        _dispatcher.Unsubscribe<ChannelModeChanged>(OnChannelModeChanged);
        _dispatcher.Unsubscribe<UserModeChanged>(OnUserModeChanged);
        _dispatcher.Unsubscribe<TopicChanged>(OnTopicChanged);
        _dispatcher.Unsubscribe<InviteReceived>(OnInviteReceived);
        _dispatcher.Unsubscribe<MessageReceived>(OnMessageReceived);
        _dispatcher.Unsubscribe<ActionReceived>(OnActionReceived);
        _dispatcher.Unsubscribe<NoticeReceived>(OnNoticeReceived);
        _dispatcher.Unsubscribe<ServerNoticeReceived>(OnServerNoticeReceived);
        _dispatcher.Unsubscribe<WallopsReceived>(OnWallopsReceived);
        _dispatcher.Unsubscribe<CtcpRequest>(OnCtcpRequest);
        _dispatcher.Unsubscribe<CtcpReply>(OnCtcpReply);
        _dispatcher.Unsubscribe<NickChanged>(OnNickChanged);
        _dispatcher.Unsubscribe<NickInUse>(OnNickInUse);
        _dispatcher.Unsubscribe<UserQuit>(OnUserQuit);
        _dispatcher.Unsubscribe<WhoIsReply>(OnWhoIsReply);
        _dispatcher.Unsubscribe<WhoIsEnd>(OnWhoIsEnd);
        _dispatcher.Unsubscribe<WhoReplyEntry>(OnWhoReplyEntry);
        _dispatcher.Unsubscribe<WhoEnd>(OnWhoEnd);
        _dispatcher.Unsubscribe<ChannelListEntry>(OnChannelListEntry);
        _dispatcher.Unsubscribe<ChannelListEnd>(OnChannelListEnd);
        _dispatcher.Unsubscribe<BanListEntry>(OnBanListEntry);
        _dispatcher.Unsubscribe<BanListEnd>(OnBanListEnd);
        _dispatcher.Unsubscribe<PrivilegeError>(OnPrivilegeError);
        _dispatcher.Unsubscribe<RawLineReceived>(OnRawLineReceived);
        _dispatcher.Unsubscribe<RawLineSent>(OnRawLineSent);
        _dispatcher.Unsubscribe<ErrorReceived>(OnErrorReceived);
    }
}
