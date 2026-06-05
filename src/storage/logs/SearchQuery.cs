// SPDX-License-Identifier: GPL-3.0-or-later
// Search query and result types for the FTS5 log index. See ARCHITECTURE.md §12.3.

namespace DataJack.Core.Storage.Logs;

/// <summary>
/// Parameters for a log search. All filter fields are optional; omitted fields impose no
/// constraint. <see cref="Text"/> is passed directly to SQLite FTS5 and may use FTS5
/// query syntax (phrase searches, exclusions, etc.). An empty or whitespace text skips
/// the FTS5 step and applies only the metadata filters.
/// </summary>
public sealed record SearchQuery(
    /// <summary>FTS5 query string (searched in message text and from-nick). Empty means "match all".</summary>
    string          Text,
    /// <summary>When non-null, restrict results to this sender nick (case-insensitive).</summary>
    string?         Nick   = null,
    /// <summary>When non-null, restrict results to this server identifier.</summary>
    string?         Server = null,
    /// <summary>When non-null, restrict results to messages at or after this time.</summary>
    DateTimeOffset? After  = null,
    /// <summary>When non-null, restrict results to messages at or before this time.</summary>
    DateTimeOffset? Before = null);

/// <summary>One page of search results returned by <see cref="LogFtsIndex.SearchAsync"/>.</summary>
public sealed record SearchResultPage(
    /// <summary>Entries on this page, ordered by relevance then timestamp descending.</summary>
    IReadOnlyList<LogEntry> Entries,
    /// <summary>Total number of matching entries across all pages.</summary>
    long                    TotalCount,
    /// <summary>Zero-based page index.</summary>
    int                     Page,
    /// <summary>Maximum entries per page.</summary>
    int                     PageSize)
{
    /// <summary>True when there are more entries beyond this page.</summary>
    public bool HasMore => (long)(Page + 1) * PageSize < TotalCount;
}
