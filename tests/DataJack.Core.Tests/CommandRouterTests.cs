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

    // ---------------------------------------------------------------------------
    // KICK
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task KickAsync_NoReason_SendsCorrectLine()
    {
        await ConnectAsync();
        await _router.KickAsync("#test", "alice");
        Assert.Equal("KICK #test alice", await ReadLineAsync());
    }

    [Fact]
    public async Task KickAsync_WithReason_IncludesColonReason()
    {
        await ConnectAsync();
        await _router.KickAsync("#test", "alice", "spamming");
        Assert.Equal("KICK #test alice :spamming", await ReadLineAsync());
    }

    [Fact]
    public async Task KickAsync_InvalidChannel_Throws()
    {
        await ConnectAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => _router.KickAsync("notachannel", "alice"));
    }

    [Fact]
    public async Task KickAsync_InvalidNick_Throws()
    {
        await ConnectAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => _router.KickAsync("#test", ""));
    }

    // ---------------------------------------------------------------------------
    // BAN / UNBAN
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task BanAsync_SendsModePlusBLine()
    {
        await ConnectAsync();
        await _router.BanAsync("#test", "alice!*@*");
        Assert.Equal("MODE #test +b alice!*@*", await ReadLineAsync());
    }

    [Fact]
    public async Task UnbanAsync_SendsModeMinusBLine()
    {
        await ConnectAsync();
        await _router.UnbanAsync("#test", "alice!*@*");
        Assert.Equal("MODE #test -b alice!*@*", await ReadLineAsync());
    }

    [Fact]
    public async Task BanAsync_EmptyMask_Throws()
    {
        await ConnectAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => _router.BanAsync("#test", ""));
    }

    // ---------------------------------------------------------------------------
    // KICKBAN
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task KickBanAsync_NoReason_SendsBanThenKick()
    {
        await ConnectAsync();
        await _router.KickBanAsync("#test", "alice", "alice!*@*");
        Assert.Equal("MODE #test +b alice!*@*", await ReadLineAsync());
        Assert.Equal("KICK #test alice", await ReadLineAsync());
    }

    [Fact]
    public async Task KickBanAsync_WithReason_KickLineIncludesReason()
    {
        await ConnectAsync();
        await _router.KickBanAsync("#test", "alice", "alice!*@*", "flooding");
        Assert.Equal("MODE #test +b alice!*@*", await ReadLineAsync());
        Assert.Equal("KICK #test alice :flooding", await ReadLineAsync());
    }

    // ---------------------------------------------------------------------------
    // OP / DEOP / VOICE / DEVOICE
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task OpAsync_SendsModeOpLine()
    {
        await ConnectAsync();
        await _router.OpAsync("#test", "alice");
        Assert.Equal("MODE #test +o alice", await ReadLineAsync());
    }

    [Fact]
    public async Task DeopAsync_SendsModeMinusOpLine()
    {
        await ConnectAsync();
        await _router.DeopAsync("#test", "alice");
        Assert.Equal("MODE #test -o alice", await ReadLineAsync());
    }

    [Fact]
    public async Task VoiceAsync_SendsModeVoiceLine()
    {
        await ConnectAsync();
        await _router.VoiceAsync("#test", "alice");
        Assert.Equal("MODE #test +v alice", await ReadLineAsync());
    }

    [Fact]
    public async Task DevoiceAsync_SendsModeMinusVoiceLine()
    {
        await ConnectAsync();
        await _router.DevoiceAsync("#test", "alice");
        Assert.Equal("MODE #test -v alice", await ReadLineAsync());
    }

    [Fact]
    public async Task OpAsync_InvalidChannel_Throws()
    {
        await ConnectAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => _router.OpAsync("notachan", "alice"));
    }

    // ---------------------------------------------------------------------------
    // MODE (general)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ModeAsync_NoParams_SendsModeString()
    {
        await ConnectAsync();
        await _router.ModeAsync("#test", "+mn");
        Assert.Equal("MODE #test +mn", await ReadLineAsync());
    }

    [Fact]
    public async Task ModeAsync_WithParams_AppendsParamsSpaceSeparated()
    {
        await ConnectAsync();
        await _router.ModeAsync("#test", "+kl", ["secretkey", "50"]);
        Assert.Equal("MODE #test +kl secretkey 50", await ReadLineAsync());
    }

    [Fact]
    public async Task ModeAsync_EmptyModeString_Throws()
    {
        await ConnectAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => _router.ModeAsync("#test", ""));
    }

    // ---------------------------------------------------------------------------
    // INVITE
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task InviteAsync_SendsInviteLine()
    {
        await ConnectAsync();
        await _router.InviteAsync("alice", "#secret");
        Assert.Equal("INVITE alice #secret", await ReadLineAsync());
    }

    [Fact]
    public async Task InviteAsync_InvalidChannel_Throws()
    {
        await ConnectAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => _router.InviteAsync("alice", "notachannel"));
    }

    // ---------------------------------------------------------------------------
    // TOPIC
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task TopicAsync_WithText_SendsTopicLine()
    {
        await ConnectAsync();
        await _router.TopicAsync("#test", "Welcome to the channel");
        Assert.Equal("TOPIC #test :Welcome to the channel", await ReadLineAsync());
    }

    [Fact]
    public async Task TopicAsync_NoText_SendsBareTopicToQueryOrClear()
    {
        await ConnectAsync();
        await _router.TopicAsync("#test");
        Assert.Equal("TOPIC #test", await ReadLineAsync());
    }

    // ---------------------------------------------------------------------------
    // NAMES
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task NamesAsync_WithChannel_SendsNamesLine()
    {
        await ConnectAsync();
        await _router.NamesAsync("#test");
        Assert.Equal("NAMES #test", await ReadLineAsync());
    }

    [Fact]
    public async Task NamesAsync_NoChannel_SendsBareNames()
    {
        await ConnectAsync();
        await _router.NamesAsync();
        Assert.Equal("NAMES", await ReadLineAsync());
    }

    // ---------------------------------------------------------------------------
    // LIST
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ListAsync_NoFilter_SendsBareList()
    {
        await ConnectAsync();
        await _router.ListAsync();
        Assert.Equal("LIST", await ReadLineAsync());
    }

    [Fact]
    public async Task ListAsync_WithFilter_AppendsFilter()
    {
        await ConnectAsync();
        await _router.ListAsync(">50");
        Assert.Equal("LIST >50", await ReadLineAsync());
    }

    // ---------------------------------------------------------------------------
    // WHOIS / WHO
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task WhoisAsync_SendsWhoisLine()
    {
        await ConnectAsync();
        await _router.WhoisAsync("alice");
        Assert.Equal("WHOIS alice", await ReadLineAsync());
    }

    [Fact]
    public async Task WhoisAsync_EmptyNick_Throws()
    {
        await ConnectAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => _router.WhoisAsync(""));
    }

    [Fact]
    public async Task WhoAsync_WithMask_SendsWhoLine()
    {
        await ConnectAsync();
        await _router.WhoAsync("#test");
        Assert.Equal("WHO #test", await ReadLineAsync());
    }

    [Fact]
    public async Task WhoAsync_NoMask_SendsBareWho()
    {
        await ConnectAsync();
        await _router.WhoAsync();
        Assert.Equal("WHO", await ReadLineAsync());
    }

    // ---------------------------------------------------------------------------
    // QUERY
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task QueryAsync_WithMessage_SendsPrivmsg()
    {
        await ConnectAsync();
        await _router.QueryAsync("alice", "hey there");
        Assert.Equal("PRIVMSG alice :hey there", await ReadLineAsync());
    }

    [Fact]
    public async Task QueryAsync_NoMessage_SendsNothingAndCompletes()
    {
        await ConnectAsync();
        // No protocol line sent; task should complete without blocking.
        await _router.QueryAsync("alice");
        // No line to read; confirm the connection is still open by sending a known line.
        await _router.RawAsync("PING :test");
        Assert.Equal("PING :test", await ReadLineAsync());
    }

    // ---------------------------------------------------------------------------
    // ME (/me — CTCP ACTION)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task MeAsync_SendsCtcpActionLine()
    {
        await ConnectAsync();
        await _router.MeAsync("#test", "waves hello");
        Assert.Equal("PRIVMSG #test :ACTION waves hello\x01", await ReadLineAsync());
    }

    [Fact]
    public async Task MeAsync_EmptyText_Throws()
    {
        await ConnectAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => _router.MeAsync("#test", ""));
    }

    // ---------------------------------------------------------------------------
    // CTCP
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task CtcpAsync_NoParams_SendsWrappedCommand()
    {
        await ConnectAsync();
        await _router.CtcpAsync("alice", "VERSION");
        Assert.Equal("PRIVMSG alice :\x01VERSION\x01", await ReadLineAsync());
    }

    [Fact]
    public async Task CtcpAsync_WithParams_IncludesParams()
    {
        await ConnectAsync();
        await _router.CtcpAsync("alice", "DCC", "SEND file.txt 0 1234 512");
        Assert.Equal("PRIVMSG alice :DCC SEND file.txt 0 1234 512\x01", await ReadLineAsync());
    }

    [Fact]
    public async Task CtcpAsync_EmptyCommand_Throws()
    {
        await ConnectAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => _router.CtcpAsync("alice", ""));
    }

    // ---------------------------------------------------------------------------
    // PING (CTCP)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task PingAsync_SendsCtcpPingWithTimestamp()
    {
        await ConnectAsync();
        await _router.PingAsync("alice");
        var line = await ReadLineAsync();
        Assert.NotNull(line);
        Assert.StartsWith("PRIVMSG alice :\x01PING ", line!);
        Assert.EndsWith("\x01", line);
    }

    // ---------------------------------------------------------------------------
    // AWAY / BACK
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task AwayAsync_WithMessage_SendsAwayLine()
    {
        await ConnectAsync();
        await _router.AwayAsync("Be right back");
        Assert.Equal("AWAY :Be right back", await ReadLineAsync());
    }

    [Fact]
    public async Task AwayAsync_NoMessage_SendsBareAway()
    {
        await ConnectAsync();
        await _router.AwayAsync();
        Assert.Equal("AWAY", await ReadLineAsync());
    }

    [Fact]
    public async Task BackAsync_SendsBareAway()
    {
        await ConnectAsync();
        await _router.BackAsync();
        Assert.Equal("AWAY", await ReadLineAsync());
    }
}
