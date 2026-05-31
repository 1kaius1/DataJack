// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.Events;
using DataJack.Core.Irc;
using DataJack.Net;
using Xunit;

namespace DataJack.Core.Tests;

/// <summary>
/// Tests for IRCCommandRouter. Each test creates a real IRCConnection backed by a
/// DuplexPipeStream so that we can read the exact line that was sent to the "server".
/// </summary>
public sealed class CommandRouterTests : IAsyncDisposable
{
    private readonly EventDispatcher _dispatcher = new();
    private readonly DuplexPipeStream _stream = new();
    private readonly IRCConnection _connection;
    private readonly IRCCommandRouter _router;
    private static readonly NetworkEndpoint FakeEndpoint = new("h", 6667, UseTls: false);

    public CommandRouterTests()
    {
        _dispatcher.Start();
        _connection = new IRCConnection("s", new FakeNetworkProvider(_stream), _dispatcher);
        _router = new IRCCommandRouter(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        _stream.Dispose();
        await _dispatcher.DisposeAsync();
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private async Task ConnectAsync()
    {
        await _connection.ConnectAsync(FakeEndpoint);
    }

    private Task<string?> ReadLineAsync() => _stream.ReadClientLineAsync();

    // ---------------------------------------------------------------------------
    // JOIN
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task JoinAsync_NoKey_SendsCorrectLine()
    {
        await ConnectAsync();
        await _router.JoinAsync("#general");
        Assert.Equal("JOIN #general", await ReadLineAsync());
    }

    [Fact]
    public async Task JoinAsync_WithKey_IncludesKey()
    {
        await ConnectAsync();
        await _router.JoinAsync("#secret", "password");
        Assert.Equal("JOIN #secret password", await ReadLineAsync());
    }

    [Theory]
    [InlineData("general")]      // no prefix
    [InlineData("#gen eral")]    // space
    [InlineData("#gen,eral")]    // comma
    [InlineData("")]             // empty
    public async Task JoinAsync_InvalidChannel_Throws(string channel)
    {
        await ConnectAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => _router.JoinAsync(channel));
    }

    [Theory]
    [InlineData("#channel")]
    [InlineData("&channel")]
    [InlineData("+channel")]
    [InlineData("!channel")]
    public async Task JoinAsync_AllChannelPrefixes_Accepted(string channel)
    {
        await ConnectAsync();
        await _router.JoinAsync(channel);
        var line = await ReadLineAsync();
        Assert.StartsWith("JOIN ", line);
    }

    // ---------------------------------------------------------------------------
    // PART
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task PartAsync_NoReason_SendsCorrectLine()
    {
        await ConnectAsync();
        await _router.PartAsync("#general");
        Assert.Equal("PART #general", await ReadLineAsync());
    }

    [Fact]
    public async Task PartAsync_WithReason_IncludesColonReason()
    {
        await ConnectAsync();
        await _router.PartAsync("#general", "bye");
        Assert.Equal("PART #general :bye", await ReadLineAsync());
    }

    [Fact]
    public async Task PartAsync_InvalidChannel_Throws()
    {
        await ConnectAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => _router.PartAsync("nochannel"));
    }

    // ---------------------------------------------------------------------------
    // MSG
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task MsgAsync_ToChannel_SendsPrivmsg()
    {
        await ConnectAsync();
        await _router.MsgAsync("#general", "hello everyone");
        Assert.Equal("PRIVMSG #general :hello everyone", await ReadLineAsync());
    }

    [Fact]
    public async Task MsgAsync_ToNick_SendsPrivmsg()
    {
        await ConnectAsync();
        await _router.MsgAsync("alice", "hey");
        Assert.Equal("PRIVMSG alice :hey", await ReadLineAsync());
    }

    [Theory]
    [InlineData("", "text")]
    [InlineData("alice", "")]
    public async Task MsgAsync_EmptyTargetOrText_Throws(string target, string text)
    {
        await ConnectAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => _router.MsgAsync(target, text));
    }

    [Fact]
    public async Task MsgAsync_TargetWithSpace_Throws()
    {
        await ConnectAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => _router.MsgAsync("al ice", "hi"));
    }

    // ---------------------------------------------------------------------------
    // NOTICE
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task NoticeAsync_SendsCorrectLine()
    {
        await ConnectAsync();
        await _router.NoticeAsync("#ops", "heads up");
        Assert.Equal("NOTICE #ops :heads up", await ReadLineAsync());
    }

    [Theory]
    [InlineData("", "text")]
    [InlineData("alice", "")]
    public async Task NoticeAsync_EmptyTargetOrText_Throws(string target, string text)
    {
        await ConnectAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => _router.NoticeAsync(target, text));
    }

    // ---------------------------------------------------------------------------
    // NICK
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task NickAsync_ValidNick_SendsCorrectLine()
    {
        await ConnectAsync();
        await _router.NickAsync("NewNick");
        Assert.Equal("NICK NewNick", await ReadLineAsync());
    }

    [Theory]
    [InlineData("nick name")]    // space
    [InlineData("ni,ck")]        // comma
    [InlineData("9startdigit")]  // starts with digit
    [InlineData("")]             // empty
    public async Task NickAsync_InvalidNick_Throws(string nick)
    {
        await ConnectAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => _router.NickAsync(nick));
    }

    [Theory]
    [InlineData("[nick]")]
    [InlineData("\\nick\\")]
    [InlineData("^nick^")]
    [InlineData("_nick_")]
    [InlineData("`nick`")]
    [InlineData("{nick}")]
    [InlineData("|nick|")]
    public async Task NickAsync_StartsWithSpecialChar_Accepted(string nick)
    {
        await ConnectAsync();
        await _router.NickAsync(nick);
        var line = await ReadLineAsync();
        Assert.StartsWith("NICK ", line);
    }

    // ---------------------------------------------------------------------------
    // QUIT
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task QuitAsync_NoReason_SendsQuit()
    {
        await ConnectAsync();
        await _router.QuitAsync();
        Assert.Equal("QUIT", await ReadLineAsync());
    }

    [Fact]
    public async Task QuitAsync_WithReason_SendsQuitColonReason()
    {
        await ConnectAsync();
        await _router.QuitAsync("goodbye");
        Assert.Equal("QUIT :goodbye", await ReadLineAsync());
    }

    // ---------------------------------------------------------------------------
    // RAW
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task RawAsync_ArbitraryLine_SendsVerbatim()
    {
        await ConnectAsync();
        await _router.RawAsync("WHO #channel");
        Assert.Equal("WHO #channel", await ReadLineAsync());
    }

    [Fact]
    public async Task RawAsync_EmptyLine_Throws()
    {
        await ConnectAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => _router.RawAsync(""));
    }

    [Theory]
    [InlineData("LINE\rBAD")]
    [InlineData("LINE\nBAD")]
    [InlineData("LINE\r\nBAD")]
    public async Task RawAsync_LineContainsCrOrLf_Throws(string line)
    {
        await ConnectAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => _router.RawAsync(line));
    }
}
