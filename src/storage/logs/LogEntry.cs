// SPDX-License-Identifier: GPL-3.0-or-later
// Shared log entry type for the FTS5 search index. See ARCHITECTURE.md §12.3.

namespace DataJack.Core.Storage.Logs;

/// <summary>The kind of IRC message recorded in a log entry.</summary>
public enum LogEntryKind
{
    /// <summary>A PRIVMSG from a user.</summary>
    Message,
    /// <summary>A CTCP ACTION (/me).</summary>
    Action,
    /// <summary>A NOTICE from a user or server.</summary>
    Notice,
    /// <summary>A server-generated status line (join, part, quit, mode change, etc.).</summary>
    ServerMessage,
}

/// <summary>
/// An immutable record of one IRC message as stored in the FTS5 search index.
/// <see cref="Id"/> is 0 for newly created entries; it is populated after indexing.
/// </summary>
public sealed record LogEntry(
    /// <summary>Row ID assigned by SQLite; 0 for unsaved entries.</summary>
    long           Id,
    /// <summary>Server identifier (network name or address).</summary>
    string         Server,
    /// <summary>Buffer identifier: channel name or query nick.</summary>
    string         Target,
    /// <summary>Nick of the message sender, or a server name for server messages.</summary>
    string         FromNick,
    /// <summary>Message text.</summary>
    string         Text,
    /// <summary>When the message was received (UTC).</summary>
    DateTimeOffset Timestamp,
    /// <summary>Message kind for display and icon selection.</summary>
    LogEntryKind   Kind);
