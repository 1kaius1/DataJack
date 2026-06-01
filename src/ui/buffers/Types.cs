// SPDX-License-Identifier: GPL-3.0-or-later
// Buffer type definitions. See ARCHITECTURE.md §6.1.
// Every displayable surface is a buffer. Buffers are pure data objects -- no Avalonia
// dependency -- so they are testable without a display.

namespace DataJack.Ui.Buffers;

// ---------------------------------------------------------------------------
// Enumerations
// ---------------------------------------------------------------------------

/// <summary>The structural kind of a buffer. Controls which UI panels are shown.</summary>
public enum BufferKind
{
    /// <summary>Aggregated network status window (shown at the top level).</summary>
    NetworkStatus,
    /// <summary>Per-server status window: connection messages, MOTD.</summary>
    ServerStatus,
    /// <summary>A joined IRC channel.</summary>
    Channel,
    /// <summary>A private message conversation (query).</summary>
    Query,
    /// <summary>A DCC CHAT session.</summary>
    DccChat,
    /// <summary>Aggregated server notices.</summary>
    Notices,
    /// <summary>Raw protocol line log for debugging.</summary>
    RawLog,
    /// <summary>Aggregated view of all messages that matched a highlight pattern.</summary>
    Highlights,
}

/// <summary>The semantic kind of a message line displayed in a buffer.</summary>
public enum MessageKind
{
    Normal,
    Action,
    Notice,
    ServerNotice,
    Join,
    Part,
    Quit,
    Kick,
    NickChange,
    Topic,
    Mode,
    Motd,
    Info,
    Error,
    RawLine,
}

// ---------------------------------------------------------------------------
// Message entry
// ---------------------------------------------------------------------------

/// <summary>
/// One displayable message line stored in an <see cref="IBuffer"/>.
/// Timestamp uses server-time when the server-time IRCv3 capability is active.
/// </summary>
public readonly record struct MessageEntry(
    DateTimeOffset                       Timestamp,
    string?                              Nick,
    MessageKind                          Kind,
    string                               Text,
    IReadOnlyDictionary<string, string>? Tags);

// ---------------------------------------------------------------------------
// Buffer interface and base class
// ---------------------------------------------------------------------------

/// <summary>A displayable chat surface. All buffer types implement this interface.</summary>
public interface IBuffer
{
    /// <summary>Structural kind; determines which UI panels are shown.</summary>
    BufferKind Kind { get; }

    /// <summary>Stable unique ID for this buffer (server + target pair).</summary>
    string Id { get; }

    /// <summary>Human-readable label for the tab strip.</summary>
    string Title { get; }

    /// <summary>Server identifier this buffer belongs to.</summary>
    string Server { get; }

    /// <summary>Whether this buffer has unread messages.</summary>
    bool HasUnread { get; }

    /// <summary>All messages currently held in memory.</summary>
    IReadOnlyList<MessageEntry> Messages { get; }

    /// <summary>Append a message and raise <see cref="MessageAdded"/>.</summary>
    void AddMessage(MessageEntry message);

    /// <summary>Mark all current messages as read.</summary>
    void MarkRead();

    /// <summary>Raised on the thread that called <see cref="AddMessage"/>.</summary>
    event Action<MessageEntry> MessageAdded;
}

/// <summary>Base implementation shared by all concrete buffer types.</summary>
public abstract class BufferBase : IBuffer
{
    private readonly List<MessageEntry> _messages = new();

    public abstract BufferKind Kind { get; }
    public string Id { get; }
    public string Title { get; protected set; }
    public string Server { get; }
    public bool HasUnread { get; private set; }

    public IReadOnlyList<MessageEntry> Messages => _messages;

    public event Action<MessageEntry>? MessageAdded;

    protected BufferBase(string id, string title, string server)
    {
        Id     = id;
        Title  = title;
        Server = server;
    }

    public void AddMessage(MessageEntry message)
    {
        _messages.Add(message);
        HasUnread = true;
        MessageAdded?.Invoke(message);
    }

    public void MarkRead() => HasUnread = false;
}

// ---------------------------------------------------------------------------
// Concrete buffer types
// ---------------------------------------------------------------------------

/// <summary>Aggregated network status buffer (one per application).</summary>
public sealed class NetworkStatusBuffer : BufferBase
{
    public const string FixedId = "::network";
    public override BufferKind Kind => BufferKind.NetworkStatus;

    public NetworkStatusBuffer() : base(FixedId, "DataJack", string.Empty) { }
}

/// <summary>Per-server status window showing connection events and MOTD.</summary>
public sealed class ServerStatusBuffer : BufferBase
{
    public override BufferKind Kind => BufferKind.ServerStatus;

    public ServerStatusBuffer(string server)
        : base($"{server}::status", server, server) { }
}

/// <summary>A joined IRC channel buffer.</summary>
public sealed class ChannelBuffer : BufferBase
{
    public override BufferKind Kind => BufferKind.Channel;

    /// <summary>The channel name (e.g. "#datajack").</summary>
    public string Channel { get; }

    /// <summary>Current channel topic, or null if no topic is set.</summary>
    public string? Topic { get; set; }

    /// <summary>Nicks currently in the channel with their mode prefixes.</summary>
    public List<ChannelMember> Members { get; } = new();

    public ChannelBuffer(string server, string channel)
        : base($"{server}::{channel}", channel, server)
    {
        Channel = channel;
    }
}

/// <summary>A user in a channel with their highest mode prefix.</summary>
public readonly record struct ChannelMember(
    string Nick,
    /// <summary>All mode prefix characters the user holds (e.g. "@+").</summary>
    string Prefixes)
{
    /// <summary>The highest-ranked prefix character, or '\0' if none.</summary>
    public char HighestPrefix => Prefixes.Length > 0 ? Prefixes[0] : '\0';
}

/// <summary>A private message conversation with another user.</summary>
public sealed class QueryBuffer : BufferBase
{
    public override BufferKind Kind => BufferKind.Query;

    /// <summary>The nick this query is with.</summary>
    public string TargetNick { get; }

    public QueryBuffer(string server, string nick)
        : base($"{server}::query::{nick}", nick, server)
    {
        TargetNick = nick;
    }
}

/// <summary>Aggregated server notices buffer.</summary>
public sealed class NoticesBuffer : BufferBase
{
    public override BufferKind Kind => BufferKind.Notices;

    public NoticesBuffer(string server)
        : base($"{server}::notices", "Notices", server) { }
}

/// <summary>Raw IRC protocol line display buffer.</summary>
public sealed class RawLogBuffer : BufferBase
{
    public override BufferKind Kind => BufferKind.RawLog;

    public RawLogBuffer(string server)
        : base($"{server}::rawlog", "Raw Log", server) { }
}

/// <summary>Aggregated highlights buffer (one per application).</summary>
public sealed class HighlightsBuffer : BufferBase
{
    public const string FixedId = "::highlights";
    public override BufferKind Kind => BufferKind.Highlights;

    public HighlightsBuffer() : base(FixedId, "Highlights", string.Empty) { }
}
