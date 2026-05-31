// SPDX-License-Identifier: GPL-3.0-or-later
// Complete event vocabulary for the DataJack event bus. See ARCHITECTURE.md §5.2.
// New events must be defined here before being emitted anywhere in the codebase.

namespace DataJack.Core.Events;

// ---------------------------------------------------------------------------
// Connection events
// ---------------------------------------------------------------------------

/// <summary>A TCP/TLS connection attempt is about to begin.</summary>
public readonly record struct ConnectionAttempted(
    string Server,
    string Address,
    int Port,
    bool Tls);

/// <summary>TCP/TLS handshake completed; registration has not yet begun.</summary>
public readonly record struct ConnectionEstablished(string Server);

/// <summary>The connection was dropped, either cleanly or by error.</summary>
public readonly record struct ConnectionClosed(string Server, string? Reason);

/// <summary>The connection attempt failed before a session was established.</summary>
public readonly record struct ConnectionFailed(string Server, string Reason);

/// <summary>A reconnection attempt is scheduled after a disconnect.</summary>
public readonly record struct ReconnectScheduled(string Server, double DelaySeconds, int AttemptNumber);

/// <summary>Reconnection succeeded; lists the channels that were rejoined.</summary>
public readonly record struct ReconnectSucceeded(string Server, IReadOnlyList<string> RejoinedChannels);

/// <summary>All reconnection attempts were exhausted without success.</summary>
public readonly record struct ReconnectFailed(string Server, string Reason);

/// <summary>A raw line arrived from the server before any parsing.</summary>
public readonly record struct RawLineReceived(string Server, string Line);

/// <summary>A raw line was sent to the server.</summary>
public readonly record struct RawLineSent(string Server, string Line);

// ---------------------------------------------------------------------------
// Message events
// ---------------------------------------------------------------------------

/// <summary>
/// A PRIVMSG arrived.
/// Tags is null when the server sent no IRCv3 message tags.
/// IsSelf is true when echo-message is active and the message originated locally.
/// </summary>
public readonly record struct MessageReceived(
    string Server,
    string Target,
    string FromNick,
    string Text,
    IReadOnlyDictionary<string, string>? Tags,
    bool IsSelf);

/// <summary>A NOTICE arrived directed at a channel or nick.</summary>
public readonly record struct NoticeReceived(
    string Server,
    string Target,
    string FromNick,
    string Text,
    IReadOnlyDictionary<string, string>? Tags);

/// <summary>A server-originated NOTICE (no nick prefix).</summary>
public readonly record struct ServerNoticeReceived(string Server, string Text);

/// <summary>A CTCP request arrived.</summary>
public readonly record struct CtcpRequest(string Server, string FromNick, string Command, string? Params);

/// <summary>A CTCP reply arrived.</summary>
public readonly record struct CtcpReply(string Server, string FromNick, string Command, string? Params);

/// <summary>A PRIVMSG ACTION (/me) arrived.</summary>
public readonly record struct ActionReceived(
    string Server,
    string Target,
    string FromNick,
    string Text,
    IReadOnlyDictionary<string, string>? Tags);

/// <summary>A WALLOPS message arrived.</summary>
public readonly record struct WallopsReceived(string Server, string FromNick, string Text);

// ---------------------------------------------------------------------------
// Registration events
// ---------------------------------------------------------------------------

/// <summary>The server accepted registration and assigned a nick (numeric 001).</summary>
public readonly record struct WelcomeReceived(string Server, string Nick);

/// <summary>One line of the server's Message of the Day (numeric 372).</summary>
public readonly record struct MOTDReceived(string Server, string Text);

/// <summary>The server finished sending the MOTD (numeric 376).</summary>
public readonly record struct MOTDEnd(string Server);

/// <summary>CAP negotiation completed; lists capabilities the server granted and denied.</summary>
public readonly record struct CapabilityNegotiated(
    string Server,
    IReadOnlyList<string> Granted,
    IReadOnlyList<string> Denied);

/// <summary>The server added or removed capabilities at runtime (cap-notify).</summary>
public readonly record struct ServerCapabilityChanged(
    string Server,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed);

/// <summary>SASL authentication has begun with the given mechanism.</summary>
public readonly record struct SASLStarted(string Server, string Mechanism);

/// <summary>SASL authentication succeeded (numeric 900/903).</summary>
public readonly record struct SASLSucceeded(string Server);

/// <summary>SASL authentication failed; the connection may be dropped depending on config.</summary>
public readonly record struct SASLFailed(string Server, string Reason);

// ---------------------------------------------------------------------------
// Channel events
// ---------------------------------------------------------------------------

/// <summary>
/// A JOIN was processed. Account is populated when the extended-join capability is active.
/// </summary>
public readonly record struct JoinedChannel(
    string Server,
    string Channel,
    string Nick,
    string? Account);

/// <summary>A PART was processed.</summary>
public readonly record struct PartedChannel(
    string Server,
    string Channel,
    string Nick,
    string? Reason);

/// <summary>A KICK was processed.</summary>
public readonly record struct KickReceived(
    string Server,
    string Channel,
    string KickedNick,
    string KickerNick,
    string? Reason);

/// <summary>A TOPIC change was processed.</summary>
public readonly record struct TopicChanged(
    string Server,
    string Channel,
    string NewTopic,
    string SetterNick);

/// <summary>An INVITE arrived for the local user.</summary>
public readonly record struct InviteReceived(string Server, string Channel, string FromNick);

// ---------------------------------------------------------------------------
// User events
// ---------------------------------------------------------------------------

/// <summary>A nick change was processed.</summary>
public readonly record struct NickChanged(string Server, string OldNick, string NewNick);

/// <summary>The requested nick is already in use (numeric 433).</summary>
public readonly record struct NickInUse(string Server, string Nick);

/// <summary>A QUIT was processed.</summary>
public readonly record struct UserQuit(string Server, string Nick, string? Reason);

// ---------------------------------------------------------------------------
// Error and warning events
// ---------------------------------------------------------------------------

/// <summary>The server sent an ERROR message; the connection will close.</summary>
public readonly record struct ErrorReceived(string Server, string Message);

/// <summary>
/// A byte sequence could not be decoded with the configured encoding.
/// The invalid bytes were replaced with U+FFFD before display.
/// </summary>
public readonly record struct EncodingWarning(
    string Server,
    ReadOnlyMemory<byte> RawBytes,
    string AppliedEncoding);

/// <summary>The outbound flood queue exceeded its capacity and messages were dropped.</summary>
public readonly record struct FloodQueueFull(string Server, int DroppedCount);

/// <summary>A script handler invocation was dropped because the script queue was full.</summary>
public readonly record struct ScriptInvocationDropped(string ScriptName, string EventType);
