// SPDX-License-Identifier: GPL-3.0-or-later
// FTS5-backed log search index. See ARCHITECTURE.md §12.3.
//
// Schema: a single standalone FTS5 virtual table `log_messages`.
//   - from_nick, text  — FTS-indexed (searchable via MATCH)
//   - server, target, ts, kind — UNINDEXED (stored but not FTS-indexed;
//     used for metadata-only filtering and returned in SELECT results)
//
// When SearchQuery.Text is non-empty the FTS5 MATCH clause narrows candidates;
// metadata filters are then applied as additional SQL predicates.
// When Text is empty the FTS step is skipped and only metadata filters run
// (full table scan — acceptable for a local log search tool at Phase 3 scale).
//
// Threading:
//   SQLite connections are not thread-safe. Callers must serialize all
//   operations on a single LogFtsIndex instance.

using Microsoft.Data.Sqlite;

namespace DataJack.Core.Storage.Logs;

/// <summary>
/// Maintains a SQLite FTS5 search index over IRC log entries and provides paginated,
/// filtered full-text search. Supports per-nick, per-server, and date-range filters
/// combined with full-text queries using standard FTS5 query syntax.
/// </summary>
public sealed class LogFtsIndex : IAsyncDisposable
{
    private readonly string            _connectionString;
    private          SqliteConnection? _connection;

    /// <param name="dbPath">Absolute path to the SQLite database file.</param>
    public LogFtsIndex(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _connectionString = $"Data Source={dbPath}";
    }

    // Used by tests: in-memory SQLite database; the test project has no direct
    // Microsoft.Data.Sqlite reference, so this constructor hides the connection string.
    internal LogFtsIndex(string connectionString, bool _isConnectionString)
    {
        _connectionString = connectionString;
    }

    // ---------------------------------------------------------------------------
    // Initialisation
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Create the <c>log_messages</c> FTS5 virtual table if it does not already exist.
    /// Must be called once before any other operation.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var db = await GetConnectionAsync(ct).ConfigureAwait(false);

