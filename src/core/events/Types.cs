// SPDX-License-Identifier: GPL-3.0-or-later
// This file defines the complete event vocabulary for the DataJack event bus.
// All cross-component communication flows through these types; see ARCHITECTURE.md §5.2.
// Phase 0 defines the connection, message, and error categories.
// Remaining categories (registration, channel, user, DCC) are populated in Phase 1.

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
