// SPDX-License-Identifier: GPL-3.0-or-later
// NOTE: CTCP delimiter strings use  (4-digit Unicode escape), NOT \x01.
// \x is greedy: "\x01ACTION" would consume 'A' and 'C' as hex digits, producing
// char U+01AC instead of SOH + "ACTION".
using DataJack.Core.Events;
using DataJack.Core.Irc;
using Xunit;

namespace DataJack.Core.Tests;

public sealed class IrcParserTests : IAsyncDisposable
{
    private readonly EventDispatcher _dispatcher = new();

    public IrcParserTests() => _dispatcher.Start();

    // ---------------------------------------------------------------------------
    // ParseMessage — pure static; no dispatcher required
    // ---------------------------------------------------------------------------

    [Fact]
    public void ParseMessage_EmptyLine_ReturnsNull()
    {
        Assert.Null(IRCParser.ParseMessage(""));
        Assert.Null(IRCParser.ParseMessage(string.Empty));
    }

    [Fact]
    public void ParseMessage_SimpleCommand_ParsesCorrectly()
    {
        var msg = IRCParser.ParseMessage("PING :irc.libera.chat");
        Assert.NotNull(msg);
        Assert.Equal("PING", msg!.Value.Command);
        Assert.Null(msg.Value.Prefix);
        Assert.Equal("irc.libera.chat", msg.Value.Param(0));
    }

    [Fact]
    public void ParseMessage_PrefixedNumeric_ExtractsAllFields()
    {
        var msg = IRCParser.ParseMessage(":irc.libera.chat 001 somenick :Welcome to Libera");
        Assert.NotNull(msg);
        var m = msg!.Value;
        Assert.Equal("irc.libera.chat", m.Prefix);
        Assert.Null(m.Nick); // server prefix, not nick!user@host
        Assert.Equal("001", m.Command);
        Assert.Equal("somenick", m.Param(0));
        Assert.Equal("Welcome to Libera", m.Param(1));
    }

    [Fact]
    public void ParseMessage_NickPrefix_ExtractsNickUserHost()
    {
        var msg = IRCParser.ParseMessage(":alice!alice@irc.example.com PRIVMSG #test :hello");
        Assert.NotNull(msg);
        var m = msg!.Value;
        Assert.Equal("alice", m.Nick);
        Assert.Equal("alice", m.User);
        Assert.Equal("irc.example.com", m.Host);
        Assert.Equal("PRIVMSG", m.Command);
        Assert.Equal("#test", m.Param(0));
        Assert.Equal("hello", m.Param(1));
    }

    [Fact]
    public void ParseMessage_Tags_ParsedAndUnescaped()
    {
        var msg = IRCParser.ParseMessage(
            @"@time=2024-01-01T00:00:00Z;msgid=abc123;label=test\svalue :server NOTICE * :hi");
        Assert.NotNull(msg);
        var tags = msg!.Value.Tags;
        Assert.NotNull(tags);
        Assert.Equal("2024-01-01T00:00:00Z", tags!["time"]);
        Assert.Equal("abc123", tags["msgid"]);
        Assert.Equal("test value", tags["label"]); // \s unescaped to space
    }

    [Fact]
    public void ParseMessage_TagWithNoValue_TreatedAsEmptyString()
    {
        var msg = IRCParser.ParseMessage("@draft/reacts :server PRIVMSG #ch :x");
        Assert.NotNull(msg);
        Assert.Equal(string.Empty, msg!.Value.Tags!["draft/reacts"]);
    }

    [Fact]
    public void ParseMessage_TrailingParamWithSpaces_PreservesSpaces()
    {
        var msg = IRCParser.ParseMessage(":server TOPIC #test :this is the topic with spaces");
        Assert.NotNull(msg);
        Assert.Equal("this is the topic with spaces", msg!.Value.Param(1));
    }

    [Fact]
    public void ParseMessage_MultipleMiddleParams_AllParsed()
    {
        var msg = IRCParser.ParseMessage(":server MODE #test +ov alice bob");
        Assert.NotNull(msg);
        Assert.Equal(4, msg!.Value.Params.Length);
        Assert.Equal("#test", msg.Value.Param(0));
        Assert.Equal("+ov", msg.Value.Param(1));
        Assert.Equal("alice", msg.Value.Param(2));
        Assert.Equal("bob", msg.Value.Param(3));
    }

