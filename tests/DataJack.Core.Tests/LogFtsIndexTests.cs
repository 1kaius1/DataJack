// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.Storage.Logs;
using Xunit;

namespace DataJack.Core.Tests;

/// <summary>
/// Tests for <see cref="LogFtsIndex"/>: indexing, full-text search, metadata filters,
/// pagination, and edge cases. Each test gets a fresh in-memory SQLite database.
/// </summary>
public sealed class LogFtsIndexTests : IAsyncDisposable
{
    private readonly LogFtsIndex _index;

    // LogFtsIndex internal constructor uses "Data Source=:memory:" — each test class
    // instance gets an isolated, empty in-memory database.
    public LogFtsIndexTests()
    {
        _index = new LogFtsIndex("Data Source=:memory:", true);
    }

    public ValueTask DisposeAsync() => _index.DisposeAsync();

    // Initialize before each test and return the index for fluent chaining.
    private async Task<LogFtsIndex> InitAsync()
    {
        await _index.InitializeAsync();
        return _index;
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static readonly DateTimeOffset T0 = new(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static LogEntry Msg(
        string server    = "libera",
        string target    = "#general",
        string fromNick  = "alice",
        string text      = "Hello world",
        DateTimeOffset? ts = null,
        LogEntryKind kind  = LogEntryKind.Message)
        => new(0, server, target, fromNick, text, ts ?? T0, kind);

    // Index several entries at different timestamps.
    private async Task SeedAsync(params LogEntry[] entries)
    {
        foreach (var e in entries)
            await _index.IndexAsync(e);
    }

    // ---------------------------------------------------------------------------
    // Empty database
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SearchAsync_EmptyDatabase_ReturnsEmpty()
    {
        await InitAsync();
        var result = await _index.SearchAsync(new SearchQuery("anything"));
        Assert.Empty(result.Entries);
        Assert.Equal(0, result.TotalCount);
    }

    // ---------------------------------------------------------------------------
    // Basic indexing + FTS retrieval
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task IndexAsync_SingleEntry_CanSearchByText()
    {
        await InitAsync();
        await SeedAsync(Msg(text: "Hello world"));
        var result = await _index.SearchAsync(new SearchQuery("hello"));
        Assert.Single(result.Entries);
        Assert.Equal("Hello world", result.Entries[0].Text);
    }

    [Fact]
    public async Task SearchAsync_TextQuery_CaseInsensitive()
    {
        await InitAsync();
        await SeedAsync(Msg(text: "Hello World"));
        // FTS5 with unicode61 is case-insensitive by default.
        var result = await _index.SearchAsync(new SearchQuery("HELLO"));
        Assert.Single(result.Entries);
    }

    [Fact]
    public async Task SearchAsync_TextQuery_NoMatch_ReturnsEmpty()
    {
        await InitAsync();
        await SeedAsync(Msg(text: "Hello world"));
        var result = await _index.SearchAsync(new SearchQuery("goodbye"));
        Assert.Empty(result.Entries);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task SearchAsync_FtsPhrase_MatchesPhrase()
    {
        await InitAsync();
        await SeedAsync(
            Msg(text: "hello world"),
            Msg(text: "world hello"));     // order reversed — should NOT match phrase
        // FTS5 phrase query with quotes.
        var result = await _index.SearchAsync(new SearchQuery("\"hello world\""));
        Assert.Single(result.Entries);
        Assert.Equal("hello world", result.Entries[0].Text);
    }

    [Fact]
    public async Task SearchAsync_InvalidFts5Query_ReturnsEmpty()
    {
        await InitAsync();
        await SeedAsync(Msg(text: "hello world"));
        // An unclosed quote is invalid FTS5 syntax.
        var result = await _index.SearchAsync(new SearchQuery("\"unclosed"));
        Assert.Empty(result.Entries);
        Assert.Equal(0, result.TotalCount);
    }

    // ---------------------------------------------------------------------------
    // Empty text — metadata-only mode
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SearchAsync_EmptyText_ReturnsAllEntries()
    {
        await InitAsync();
        await SeedAsync(
            Msg(text: "first"),
            Msg(text: "second"),
            Msg(text: "third"));
        var result = await _index.SearchAsync(new SearchQuery(""));
        Assert.Equal(3, result.TotalCount);
    }

    [Fact]
    public async Task SearchAsync_WhitespaceText_TreatedAsEmpty()
    {
        await InitAsync();
        await SeedAsync(Msg(), Msg());
        var result = await _index.SearchAsync(new SearchQuery("   "));
        Assert.Equal(2, result.TotalCount);
    }

    // ---------------------------------------------------------------------------
    // Nick filter
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SearchAsync_NickFilter_ReturnsMatchingNick()
    {
        await InitAsync();
        await SeedAsync(
            Msg(fromNick: "alice", text: "from alice"),
            Msg(fromNick: "bob",   text: "from bob"));
        var result = await _index.SearchAsync(new SearchQuery("", Nick: "alice"));
        Assert.Single(result.Entries);
        Assert.Equal("alice", result.Entries[0].FromNick);
    }

    [Fact]
    public async Task SearchAsync_NickFilter_CaseInsensitive()
    {
        await InitAsync();
        await SeedAsync(Msg(fromNick: "Alice", text: "message"));
        var result = await _index.SearchAsync(new SearchQuery("", Nick: "ALICE"));
        Assert.Single(result.Entries);
    }

    [Fact]
    public async Task SearchAsync_NickFilter_ExcludesOtherNick()
    {
        await InitAsync();
        await SeedAsync(Msg(fromNick: "bob", text: "from bob"));
        var result = await _index.SearchAsync(new SearchQuery("", Nick: "alice"));
        Assert.Empty(result.Entries);
        Assert.Equal(0, result.TotalCount);
    }

    // ---------------------------------------------------------------------------
    // Server filter
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SearchAsync_ServerFilter_ReturnsMatchingServer()
    {
        await InitAsync();
        await SeedAsync(
            Msg(server: "libera",  text: "from libera"),
            Msg(server: "efnet",   text: "from efnet"));
        var result = await _index.SearchAsync(new SearchQuery("", Server: "libera"));
        Assert.Single(result.Entries);
        Assert.Equal("libera", result.Entries[0].Server);
    }

    [Fact]
    public async Task SearchAsync_ServerFilter_ExcludesOtherServer()
    {
        await InitAsync();
        await SeedAsync(Msg(server: "efnet", text: "efnet message"));
        var result = await _index.SearchAsync(new SearchQuery("", Server: "libera"));
        Assert.Empty(result.Entries);
    }

    // ---------------------------------------------------------------------------
    // Date range filters
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SearchAsync_AfterFilter_ReturnsEntriesAtOrAfter()
    {
        await InitAsync();
        var early = T0;
        var late  = T0.AddHours(2);
        await SeedAsync(
            Msg(ts: early, text: "early message"),
            Msg(ts: late,  text: "late message"));

        var result = await _index.SearchAsync(new SearchQuery("", After: T0.AddHours(1)));
        Assert.Single(result.Entries);
        Assert.Equal("late message", result.Entries[0].Text);
    }

    [Fact]
    public async Task SearchAsync_BeforeFilter_ReturnsEntriesAtOrBefore()
    {
        await InitAsync();
        var early = T0;
        var late  = T0.AddHours(2);
        await SeedAsync(
            Msg(ts: early, text: "early message"),
            Msg(ts: late,  text: "late message"));

        var result = await _index.SearchAsync(new SearchQuery("", Before: T0.AddHours(1)));
        Assert.Single(result.Entries);
        Assert.Equal("early message", result.Entries[0].Text);
    }

    [Fact]
    public async Task SearchAsync_DateRange_ReturnsWithinRange()
    {
        await InitAsync();
        await SeedAsync(
            Msg(ts: T0,              text: "before"),
            Msg(ts: T0.AddHours(1),  text: "inside"),
            Msg(ts: T0.AddHours(3),  text: "after"));

        var result = await _index.SearchAsync(new SearchQuery("",
            After:  T0.AddMinutes(30),
            Before: T0.AddHours(2)));
        Assert.Single(result.Entries);
        Assert.Equal("inside", result.Entries[0].Text);
    }

    // ---------------------------------------------------------------------------
    // Combined filters
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SearchAsync_TextAndNickFilter_Combined()
    {
        await InitAsync();
        await SeedAsync(
            Msg(fromNick: "alice", text: "urgent matter here"),
            Msg(fromNick: "bob",   text: "urgent matter here"),
            Msg(fromNick: "alice", text: "routine check"));

        var result = await _index.SearchAsync(new SearchQuery("urgent", Nick: "alice"));
        Assert.Single(result.Entries);
        Assert.Equal("alice", result.Entries[0].FromNick);
    }

    [Fact]
    public async Task SearchAsync_TextAndServerFilter_Combined()
    {
        await InitAsync();
        await SeedAsync(
            Msg(server: "libera", text: "hello libera"),
            Msg(server: "efnet",  text: "hello efnet"));

        var result = await _index.SearchAsync(new SearchQuery("hello", Server: "libera"));
        Assert.Single(result.Entries);
        Assert.Equal("libera", result.Entries[0].Server);
    }

    // ---------------------------------------------------------------------------
    // TotalCount and ordering
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SearchAsync_TotalCount_ReflectsAllMatchingEntries()
    {
        await InitAsync();
        for (int i = 0; i < 5; i++)
            await _index.IndexAsync(Msg(text: $"message {i}"));
        await _index.IndexAsync(Msg(text: "unrelated"));

        var result = await _index.SearchAsync(new SearchQuery("message"), pageSize: 2);
        Assert.Equal(5, result.TotalCount);
        Assert.Equal(2, result.Entries.Count);   // first page
    }

    [Fact]
    public async Task SearchAsync_EmptyText_OrderedByTimestampDesc()
    {
        await InitAsync();
        await SeedAsync(
            Msg(ts: T0,             text: "oldest"),
            Msg(ts: T0.AddHours(2), text: "newest"),
            Msg(ts: T0.AddHours(1), text: "middle"));

        var result = await _index.SearchAsync(new SearchQuery(""));
        Assert.Equal("newest", result.Entries[0].Text);
        Assert.Equal("middle", result.Entries[1].Text);
        Assert.Equal("oldest", result.Entries[2].Text);
    }

    // ---------------------------------------------------------------------------
    // Pagination
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Pagination_Page0_ContainsFirstEntries()
    {
        await InitAsync();
        for (int i = 0; i < 5; i++)
            await _index.IndexAsync(Msg(text: $"item {i}", ts: T0.AddSeconds(i)));

        var result = await _index.SearchAsync(new SearchQuery(""), page: 0, pageSize: 3);
        Assert.Equal(3, result.Entries.Count);
        Assert.Equal(5, result.TotalCount);
    }

    [Fact]
    public async Task Pagination_Page1_ContainsRemainingEntries()
    {
        await InitAsync();
        for (int i = 0; i < 5; i++)
            await _index.IndexAsync(Msg(text: $"item {i}", ts: T0.AddSeconds(i)));

        var result = await _index.SearchAsync(new SearchQuery(""), page: 1, pageSize: 3);
        Assert.Equal(2, result.Entries.Count);   // 5 total, 3 on page 0, 2 on page 1
    }

    [Fact]
    public async Task Pagination_HasMore_TrueWhenMorePages()
    {
        await InitAsync();
        for (int i = 0; i < 5; i++)
            await _index.IndexAsync(Msg(text: $"item {i}"));

        var result = await _index.SearchAsync(new SearchQuery(""), page: 0, pageSize: 3);
        Assert.True(result.HasMore);
    }

    [Fact]
    public async Task Pagination_HasMore_FalseOnLastPage()
    {
        await InitAsync();
        for (int i = 0; i < 5; i++)
            await _index.IndexAsync(Msg(text: $"item {i}"));

        var result = await _index.SearchAsync(new SearchQuery(""), page: 1, pageSize: 3);
        Assert.False(result.HasMore);
    }

    // ---------------------------------------------------------------------------
    // Action entries
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task IndexAsync_ActionEntry_IsSearchableByText()
    {
        await InitAsync();
        await SeedAsync(Msg(text: "waves hello", kind: LogEntryKind.Action));
        var result = await _index.SearchAsync(new SearchQuery("waves"));
        Assert.Single(result.Entries);
        Assert.Equal(LogEntryKind.Action, result.Entries[0].Kind);
    }

    // ---------------------------------------------------------------------------
    // Returned entry fidelity
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task IndexAsync_ReturnedEntry_HasAssignedId()
    {
        await InitAsync();
        var indexed = await _index.IndexAsync(Msg());
        Assert.NotEqual(0, indexed.Id);
    }

    [Fact]
    public async Task SearchAsync_EntryFields_PreservedRoundTrip()
    {
        await InitAsync();
        var original = new LogEntry(0, "libera", "#test", "charlie",
            "roundtrip integrity check", T0, LogEntryKind.Notice);
        await _index.IndexAsync(original);

        var result = await _index.SearchAsync(new SearchQuery("roundtrip"));
        var e = Assert.Single(result.Entries);
        Assert.Equal("libera",         e.Server);
        Assert.Equal("#test",          e.Target);
        Assert.Equal("charlie",        e.FromNick);
        Assert.Equal("roundtrip integrity check", e.Text);
        Assert.Equal(T0.ToUnixTimeSeconds(), e.Timestamp.ToUnixTimeSeconds());
        Assert.Equal(LogEntryKind.Notice, e.Kind);
    }
}
