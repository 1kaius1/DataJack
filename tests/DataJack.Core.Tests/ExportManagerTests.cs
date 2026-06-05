// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.Storage.Logs;
using Xunit;

namespace DataJack.Core.Tests;

/// <summary>
/// Tests for <see cref="ExportManager"/>: plain-text and HTML formatting, special-character
/// escaping, empty input, and the ExportToString convenience overload.
/// </summary>
public sealed class ExportManagerTests
{
    private static readonly DateTimeOffset T0 =
        new(2024, 6, 15, 10, 30, 45, TimeSpan.Zero);  // 2024-06-15 10:30:45 UTC

    private static LogEntry Msg(
        string text     = "Hello world",
        string fromNick = "alice",
        LogEntryKind kind = LogEntryKind.Message)
        => new(1, "libera", "#general", fromNick, text, T0, kind);

    // ---------------------------------------------------------------------------
    // Plain text — format correctness
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task PlainText_Empty_ReturnsEmptyString()
    {
        string result = await ExportManager.ExportToStringAsync(Array.Empty<LogEntry>(), ExportFormat.PlainText);
        Assert.Equal("", result.Trim());
    }

    [Fact]
    public async Task PlainText_Message_FormatsCorrectly()
    {
        string result = await ExportManager.ExportToStringAsync(new[] { Msg() }, ExportFormat.PlainText);
        Assert.Contains("[2024-06-15 10:30:45] <alice> Hello world", result);
    }

    [Fact]
    public async Task PlainText_Action_FormatsWithAsterisk()
    {
        string result = await ExportManager.ExportToStringAsync(
            new[] { Msg(text: "waves", kind: LogEntryKind.Action) }, ExportFormat.PlainText);
        Assert.Contains("[2024-06-15 10:30:45] * alice waves", result);
    }

    [Fact]
    public async Task PlainText_Notice_FormatsWithDashes()
    {
        string result = await ExportManager.ExportToStringAsync(
            new[] { Msg(text: "notice text", kind: LogEntryKind.Notice) }, ExportFormat.PlainText);
        Assert.Contains("[2024-06-15 10:30:45] -alice- notice text", result);
    }

    [Fact]
    public async Task PlainText_ServerMessage_FormatsWithTripleAsterisk()
    {
        string result = await ExportManager.ExportToStringAsync(
            new[] { Msg(text: "alice joined #general", kind: LogEntryKind.ServerMessage) }, ExportFormat.PlainText);
        Assert.Contains("[2024-06-15 10:30:45] *** alice joined #general", result);
    }

    [Fact]
    public async Task PlainText_Timestamp_IsUtc()
    {
        // Timestamp stored as UTC 10:30:45; the export must not convert to local time.
        string result = await ExportManager.ExportToStringAsync(new[] { Msg() }, ExportFormat.PlainText);
        Assert.Contains("10:30:45", result);
    }

    [Fact]
    public async Task PlainText_MultipleEntries_EachOnSeparateLine()
    {
        var entries = new[] { Msg("first"), Msg("second"), Msg("third") };
        string result = await ExportManager.ExportToStringAsync(entries, ExportFormat.PlainText);
        string[] lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
    }

    // ---------------------------------------------------------------------------
    // HTML — structure and content
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Html_Empty_ReturnsCompleteDocument()
    {
        string result = await ExportManager.ExportToStringAsync(Array.Empty<LogEntry>(), ExportFormat.Html);
        Assert.Contains("<!DOCTYPE html>", result);
        Assert.Contains("<html", result);
        Assert.Contains("</html>", result);
        Assert.Contains("</body>", result);
    }

    [Fact]
    public async Task Html_ContainsStyleBlock()
    {
        string result = await ExportManager.ExportToStringAsync(Array.Empty<LogEntry>(), ExportFormat.Html);
        Assert.Contains("<style>", result);
    }

    [Fact]
    public async Task Html_Message_ContainsNickAndText()
    {
        string result = await ExportManager.ExportToStringAsync(new[] { Msg() }, ExportFormat.Html);
        Assert.Contains("alice", result);
        Assert.Contains("Hello world", result);
    }

    [Fact]
    public async Task Html_Message_HasTimestamp()
    {
        string result = await ExportManager.ExportToStringAsync(new[] { Msg() }, ExportFormat.Html);
        Assert.Contains("2024-06-15 10:30:45", result);
    }

    [Fact]
    public async Task Html_Action_HasActionClass()
    {
        string result = await ExportManager.ExportToStringAsync(
            new[] { Msg(kind: LogEntryKind.Action) }, ExportFormat.Html);
        Assert.Contains("class=\"line action\"", result);
    }

    [Fact]
    public async Task Html_Notice_HasNoticeClass()
    {
        string result = await ExportManager.ExportToStringAsync(
            new[] { Msg(kind: LogEntryKind.Notice) }, ExportFormat.Html);
        Assert.Contains("class=\"line notice\"", result);
    }

    [Fact]
    public async Task Html_SpecialChars_AreHtmlEncoded()
    {
        var entry = Msg(text: "<script>alert('xss')</script>", fromNick: "bad&actor");
        string result = await ExportManager.ExportToStringAsync(new[] { entry }, ExportFormat.Html);

        // The raw < > & characters must NOT appear in the output unescaped.
        Assert.DoesNotContain("<script>", result);
        Assert.DoesNotContain("bad&actor", result);

        // The encoded forms must be present.
        Assert.Contains("&lt;script&gt;", result);
        Assert.Contains("bad&amp;actor", result);
    }

    [Fact]
    public async Task Html_MultipleEntries_AllPresent()
    {
        var entries = new[]
        {
            Msg("first message"),
            Msg("second message"),
            Msg("third message"),
        };
        string result = await ExportManager.ExportToStringAsync(entries, ExportFormat.Html);
        Assert.Contains("first message",  result);
        Assert.Contains("second message", result);
        Assert.Contains("third message",  result);
    }

    // ---------------------------------------------------------------------------
    // ExportToString vs stream — output consistency
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ExportToString_PlainText_MatchesStreamOutput()
    {
        var entries = new[] { Msg("consistency check") };

        string fromString = await ExportManager.ExportToStringAsync(entries, ExportFormat.PlainText);

        using var ms = new System.IO.MemoryStream();
        await ExportManager.ExportAsync(entries, ms, ExportFormat.PlainText);
        string fromStream = System.Text.Encoding.UTF8.GetString(ms.ToArray());

        Assert.Equal(fromStream, fromString);
    }

    [Fact]
    public async Task ExportToString_Html_MatchesStreamOutput()
    {
        var entries = new[] { Msg("html consistency") };

        string fromString = await ExportManager.ExportToStringAsync(entries, ExportFormat.Html);

        using var ms = new System.IO.MemoryStream();
        await ExportManager.ExportAsync(entries, ms, ExportFormat.Html);
        string fromStream = System.Text.Encoding.UTF8.GetString(ms.ToArray());

        Assert.Equal(fromStream, fromString);
    }
}