    // ---------------------------------------------------------------------------
    // ParseNickUserHost
    // ---------------------------------------------------------------------------

    [Fact]
    public void ParseNickUserHost_FullPrefix_SplitsCorrectly()
    {
        IRCParser.ParseNickUserHost("nick!user@host.example", out var nick, out var user, out var host);
        Assert.Equal("nick", nick);
        Assert.Equal("user", user);
        Assert.Equal("host.example", host);
    }

    [Fact]
    public void ParseNickUserHost_ServerPrefix_AllNull()
    {
        IRCParser.ParseNickUserHost("irc.server.net", out var nick, out var user, out var host);
        Assert.Null(nick);
        Assert.Null(user);
        Assert.Null(host);
    }

    // ---------------------------------------------------------------------------
    // ParseTags — tag value unescaping
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData(@"a\:b",  "a;b")]
    [InlineData(@"a\sb",  "a b")]
    [InlineData(@"a\\b",  @"a\b")]
    [InlineData(@"a\rb",  "a\rb")]
    [InlineData(@"a\nb",  "a\nb")]
    [InlineData(@"a\xb",  "axb")] // unknown escape: drop the backslash, keep the char
    public void ParseTags_UnescapesValues(string raw, string expected)
    {
        var tags = IRCParser.ParseTags($"key={raw}".AsSpan());
        Assert.Equal(expected, tags["key"]);
    }

    // ---------------------------------------------------------------------------
    // Integration: dispatcher wired to parser
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Parser_001_DispatchesWelcomeReceived()
    {
        var parser = new IRCParser("libera", _dispatcher);
        var tcs = new TaskCompletionSource<WelcomeReceived>();
        _dispatcher.Subscribe<WelcomeReceived>(e => tcs.TrySetResult(e));

        await _dispatcher.PublishAsync(
            new RawLineReceived("libera", ":irc.libera.chat 001 testnick :Welcome"));

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("libera", evt.Server);
        Assert.Equal("testnick", evt.Nick);
    }

    [Fact]
    public async Task Parser_PRIVMSG_DispatchesMessageReceived()
    {
        var parser = new IRCParser("libera", _dispatcher);
        var tcs = new TaskCompletionSource<MessageReceived>();
        _dispatcher.Subscribe<MessageReceived>(e => tcs.TrySetResult(e));

        await _dispatcher.PublishAsync(
            new RawLineReceived("libera", ":alice!a@host PRIVMSG #test :hello world"));

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("alice", evt.FromNick);
        Assert.Equal("#test", evt.Target);
        Assert.Equal("hello world", evt.Text);
    }

    [Fact]
    public async Task Parser_CtcpAction_DispatchesActionReceived()
    {
        var parser = new IRCParser("libera", _dispatcher);
        var tcs = new TaskCompletionSource<ActionReceived>();
        _dispatcher.Subscribe<ActionReceived>(e => tcs.TrySetResult(e));

        await _dispatcher.PublishAsync(
            new RawLineReceived("libera", ":bob!b@host PRIVMSG #ch :ACTION waves"));

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("bob", evt.FromNick);
        Assert.Equal("waves", evt.Text);
    }

    [Fact]
    public async Task Parser_JOIN_DispatchesJoinedChannel()
    {
        var parser = new IRCParser("libera", _dispatcher);
        var tcs = new TaskCompletionSource<JoinedChannel>();
        _dispatcher.Subscribe<JoinedChannel>(e => tcs.TrySetResult(e));

        await _dispatcher.PublishAsync(
            new RawLineReceived("libera", ":carol!c@host JOIN #channel"));

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("carol", evt.Nick);
        Assert.Equal("#channel", evt.Channel);
        Assert.Null(evt.Account);
    }

    [Fact]
    public async Task Parser_DifferentServer_IsIgnored()
    {
        var parser = new IRCParser("libera", _dispatcher);
        bool fired = false;
        _dispatcher.Subscribe<WelcomeReceived>(_ => fired = true);

        await _dispatcher.PublishAsync(
            new RawLineReceived("other-server", ":s 001 nick :Welcome"));

        await Task.Delay(100);
        Assert.False(fired);
    }

    public async ValueTask DisposeAsync() => await _dispatcher.DisposeAsync();
}
