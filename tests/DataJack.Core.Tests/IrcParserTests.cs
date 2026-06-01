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

    // ---------------------------------------------------------------------------
    // Phase 3 numeric and command handlers
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Parser_005_DispatchesIsupportTokensReceived()
    {
        var parser = new IRCParser("libera", _dispatcher);
        var tcs = new TaskCompletionSource<IsupportTokensReceived>();
        _dispatcher.Subscribe<IsupportTokensReceived>(e => tcs.TrySetResult(e));

        await _dispatcher.PublishAsync(new RawLineReceived("libera",
            ":irc.libera.chat 005 me NETWORK=Libera PREFIX=(ov)@+ MODES=5 :are supported by this server"));

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("libera", evt.Server);
        Assert.Equal("Libera", evt.Tokens["NETWORK"]);
        Assert.Equal("(ov)@+", evt.Tokens["PREFIX"]);
        Assert.Equal("5", evt.Tokens["MODES"]);
        Assert.False(evt.Tokens.ContainsKey("are supported by this server"));
    }

    [Fact]
    public async Task Parser_Whois_AssemblesMultipleNumericsIntoWhoIsReply()
    {
        var parser = new IRCParser("libera", _dispatcher);
        var replytcs = new TaskCompletionSource<WhoIsReply>();
        var endtcs   = new TaskCompletionSource<WhoIsEnd>();
        _dispatcher.Subscribe<WhoIsReply>(e => replytcs.TrySetResult(e));
        _dispatcher.Subscribe<WhoIsEnd>(e => endtcs.TrySetResult(e));

        // Send 311, 312, 317, 330, then 318
        await _dispatcher.PublishAsync(new RawLineReceived("libera",
            ":irc.libera.chat 311 me alice alice irc.example.com * :Alice Smith"));
        await _dispatcher.PublishAsync(new RawLineReceived("libera",
            ":irc.libera.chat 312 me alice irc.libera.chat :Libera IRC"));
        await _dispatcher.PublishAsync(new RawLineReceived("libera",
            ":irc.libera.chat 317 me alice 120 1700000000 :seconds idle, signon time"));
        await _dispatcher.PublishAsync(new RawLineReceived("libera",
            ":irc.libera.chat 330 me alice AliceAccount :is logged in as"));
        await _dispatcher.PublishAsync(new RawLineReceived("libera",
            ":irc.libera.chat 318 me alice :End of /WHOIS list"));

        var reply = await replytcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var end   = await endtcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("alice",         reply.Nick);
        Assert.Equal("alice",         reply.User);
        Assert.Equal("irc.example.com", reply.Host);
        Assert.Equal("Alice Smith",   reply.RealName);
        Assert.Equal("irc.libera.chat", reply.ServerName);
        Assert.Equal(120,             reply.IdleSeconds);
        Assert.Equal("AliceAccount",  reply.Account);
        Assert.Equal("alice",         end.Nick);
    }

    [Fact]
    public async Task Parser_318_WithoutPrior311_OnlyEmitsWhoIsEnd()
    {
        var parser = new IRCParser("libera", _dispatcher);
        bool replyFired = false;
        var endtcs = new TaskCompletionSource<WhoIsEnd>();
        _dispatcher.Subscribe<WhoIsReply>(_ => replyFired = true);
        _dispatcher.Subscribe<WhoIsEnd>(e => endtcs.TrySetResult(e));

        await _dispatcher.PublishAsync(new RawLineReceived("libera",
            ":irc.libera.chat 318 me ghost :End of /WHOIS list"));

        var end = await endtcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("ghost", end.Nick);
        Assert.False(replyFired);
    }

    [Fact]
    public async Task Parser_352_DispatchesWhoReplyEntry()
    {
        var parser = new IRCParser("libera", _dispatcher);
        var tcs = new TaskCompletionSource<WhoReplyEntry>();
        _dispatcher.Subscribe<WhoReplyEntry>(e => tcs.TrySetResult(e));

        // Fields (all distinct): channel  user        host                server          nick     status hops+realname
        await _dispatcher.PublishAsync(new RawLineReceived("libera",
            ":irc.libera.chat 352 me #test bobuser bobhost.example irc.libera.chat bobnick H :0 Bob Smith"));

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("libera",          evt.Server);
        Assert.Equal("#test",           evt.Channel);
        Assert.Equal("bobnick",         evt.Nick);
        Assert.Equal("bobuser",         evt.User);
        Assert.Equal("bobhost.example", evt.Host);
        Assert.Equal("Bob Smith",       evt.RealName);
    }

    [Fact]
    public async Task Parser_315_DispatchesWhoEnd()
    {
        var parser = new IRCParser("libera", _dispatcher);
        var tcs = new TaskCompletionSource<WhoEnd>();
        _dispatcher.Subscribe<WhoEnd>(e => tcs.TrySetResult(e));

        await _dispatcher.PublishAsync(new RawLineReceived("libera",
            ":irc.libera.chat 315 me #test :End of /WHO list"));

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("#test", evt.Target);
    }

    [Fact]
    public async Task Parser_332_DispatchesTopicChanged_NullSetter()
    {
        var parser = new IRCParser("libera", _dispatcher);
        var tcs = new TaskCompletionSource<TopicChanged>();
        _dispatcher.Subscribe<TopicChanged>(e => tcs.TrySetResult(e));

        await _dispatcher.PublishAsync(new RawLineReceived("libera",
            ":irc.libera.chat 332 me #test :Welcome to the channel"));

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("#test",                  evt.Channel);
        Assert.Equal("Welcome to the channel", evt.NewTopic);
        Assert.Null(evt.SetterNick);
    }

    [Fact]
    public async Task Parser_333_DispatchesTopicWhoTime()
    {
        var parser = new IRCParser("libera", _dispatcher);
        var tcs = new TaskCompletionSource<TopicWhoTime>();
        _dispatcher.Subscribe<TopicWhoTime>(e => tcs.TrySetResult(e));

        await _dispatcher.PublishAsync(new RawLineReceived("libera",
            ":irc.libera.chat 333 me #test alice 1700000000"));

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("#test",              evt.Channel);
        Assert.Equal("alice",              evt.SetterNick);
        Assert.Equal(1700000000,           evt.SetAt.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Parser_353_366_DispatchesNamesListReceived()
    {
        var parser = new IRCParser("libera", _dispatcher);
        var tcs = new TaskCompletionSource<NamesListReceived>();
        _dispatcher.Subscribe<NamesListReceived>(e => tcs.TrySetResult(e));

        // Two 353 lines followed by 366
        await _dispatcher.PublishAsync(new RawLineReceived("libera",
            ":irc.libera.chat 353 me = #test :@alice +bob carol"));
        await _dispatcher.PublishAsync(new RawLineReceived("libera",
            ":irc.libera.chat 353 me = #test :@dave"));
        await _dispatcher.PublishAsync(new RawLineReceived("libera",
            ":irc.libera.chat 366 me #test :End of /NAMES list"));

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("#test", evt.Channel);
        Assert.Equal(4, evt.Users.Count);

        var alice = evt.Users.First(u => u.Nick == "alice");
        Assert.Contains('@', alice.Prefixes);

        var bob = evt.Users.First(u => u.Nick == "bob");
        Assert.Contains('+', bob.Prefixes);

        var carol = evt.Users.First(u => u.Nick == "carol");
        Assert.Empty(carol.Prefixes);

        var dave = evt.Users.First(u => u.Nick == "dave");
        Assert.Contains('@', dave.Prefixes);
    }

    [Fact]
    public async Task Parser_367_368_DispatchesBanListEvents()
    {
        var parser = new IRCParser("libera", _dispatcher);
        var entryTcs = new TaskCompletionSource<BanListEntry>();
        var endTcs   = new TaskCompletionSource<BanListEnd>();
        _dispatcher.Subscribe<BanListEntry>(e => entryTcs.TrySetResult(e));
        _dispatcher.Subscribe<BanListEnd>(e => endTcs.TrySetResult(e));

        await _dispatcher.PublishAsync(new RawLineReceived("libera",
            ":irc.libera.chat 367 me #test *!*@badhost.example setter 1700000000"));
        await _dispatcher.PublishAsync(new RawLineReceived("libera",
            ":irc.libera.chat 368 me #test :End of channel ban list"));

        var entry = await entryTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("#test",               entry.Channel);
        Assert.Equal("*!*@badhost.example", entry.Mask);
        Assert.Equal("setter",              entry.Setter);
        Assert.Equal(1700000000,            entry.SetAt.ToUnixTimeSeconds());

        var end = await endTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("#test", end.Channel);
    }

    [Fact]
    public async Task Parser_329_DispatchesChannelCreated()
    {
        var parser = new IRCParser("libera", _dispatcher);
        var tcs = new TaskCompletionSource<ChannelCreated>();
        _dispatcher.Subscribe<ChannelCreated>(e => tcs.TrySetResult(e));

        await _dispatcher.PublishAsync(new RawLineReceived("libera",
            ":irc.libera.chat 329 me #test 1700000000"));

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("#test",      evt.Channel);
        Assert.Equal(1700000000,   evt.CreatedAt.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Parser_322_323_DispatchesChannelListEvents()
    {
        var parser = new IRCParser("libera", _dispatcher);
        var entryTcs = new TaskCompletionSource<ChannelListEntry>();
        var endTcs   = new TaskCompletionSource<ChannelListEnd>();
        _dispatcher.Subscribe<ChannelListEntry>(e => entryTcs.TrySetResult(e));
        _dispatcher.Subscribe<ChannelListEnd>(e => endTcs.TrySetResult(e));

        await _dispatcher.PublishAsync(new RawLineReceived("libera",
            ":irc.libera.chat 322 me #test 42 :A test channel"));
        await _dispatcher.PublishAsync(new RawLineReceived("libera",
            ":irc.libera.chat 323 me :End of /LIST"));

        var entry = await entryTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("#test",          entry.Channel);
        Assert.Equal(42,               entry.UserCount);
        Assert.Equal("A test channel", entry.Topic);

        await endTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Parser_730_DispatchesMonitorStatusChanged_Online()
    {
        var parser = new IRCParser("libera", _dispatcher);
        var tcs = new TaskCompletionSource<MonitorStatusChanged>();
        _dispatcher.Subscribe<MonitorStatusChanged>(e => tcs.TrySetResult(e));

        await _dispatcher.PublishAsync(new RawLineReceived("libera",
            ":irc.libera.chat 730 me :alice!alice@host.example"));

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("alice", evt.Nick);
        Assert.True(evt.IsOnline);
    }

    [Fact]
    public async Task Parser_731_DispatchesMonitorStatusChanged_Offline()
    {
        var parser = new IRCParser("libera", _dispatcher);
        var tcs = new TaskCompletionSource<MonitorStatusChanged>();
        _dispatcher.Subscribe<MonitorStatusChanged>(e => tcs.TrySetResult(e));

        await _dispatcher.PublishAsync(new RawLineReceived("libera",
            ":irc.libera.chat 731 me :bob"));

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("bob",  evt.Nick);
        Assert.False(evt.IsOnline);
    }

    [Fact]
    public async Task Parser_ChannelMode_DispatchesChannelModeChanged()
    {
        var parser = new IRCParser("libera", _dispatcher);
        var tcs = new TaskCompletionSource<ChannelModeChanged>();
        _dispatcher.Subscribe<ChannelModeChanged>(e => tcs.TrySetResult(e));

        await _dispatcher.PublishAsync(new RawLineReceived("libera",
            ":alice!alice@host MODE #test +o bob"));

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("#test", evt.Channel);
        Assert.Equal("+o",    evt.ModeString);
        Assert.Equal("bob",   evt.Params[0]);
        Assert.Equal("alice", evt.SetterNick);
    }

    [Fact]
    public async Task Parser_UserMode_DispatchesUserModeChanged()
    {
        var parser = new IRCParser("libera", _dispatcher);
        var tcs = new TaskCompletionSource<UserModeChanged>();
        _dispatcher.Subscribe<UserModeChanged>(e => tcs.TrySetResult(e));

        await _dispatcher.PublishAsync(new RawLineReceived("libera",
            ":irc.libera.chat MODE mynick +i"));

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("mynick", evt.Nick);
        Assert.Equal("+i",     evt.ModeString);
    }

    [Fact]
    public async Task Parser_AWAY_SetAway_DispatchesUserAwayChanged_True()
    {
        var parser = new IRCParser("libera", _dispatcher);
        var tcs = new TaskCompletionSource<UserAwayChanged>();
        _dispatcher.Subscribe<UserAwayChanged>(e => tcs.TrySetResult(e));

        await _dispatcher.PublishAsync(new RawLineReceived("libera",
            ":carol!carol@host AWAY :Be right back"));

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("carol",          evt.Nick);
        Assert.True(evt.IsAway);
        Assert.Equal("Be right back",  evt.Message);
    }

    [Fact]
    public async Task Parser_AWAY_NoParam_DispatchesUserAwayChanged_False()
    {
        var parser = new IRCParser("libera", _dispatcher);
        var tcs = new TaskCompletionSource<UserAwayChanged>();
        _dispatcher.Subscribe<UserAwayChanged>(e => tcs.TrySetResult(e));

        await _dispatcher.PublishAsync(new RawLineReceived("libera",
            ":carol!carol@host AWAY"));

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("carol", evt.Nick);
        Assert.False(evt.IsAway);
        Assert.Null(evt.Message);
    }

    [Fact]
    public async Task Parser_CHGHOST_DispatchesUserHostChanged()
    {
        var parser = new IRCParser("libera", _dispatcher);
        var tcs = new TaskCompletionSource<UserHostChanged>();
        _dispatcher.Subscribe<UserHostChanged>(e => tcs.TrySetResult(e));

        await _dispatcher.PublishAsync(new RawLineReceived("libera",
            ":dave!olduser@oldhost CHGHOST newuser newhost.example"));

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("dave",            evt.Nick);
        Assert.Equal("newuser",         evt.NewUser);
        Assert.Equal("newhost.example", evt.NewHost);
    }

    [Fact]
    public async Task Parser_ACCOUNT_DispatchesUserAccountChanged_WithAccount()
    {
        var parser = new IRCParser("libera", _dispatcher);
        var tcs = new TaskCompletionSource<UserAccountChanged>();
        _dispatcher.Subscribe<UserAccountChanged>(e => tcs.TrySetResult(e));

        await _dispatcher.PublishAsync(new RawLineReceived("libera",
            ":eve!eve@host ACCOUNT EveServices"));

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("eve",         evt.Nick);
        Assert.Equal("EveServices", evt.Account);
    }

    [Fact]
    public async Task Parser_ACCOUNT_Star_DispatchesUserAccountChanged_NullAccount()
    {
        var parser = new IRCParser("libera", _dispatcher);
        var tcs = new TaskCompletionSource<UserAccountChanged>();
        _dispatcher.Subscribe<UserAccountChanged>(e => tcs.TrySetResult(e));

        await _dispatcher.PublishAsync(new RawLineReceived("libera",
            ":eve!eve@host ACCOUNT *"));

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(evt.Account);
    }

    [Fact]
    public async Task Parser_SETNAME_DispatchesUserRealNameChanged()
    {
        var parser = new IRCParser("libera", _dispatcher);
        var tcs = new TaskCompletionSource<UserRealNameChanged>();
        _dispatcher.Subscribe<UserRealNameChanged>(e => tcs.TrySetResult(e));

        await _dispatcher.PublishAsync(new RawLineReceived("libera",
            ":frank!frank@host SETNAME :Frank The New Name"));

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("frank",              evt.Nick);
        Assert.Equal("Frank The New Name", evt.NewRealName);
    }

    public async ValueTask DisposeAsync() => await _dispatcher.DisposeAsync();
}
