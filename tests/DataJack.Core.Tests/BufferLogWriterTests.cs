// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.Storage.Logs;
using Xunit;

namespace DataJack.Core.Tests;

public sealed class BufferLogWriterTests : IAsyncDisposable
{
    private readonly string _tempDir;

    public BufferLogWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"datajack_log_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ---------------------------------------------------------------------------
    // GetLogPath
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task GetLogPath_ReturnsPathUnderLogDirectory()
    {
        await using var writer = new BufferLogWriter(_tempDir);
        var date = new DateOnly(2025, 1, 15);
        string path = writer.GetLogPath("libera", "#datajack", date);

        Assert.StartsWith(_tempDir, path);
        Assert.Contains("2025-01-15", path);
        Assert.EndsWith(".log", path);
    }

    [Fact]
    public async Task GetLogPath_SeparatesServerAndBuffer()
    {
        await using var writer = new BufferLogWriter(_tempDir);
        var date = new DateOnly(2025, 6, 1);
        string path1 = writer.GetLogPath("libera", "#ch1", date);
        string path2 = writer.GetLogPath("libera", "#ch2", date);
        string path3 = writer.GetLogPath("freenode", "#ch1", date);

        Assert.NotEqual(path1, path2);
        Assert.NotEqual(path1, path3);
    }

    [Fact]
    public async Task GetLogPath_SlashIsReplaced()
    {
        // '/' is the only guaranteed-invalid filename character on Linux/macOS/Windows.
        // '\' and ':' are invalid on Windows but legal on Linux, so we only assert the
        // cross-platform guarantee.
        await using var writer = new BufferLogWriter(_tempDir);
        var date = new DateOnly(2025, 1, 1);
        string path = writer.GetLogPath("server", "#chan/sub", date);

        string fileName = Path.GetFileName(path);
        Assert.DoesNotContain('/', fileName);
    }

    // ---------------------------------------------------------------------------
    // Log + flush
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Log_WritesLineToFile()
    {
        // Use an explicit writer that we dispose once; 'await using' would double-dispose.
        var writer = new BufferLogWriter(_tempDir);
        var ts = new DateTimeOffset(2025, 3, 10, 12, 0, 0, TimeSpan.Zero);
        writer.Log("libera", "#test", ts, "alice", "Normal", "hello world");
        await writer.DisposeAsync();
        // No assertion beyond confirming no exception is thrown.
    }

    [Fact]
    public async Task Log_MultipleEntries_AllPersisted()
    {
        // Use a separate writer scoped entirely within this test.
        var writer2 = new BufferLogWriter(_tempDir);
        var ts = DateTimeOffset.UtcNow;

        for (int i = 0; i < 10; i++)
            writer2.Log("net", "#ch", ts.AddSeconds(i), "nick", "Normal", $"message {i}");

        await writer2.DisposeAsync();

        var date = DateOnly.FromDateTime(ts.LocalDateTime);
        string path = writer2.GetLogPath("net", "#ch", date);

        Assert.True(File.Exists(path));
        string[] lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(10, lines.Length);
    }

    [Fact]
    public async Task Log_TabDelimitedFormat_FourFields()
    {
        var writer3 = new BufferLogWriter(_tempDir);
        var ts = new DateTimeOffset(2025, 4, 1, 8, 0, 0, TimeSpan.Zero);

        writer3.Log("net", "#ch2", ts, "bob", "Normal", "test message");
        await writer3.DisposeAsync();

        string path = writer3.GetLogPath("net", "#ch2", DateOnly.FromDateTime(ts.LocalDateTime));
        string line = (await File.ReadAllLinesAsync(path))[0];

        string[] fields = line.Split('\t');
        Assert.Equal(4, fields.Length);
        Assert.Equal("bob", fields[1]);
        Assert.Equal("Normal", fields[2]);
        Assert.Equal("test message", fields[3]);
    }

    [Fact]
    public async Task Log_NullNick_WritesHyphen()
    {
        var writer4 = new BufferLogWriter(_tempDir);
        var ts = new DateTimeOffset(2025, 5, 5, 9, 0, 0, TimeSpan.Zero);

        writer4.Log("net", "#ch3", ts, null, "Info", "server message");
        await writer4.DisposeAsync();

        string path = writer4.GetLogPath("net", "#ch3", DateOnly.FromDateTime(ts.LocalDateTime));
        string line = (await File.ReadAllLinesAsync(path))[0];
        string[] fields = line.Split('\t');

        Assert.Equal("-", fields[1]);
    }

    [Fact]
    public async Task Log_AppendMode_DoesNotTruncateExistingFile()
    {
        // Write a first batch.
        var writer5 = new BufferLogWriter(_tempDir);
        var ts = new DateTimeOffset(2025, 6, 15, 10, 0, 0, TimeSpan.Zero);
        writer5.Log("net", "#ch4", ts, "a", "Normal", "first");
        await writer5.DisposeAsync();

        // Write a second batch to the same file.
        var writer6 = new BufferLogWriter(_tempDir);
        writer6.Log("net", "#ch4", ts, "b", "Normal", "second");
        await writer6.DisposeAsync();

        string path = writer5.GetLogPath("net", "#ch4", DateOnly.FromDateTime(ts.LocalDateTime));
        string[] lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(2, lines.Length);
    }
}
