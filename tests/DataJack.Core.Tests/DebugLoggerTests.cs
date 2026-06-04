// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.Events;
using DataJack.Core.Irc;
using Xunit;

namespace DataJack.Core.Tests;

public sealed class DebugLoggerTests : IDisposable
{
    private readonly string _tempDir;

    public DebugLoggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"datajack-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ---------------------------------------------------------------------------
    // Redact (pure unit tests — no file I/O or dispatcher needed)
    // ---------------------------------------------------------------------------

    [Fact]
    public void Redact_PassCommand_IsRedacted()
    {
        Assert.Equal("PASS :<redacted>", DebugLogger.Redact("PASS :s3cr3t"));
    }

    [Fact]
    public void Redact_PassCommandCaseInsensitive_IsRedacted()
    {
        Assert.Equal("PASS :<redacted>", DebugLogger.Redact("pass :s3cr3t"));
    }

    [Fact]
    public void Redact_AuthenticateWithToken_IsRedacted()
    {
        Assert.Equal("AUTHENTICATE <redacted>",
            DebugLogger.Redact("AUTHENTICATE dXNlcjpwYXNzd29yZA=="));
    }

    [Fact]
    public void Redact_AuthenticatePlus_IsNotRedacted()
    {
        // AUTHENTICATE + is the "empty response" continuation — not a credential.
        Assert.Equal("AUTHENTICATE +", DebugLogger.Redact("AUTHENTICATE +"));
    }

    [Fact]
    public void Redact_NickCommand_IsUnchanged()
    {
        Assert.Equal("NICK Foo", DebugLogger.Redact("NICK Foo"));
    }

    [Fact]
    public void Redact_NormalMessage_IsUnchanged()
    {
        Assert.Equal("PRIVMSG #chan :hello", DebugLogger.Redact("PRIVMSG #chan :hello"));
    }

    // ---------------------------------------------------------------------------
    // Integration tests — real dispatcher and file I/O
    // ---------------------------------------------------------------------------

    private async Task<(EventDispatcher dispatcher, DebugLogger logger, string path)>
        CreateAsync()
    {
        string path = Path.Combine(_tempDir, $"debug-{Guid.NewGuid():N}.log");
        var dispatcher = new EventDispatcher();
        dispatcher.Start();
        var logger = new DebugLogger(path, dispatcher);
        await Task.Yield(); // allow the Start task to begin
        return (dispatcher, logger, path);
    }

    [Fact]
    public async Task Constructor_WritesOpenHeader()
    {
        var (dispatcher, logger, path) = await CreateAsync();
        logger.Dispose();
        await dispatcher.DisposeAsync();

        string content = await File.ReadAllTextAsync(path);
        Assert.Contains("DataJack debug log opened", content);
    }

    [Fact]
    public async Task Dispose_WritesCloseHeader()
    {
        var (dispatcher, logger, path) = await CreateAsync();
        logger.Dispose();
        await dispatcher.DisposeAsync();

        string content = await File.ReadAllTextAsync(path);
        Assert.Contains("DataJack debug log closed", content);
    }

    [Fact]
    public async Task RawLineSent_IsWrittenToFile()
    {
        var (dispatcher, logger, path) = await CreateAsync();

        await dispatcher.PublishAsync(new RawLineSent("irc.test", "NICK Foo"));
        await Task.Delay(50);

        logger.Dispose();
        await dispatcher.DisposeAsync();

        string content = await File.ReadAllTextAsync(path);
        Assert.Contains(">> [irc.test] NICK Foo", content);
    }

    [Fact]
    public async Task RawLineSent_PassLine_IsRedactedInFile()
    {
        var (dispatcher, logger, path) = await CreateAsync();

        await dispatcher.PublishAsync(new RawLineSent("irc.test", "PASS :topsecret"));
        await Task.Delay(50);

        logger.Dispose();
        await dispatcher.DisposeAsync();

        string content = await File.ReadAllTextAsync(path);
        Assert.Contains("PASS :<redacted>", content);
        Assert.DoesNotContain("topsecret", content);
    }

    [Fact]
    public async Task RawLineReceived_IsWrittenToFile()
    {
        var (dispatcher, logger, path) = await CreateAsync();

        await dispatcher.PublishAsync(new RawLineReceived("irc.test", ":server 001 Foo :Welcome"));
        await Task.Delay(50);

        logger.Dispose();
        await dispatcher.DisposeAsync();

        string content = await File.ReadAllTextAsync(path);
        Assert.Contains("<< [irc.test] :server 001 Foo :Welcome", content);
    }

    [Fact]
    public async Task ConnectionAttempted_IsWrittenToFile()
    {
        var (dispatcher, logger, path) = await CreateAsync();

        await dispatcher.PublishAsync(
            new ConnectionAttempted("irc.test", "irc.libera.chat", 6697, Tls: true));
        await Task.Delay(50);

        logger.Dispose();
        await dispatcher.DisposeAsync();

        string content = await File.ReadAllTextAsync(path);
        Assert.Contains("[CONNECTING]", content);
        Assert.Contains("irc.libera.chat:6697", content);
        Assert.Contains("(TLS)", content);
    }

    [Fact]
    public async Task ConnectionEstablished_IsWrittenToFile()
    {
        var (dispatcher, logger, path) = await CreateAsync();

        await dispatcher.PublishAsync(new ConnectionEstablished("irc.test"));
        await Task.Delay(50);

        logger.Dispose();
        await dispatcher.DisposeAsync();

        string content = await File.ReadAllTextAsync(path);
        Assert.Contains("[CONNECTED]", content);
    }

    [Fact]
    public async Task ConnectionFailed_IsWrittenToFile()
    {
        var (dispatcher, logger, path) = await CreateAsync();

        await dispatcher.PublishAsync(new ConnectionFailed("irc.test", "Connection refused"));
        await Task.Delay(50);

        logger.Dispose();
        await dispatcher.DisposeAsync();

        string content = await File.ReadAllTextAsync(path);
        Assert.Contains("[FAILED]", content);
        Assert.Contains("Connection refused", content);
    }

    [Fact]
    public async Task ConnectionClosed_IsWrittenToFile()
    {
        var (dispatcher, logger, path) = await CreateAsync();

        await dispatcher.PublishAsync(new ConnectionClosed("irc.test", "Remote host closed"));
        await Task.Delay(50);

        logger.Dispose();
        await dispatcher.DisposeAsync();

        string content = await File.ReadAllTextAsync(path);
        Assert.Contains("[CLOSED]", content);
        Assert.Contains("Remote host closed", content);
    }

    [Fact]
    public async Task Dispose_StopsLogging_NoFurtherLinesWritten()
    {
        var (dispatcher, logger, path) = await CreateAsync();

        // Confirm logging is active.
        await dispatcher.PublishAsync(new RawLineSent("irc.test", "NICK Before"));
        await Task.Delay(50);

        logger.Dispose();

        // Lines published after Dispose should not appear.
        await dispatcher.PublishAsync(new RawLineSent("irc.test", "NICK After"));
        await Task.Delay(50);

        await dispatcher.DisposeAsync();

        string content = await File.ReadAllTextAsync(path);
        Assert.Contains("NICK Before", content);
        Assert.DoesNotContain("NICK After", content);
    }
}