        // Single FTS5 virtual table. from_nick and text are indexed for full-text search.
        // server, target, ts, kind are UNINDEXED: stored but not tokenized.
        await RunAsync(db, """
            CREATE VIRTUAL TABLE IF NOT EXISTS log_messages USING fts5(
                from_nick,
                text,
                server  UNINDEXED,
                target  UNINDEXED,
                ts      UNINDEXED,
                kind    UNINDEXED,
                tokenize='unicode61'
            )
            """, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------
    // Indexing
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Insert a log entry into the index.
    /// Returns the entry with <see cref="LogEntry.Id"/> set to the assigned rowid.
    /// </summary>
    public async Task<LogEntry> IndexAsync(LogEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var db = await GetConnectionAsync(ct).ConfigureAwait(false);

        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO log_messages (from_nick, text, server, target, ts, kind)
            VALUES (@fromNick, @text, @server, @target, @ts, @kind)
            """;
        cmd.Parameters.AddWithValue("@fromNick", entry.FromNick);
        cmd.Parameters.AddWithValue("@text",     entry.Text);
        cmd.Parameters.AddWithValue("@server",   entry.Server);
        cmd.Parameters.AddWithValue("@target",   entry.Target);
        cmd.Parameters.AddWithValue("@ts",       entry.Timestamp.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("@kind",     entry.Kind.ToString());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        using var rowCmd = db.CreateCommand();
        rowCmd.CommandText = "SELECT last_insert_rowid()";
        long id = (long)(await rowCmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;

        return entry with { Id = id };
    }

    // ---------------------------------------------------------------------------
    // Search
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Execute a search and return one page of results.
    /// </summary>
    /// <param name="query">Search parameters. See <see cref="SearchQuery"/>.</param>
    /// <param name="page">Zero-based page index.</param>
    /// <param name="pageSize">Maximum entries per page (default 50).</param>
    public async Task<SearchResultPage> SearchAsync(
        SearchQuery       query,
        int               page     = 0,
        int               pageSize = 50,
        CancellationToken ct       = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        var  db     = await GetConnectionAsync(ct).ConfigureAwait(false);
        bool hasFts = !string.IsNullOrWhiteSpace(query.Text);
        string where = BuildWhere(hasFts);

        long totalCount;
        try
        {
            using var countCmd = db.CreateCommand();
            countCmd.CommandText = $"SELECT COUNT(*) FROM log_messages WHERE {where}";
            Bind(countCmd, query, hasFts);
            totalCount = (long)(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }
        catch (SqliteException)
        {
            // Invalid FTS5 query syntax: return empty result rather than surfacing the error.
            return new SearchResultPage(Array.Empty<LogEntry>(), 0, page, pageSize);
        }

        var entries = new List<LogEntry>();
        using var dataCmd = db.CreateCommand();
        // FTS5 rank column is available only when a MATCH clause is present.
        string order = hasFts ? "rank, ts DESC" : "ts DESC";
        dataCmd.CommandText = $"""
            SELECT rowid, server, target, from_nick, text, ts, kind
            FROM log_messages
            WHERE {where}
            ORDER BY {order}
            LIMIT @pageSize OFFSET @offset
            """;
        Bind(dataCmd, query, hasFts);
        dataCmd.Parameters.AddWithValue("@pageSize", pageSize);
        dataCmd.Parameters.AddWithValue("@offset",   (long)page * pageSize);

        using var reader = await dataCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            entries.Add(ReadEntry(reader));

        return new SearchResultPage(entries, totalCount, page, pageSize);
    }

    // ---------------------------------------------------------------------------
    // Disposal
    // ---------------------------------------------------------------------------

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync().ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------
    // Private helpers
    // ---------------------------------------------------------------------------

    private async Task<SqliteConnection> GetConnectionAsync(CancellationToken ct)
    {
        if (_connection is null)
        {
            _connection = new SqliteConnection(_connectionString);
            await _connection.OpenAsync(ct).ConfigureAwait(false);
        }
        return _connection;
    }

    private static Task RunAsync(SqliteConnection db, string sql, CancellationToken ct)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteNonQueryAsync(ct);
    }

    // Build the WHERE clause. When hasFts is true, includes an FTS5 MATCH condition
    // using the full table name log_messages (aliases are not valid for MATCH).
    private static string BuildWhere(bool hasFts)
    {
        var parts = new List<string>(5);
        if (hasFts) parts.Add("log_messages MATCH @ftsQuery");
        parts.Add("(@server IS NULL OR server    = @server)");
        parts.Add("(@nick   IS NULL OR from_nick = @nick COLLATE NOCASE)");
        parts.Add("(@after  IS NULL OR ts >= @after)");
        parts.Add("(@before IS NULL OR ts <= @before)");
        return string.Join(" AND ", parts);
    }

    private static void Bind(SqliteCommand cmd, SearchQuery q, bool hasFts)
    {
        if (hasFts)
            cmd.Parameters.AddWithValue("@ftsQuery", q.Text.Trim());

        cmd.Parameters.AddWithValue("@server", (object?)q.Server ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@nick",   (object?)q.Nick   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@after",
            q.After  is { } a ? (object)a.ToUnixTimeSeconds()  : DBNull.Value);
        cmd.Parameters.AddWithValue("@before",
            q.Before is { } b ? (object)b.ToUnixTimeSeconds()  : DBNull.Value);
    }

    // Column order must match the SELECT list: rowid, server, target, from_nick, text, ts, kind
    private static LogEntry ReadEntry(SqliteDataReader r) =>
        new(
            Id:        r.GetInt64(0),
            Server:    r.GetString(1),
            Target:    r.GetString(2),
            FromNick:  r.GetString(3),
            Text:      r.GetString(4),
            Timestamp: DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(5)),
            Kind:      Enum.Parse<LogEntryKind>(r.GetString(6)));
}
