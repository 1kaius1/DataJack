// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.Events;
using DataJack.Core.State;
using Xunit;

namespace DataJack.Core.Tests;

// ---------------------------------------------------------------------------
// Shared fixture: EventDispatcher + IRCStateModel + IRCStateUpdater
// ---------------------------------------------------------------------------

public sealed class IRCStateUpdaterTests : IAsyncDisposable
{
    private const string Server      = "libera";
    private const string OtherServer = "efnet";

    private readonly EventDispatcher  _dispatcher = new();
    private readonly IRCStateModel    _model      = new();
    private readonly IRCStateUpdater  _updater;

    public IRCStateUpdaterTests()
    {
        _dispatcher.Start();
        _updater = new IRCStateUpdater(Server, _model, _dispatcher);
    }

    public ValueTask DisposeAsync() => _dispatcher.DisposeAsync();

    // Publish an event and yield to the dispatch loop.
    private async Task Pub<T>(T evt) where T : struct
    {
        await _dispatcher.PublishAsync(evt, EventPriority.Normal);
        await Task.Delay(50);
    }

    // Bring the server to a connected + registered state.
    private async Task ConnectAsync(string nick = "TestUser")
    {
        await Pub(new ConnectionAttempted(Server, "irc.libera.chat", 6697, true));
        await Pub(new ConnectionEstablished(Server));
        await Pub(new WelcomeReceived(Server, nick));
    }

    // Join a channel as the local user, then add one other user to it.
    private async Task JoinChannelAsync(string channel, string otherNick = "alice")
    {
        await Pub(new JoinedChannel(Server, channel, "TestUser", null));
        await Pub(new JoinedChannel(Server, channel, otherNick, null));
    }

    // ---------------------------------------------------------------------------
    // Connection lifecycle
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ConnectionAttempted_CreatesServerEntry()
    {
        await Pub(new ConnectionAttempted(Server, "irc.libera.chat", 6697, true));

        var q = _model.CreateQuery();
        Assert.False(q.IsConnected(Server));
    }

    [Fact]
    public async Task ConnectionEstablished_MarksConnected()
    {
        await Pub(new ConnectionAttempted(Server, "irc.libera.chat", 6697, false));
        await Pub(new ConnectionEstablished(Server));

        Assert.True(_model.CreateQuery().IsConnected(Server));
    }

    [Fact]
    public async Task WelcomeReceived_SetsRegisteredNick()
    {
        await ConnectAsync("mynick");

        Assert.Equal("mynick", _model.CreateQuery().GetCurrentNick(Server));
    }

    [Fact]
    public async Task ConnectionClosed_MarksDisconnectedAndClearsState()
    {
        await ConnectAsync();
        await Pub(new CapabilityNegotiated(Server, ["server-time"], []));
        await Pub(new JoinedChannel(Server, "#test", "TestUser", null));
        await Pub(new ConnectionClosed(Server, null));

        var q = _model.CreateQuery();
        Assert.False(q.IsConnected(Server));
        Assert.Null(q.GetCurrentNick(Server));
        Assert.Empty(q.GetChannelUsers(Server, "#test"));
        Assert.Empty(q.GetActiveCapabilities(Server));
    }

    [Fact]
    public async Task ConnectionClosed_MarksMonitoredNicksOffline()
    {
        await ConnectAsync();
        await Pub(new MonitorStatusChanged(Server, "bob", IsOnline: true));
        await Pub(new ConnectionClosed(Server, null));

        // The snapshot's MonitoredNicks list is not exposed via IRCStateQuery, so
        // verify directly through Apply returning the updated snapshot.
        var snapshot = _model.Apply(s => s); // read-only Apply to get snapshot
        var server   = snapshot.Servers[Server];
        Assert.Single(server.MonitoredNicks, m => m.Nick == "bob" && !m.IsOnline);
    }

    // ---------------------------------------------------------------------------
    // ISUPPORT
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task IsupportTokensReceived_StoresTokens()
    {
        await ConnectAsync();
        await Pub(new IsupportTokensReceived(Server,
            new Dictionary<string, string> { ["MAXNICKLEN"] = "30", ["PREFIX"] = "(ov)@+" }));

        var snap = _model.Apply(s => s);
        var tok  = snap.Servers[Server].IsupportTokens;
        Assert.Equal("30", tok["MAXNICKLEN"]);
        Assert.Equal("(ov)@+", tok["PREFIX"]);
    }

    [Fact]
    public async Task IsupportTokensReceived_MergesOnSubsequentCall()
    {
        await ConnectAsync();
        await Pub(new IsupportTokensReceived(Server,
            new Dictionary<string, string> { ["MAXNICKLEN"] = "30" }));
        await Pub(new IsupportTokensReceived(Server,
            new Dictionary<string, string> { ["MAXNICKLEN"] = "32", ["NETWORK"] = "Libera.Chat" }));

        var tok = _model.Apply(s => s).Servers[Server].IsupportTokens;
        Assert.Equal("32", tok["MAXNICKLEN"]);       // later value wins
        Assert.Equal("Libera.Chat", tok["NETWORK"]); // new key added
    }

    // ---------------------------------------------------------------------------
    // Capabilities
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task CapabilityNegotiated_ReplacesActiveCaps()
    {
        await ConnectAsync();
        await Pub(new CapabilityNegotiated(Server, ["server-time", "away-notify"], []));
        await Pub(new CapabilityNegotiated(Server, ["echo-message"], []));   // second negotiation

        var caps = _model.CreateQuery().GetActiveCapabilities(Server);
        Assert.Contains("echo-message", caps);
        Assert.DoesNotContain("server-time", caps);
    }

    [Fact]
    public async Task ServerCapabilityChanged_AddsCap()
    {
        await ConnectAsync();
        await Pub(new ServerCapabilityChanged(Server, Added: ["labeled-response"], Removed: []));

        Assert.Contains("labeled-response", _model.CreateQuery().GetActiveCapabilities(Server));
    }

    [Fact]
    public async Task ServerCapabilityChanged_RemovesCap()
    {
        await ConnectAsync();
        await Pub(new CapabilityNegotiated(Server, ["server-time", "away-notify"], []));
        await Pub(new ServerCapabilityChanged(Server, Added: [], Removed: ["server-time"]));

        var caps = _model.CreateQuery().GetActiveCapabilities(Server);
        Assert.DoesNotContain("server-time", caps);
        Assert.Contains("away-notify", caps);
    }

    // ---------------------------------------------------------------------------
    // Nick changes
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task NickChanged_UpdatesLocalNick()
    {
        await ConnectAsync("nick1");
        await Pub(new NickChanged(Server, OldNick: "nick1", NewNick: "nick2"));

        Assert.Equal("nick2", _model.CreateQuery().GetCurrentNick(Server));
    }

    [Fact]
    public async Task NickChanged_UpdatesNickInAllChannels()
    {
        await ConnectAsync();
        await Pub(new JoinedChannel(Server, "#chan", "TestUser", null));
        await Pub(new JoinedChannel(Server, "#chan", "alice", null));
        await Pub(new NickChanged(Server, OldNick: "alice", NewNick: "alice2"));

        var users = _model.CreateQuery().GetChannelUsers(Server, "#chan");
        Assert.Contains(users, u => u.Nick == "alice2");
        Assert.DoesNotContain(users, u => u.Nick == "alice");
    }

    [Fact]
    public async Task NickChanged_NonLocalNick_DoesNotChangeLocalNick()
    {
        await ConnectAsync("TestUser");
        await Pub(new JoinedChannel(Server, "#chan", "TestUser", null));
        await Pub(new JoinedChannel(Server, "#chan", "alice",    null));
        await Pub(new NickChanged(Server, OldNick: "alice", NewNick: "alice2"));

        Assert.Equal("TestUser", _model.CreateQuery().GetCurrentNick(Server));
    }

    // ---------------------------------------------------------------------------
    // Channel membership
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task JoinedChannel_SelfJoin_CreatesChannelEntry()
    {
        await ConnectAsync();
        await Pub(new JoinedChannel(Server, "#test", "TestUser", null));

        Assert.NotNull(_model.CreateQuery().GetChannelModes(Server, "#test"));
    }

    [Fact]
    public async Task JoinedChannel_OtherUser_AddsUserToChannel()
    {
        await ConnectAsync();
        await Pub(new JoinedChannel(Server, "#test", "TestUser", null));
        await Pub(new JoinedChannel(Server, "#test", "alice",    null));

        var users = _model.CreateQuery().GetChannelUsers(Server, "#test");
        Assert.Contains(users, u => u.Nick == "alice");
    }

    [Fact]
    public async Task JoinedChannel_ExtendedJoin_IncludesAccount()
    {
        await ConnectAsync();
        await Pub(new JoinedChannel(Server, "#test", "TestUser", null));
        await Pub(new JoinedChannel(Server, "#test", "bob", "bobsaccount"));

        var users = _model.CreateQuery().GetChannelUsers(Server, "#test");
        var bob   = Assert.Single(users, u => u.Nick == "bob");
        Assert.Equal("bobsaccount", bob.Account);
    }

    [Fact]
    public async Task PartedChannel_SelfPart_RemovesChannel()
    {
        await ConnectAsync();
        await Pub(new JoinedChannel(Server, "#test", "TestUser", null));
        await Pub(new PartedChannel(Server, "#test", "TestUser", null));

        Assert.Null(_model.CreateQuery().GetChannelModes(Server, "#test"));
    }

    [Fact]
    public async Task PartedChannel_OtherUser_RemovesUser()
    {
        await ConnectAsync();
        await JoinChannelAsync("#test");
        await Pub(new PartedChannel(Server, "#test", "alice", null));

        var users = _model.CreateQuery().GetChannelUsers(Server, "#test");
        Assert.DoesNotContain(users, u => u.Nick == "alice");
    }

    [Fact]
    public async Task KickReceived_SelfKick_RemovesChannel()
    {
        await ConnectAsync();
        await Pub(new JoinedChannel(Server, "#test", "TestUser", null));
        await Pub(new KickReceived(Server, "#test", KickedNick: "TestUser", KickerNick: "op", null));

        Assert.Null(_model.CreateQuery().GetChannelModes(Server, "#test"));
    }

    [Fact]
    public async Task KickReceived_OtherUser_RemovesUserFromChannel()
    {
        await ConnectAsync();
        await JoinChannelAsync("#test");
        await Pub(new KickReceived(Server, "#test", KickedNick: "alice", KickerNick: "TestUser", null));

        var users = _model.CreateQuery().GetChannelUsers(Server, "#test");
        Assert.DoesNotContain(users, u => u.Nick == "alice");
    }

    [Fact]
    public async Task UserQuit_RemovesUserFromAllChannels()
    {
        await ConnectAsync();
        await JoinChannelAsync("#chan1", "alice");
        await JoinChannelAsync("#chan2", "alice");
        await Pub(new UserQuit(Server, "alice", null));

        Assert.DoesNotContain(_model.CreateQuery().GetChannelUsers(Server, "#chan1"), u => u.Nick == "alice");
        Assert.DoesNotContain(_model.CreateQuery().GetChannelUsers(Server, "#chan2"), u => u.Nick == "alice");
    }

    // ---------------------------------------------------------------------------
    // Topic
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task TopicChanged_SetsTopicText()
    {
        await ConnectAsync();
        await Pub(new JoinedChannel(Server, "#test", "TestUser", null));
        await Pub(new TopicChanged(Server, "#test", "Welcome!", SetterNick: "op"));

        var topic = _model.CreateQuery().GetChannelTopic(Server, "#test");
        Assert.NotNull(topic);
        Assert.Equal("Welcome!", topic!.Text);
        Assert.Equal("op", topic.SetterNick);
    }

    [Fact]
    public async Task TopicWhoTime_UpdatesSetterAndTime()
    {
        await ConnectAsync();
        await Pub(new JoinedChannel(Server, "#test", "TestUser", null));
        await Pub(new TopicChanged(Server, "#test", "Hello", null));
        var setAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await Pub(new TopicWhoTime(Server, "#test", "setter", setAt));

        var topic = _model.CreateQuery().GetChannelTopic(Server, "#test");
        Assert.NotNull(topic);
        Assert.Equal("Hello", topic!.Text);          // text preserved
        Assert.Equal("setter", topic.SetterNick);
        Assert.Equal(setAt, topic.SetAt);
    }

    [Fact]
    public async Task ChannelCreated_SetsCreationTime()
    {
        await ConnectAsync();
        await Pub(new JoinedChannel(Server, "#test", "TestUser", null));
        var ts = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
        await Pub(new ChannelCreated(Server, "#test", ts));

        var snap = _model.Apply(s => s);
        Assert.Equal(ts, snap.Servers[Server].Channels["#test"].CreatedAt);
    }

    // ---------------------------------------------------------------------------
    // NAMES list
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task NamesListReceived_ReplacesUserListWithModeMapping()
    {
        await ConnectAsync();
        await Pub(new JoinedChannel(Server, "#test", "TestUser", null));

        var names = new List<NamesEntry>
        {
            new("TestUser", new List<char>()),  // no prefix
            new("alice",    new List<char> { '@' }),  // @ → op (default mapping)
            new("bob",      new List<char> { '+' }),  // + → voice
        };
        await Pub(new NamesListReceived(Server, "#test", names));

        var users = _model.CreateQuery().GetChannelUsers(Server, "#test");
        Assert.Equal(3, users.Count);

        var alice = Assert.Single(users, u => u.Nick == "alice");
        Assert.Contains('o', alice.ChannelModes.Flags);  // @ mapped to 'o'

        var bob = Assert.Single(users, u => u.Nick == "bob");
        Assert.Contains('v', bob.ChannelModes.Flags);    // + mapped to 'v'

        var self = Assert.Single(users, u => u.Nick == "TestUser");
        Assert.Empty(self.ChannelModes.Flags);
    }

    // ---------------------------------------------------------------------------
    // Channel modes
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ChannelModeChanged_AppliesChannelFlag()
    {
        await ConnectAsync();
        await Pub(new JoinedChannel(Server, "#test", "TestUser", null));
        await Pub(new ChannelModeChanged(Server, "#test", "+mn", [], "op"));

        var modes = _model.CreateQuery().GetChannelModes(Server, "#test");
        Assert.NotNull(modes);
        Assert.Contains('m', modes!.Flags);
        Assert.Contains('n', modes.Flags);
    }

    [Fact]
    public async Task ChannelModeChanged_AppliesUserPrefixMode()
    {
        await ConnectAsync();
        await JoinChannelAsync("#test", "alice");
        await Pub(new ChannelModeChanged(Server, "#test", "+o", ["alice"], "TestUser"));

        var modes = _model.CreateQuery().GetUserModes(Server, "#test", "alice");
        Assert.NotNull(modes);
        Assert.Contains('o', modes!.Flags);
    }

    [Fact]
    public async Task ChannelModeChanged_RemovesChannelFlag()
    {
        await ConnectAsync();
        await Pub(new JoinedChannel(Server, "#test", "TestUser", null));
        await Pub(new ChannelModeChanged(Server, "#test", "+m", [], "op"));
        await Pub(new ChannelModeChanged(Server, "#test", "-m", [], "op"));

        var modes = _model.CreateQuery().GetChannelModes(Server, "#test");
        Assert.NotNull(modes);
        Assert.DoesNotContain('m', modes!.Flags);
    }

    // ---------------------------------------------------------------------------
    // WHO / WHOIS user-info backfill
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task WhoReplyEntry_UpdatesUserInfoInAllChannels()
    {
        await ConnectAsync();
        await JoinChannelAsync("#chan1", "alice");
        await JoinChannelAsync("#chan2", "alice");
        await Pub(new WhoReplyEntry(Server, "#chan1", "alice", "a_user", "a.host", "AliceAccount", "Alice Real"));

        foreach (var channel in new[] { "#chan1", "#chan2" })
        {
            var users = _model.CreateQuery().GetChannelUsers(Server, channel);
            var alice = Assert.Single(users, u => u.Nick == "alice");
            Assert.Equal("a_user", alice.User);
            Assert.Equal("a.host", alice.Host);
            Assert.Equal("AliceAccount", alice.Account);
            Assert.Equal("Alice Real", alice.RealName);
        }
    }

    [Fact]
    public async Task WhoIsReply_UpdatesUserInfoInAllChannels()
    {
        await ConnectAsync();
        await JoinChannelAsync("#test", "alice");
        await Pub(new WhoIsReply(Server, "alice", "auser", "ahost", "Alice Real",
            "irc.libera.chat", IdleSeconds: 30, Account: "aliceacc"));

        var alice = Assert.Single(_model.CreateQuery().GetChannelUsers(Server, "#test"),
            u => u.Nick == "alice");
        Assert.Equal("auser",    alice.User);
        Assert.Equal("ahost",    alice.Host);
        Assert.Equal("aliceacc", alice.Account);
        Assert.Equal("Alice Real", alice.RealName);
    }

    // ---------------------------------------------------------------------------
    // User metadata (CHGHOST, away-notify, account-notify, setname)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task UserHostChanged_UpdatesUserAndHostInAllChannels()
    {
        await ConnectAsync();
        await JoinChannelAsync("#chan1", "alice");
        await JoinChannelAsync("#chan2", "alice");
        await Pub(new UserHostChanged(Server, "alice", "newuser", "new.host"));

        foreach (var ch in new[] { "#chan1", "#chan2" })
        {
            var alice = Assert.Single(_model.CreateQuery().GetChannelUsers(Server, ch), u => u.Nick == "alice");
            Assert.Equal("newuser",  alice.User);
            Assert.Equal("new.host", alice.Host);
        }
    }

    [Fact]
    public async Task UserAwayChanged_SetsAwayMessage()
    {
        await ConnectAsync();
        await JoinChannelAsync("#test", "alice");
        await Pub(new UserAwayChanged(Server, "alice", IsAway: true, "Be right back"));

        var alice = Assert.Single(_model.CreateQuery().GetChannelUsers(Server, "#test"), u => u.Nick == "alice");
        Assert.Equal("Be right back", alice.AwayMessage);
    }

    [Fact]
    public async Task UserAwayChanged_ClearsAwayMessageOnReturn()
    {
        await ConnectAsync();
        await JoinChannelAsync("#test", "alice");
        await Pub(new UserAwayChanged(Server, "alice", IsAway: true,  "BRB"));
        await Pub(new UserAwayChanged(Server, "alice", IsAway: false, null));

        var alice = Assert.Single(_model.CreateQuery().GetChannelUsers(Server, "#test"), u => u.Nick == "alice");
        Assert.Null(alice.AwayMessage);
    }

    [Fact]
    public async Task UserAccountChanged_UpdatesAccount()
    {
        await ConnectAsync();
        await JoinChannelAsync("#test", "alice");
        await Pub(new UserAccountChanged(Server, "alice", "newacct"));

        var alice = Assert.Single(_model.CreateQuery().GetChannelUsers(Server, "#test"), u => u.Nick == "alice");
        Assert.Equal("newacct", alice.Account);
    }

    [Fact]
    public async Task UserAccountChanged_NullAccount_ClearsLogin()
    {
        await ConnectAsync();
        await JoinChannelAsync("#test", "alice");
        await Pub(new UserAccountChanged(Server, "alice", "acct"));
        await Pub(new UserAccountChanged(Server, "alice", null));   // ACCOUNT * -> logged out

        var alice = Assert.Single(_model.CreateQuery().GetChannelUsers(Server, "#test"), u => u.Nick == "alice");
        Assert.Null(alice.Account);
    }

    [Fact]
    public async Task UserRealNameChanged_UpdatesRealName()
    {
        await ConnectAsync();
        await JoinChannelAsync("#test", "alice");
        await Pub(new UserRealNameChanged(Server, "alice", "Alice Wonderland"));

        var alice = Assert.Single(_model.CreateQuery().GetChannelUsers(Server, "#test"), u => u.Nick == "alice");
        Assert.Equal("Alice Wonderland", alice.RealName);
    }

    // ---------------------------------------------------------------------------
    // MONITOR
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task MonitorStatusChanged_AddsNewEntry()
    {
        await ConnectAsync();
        await Pub(new MonitorStatusChanged(Server, "bob", IsOnline: true));

        var snap = _model.Apply(s => s);
        Assert.Single(snap.Servers[Server].MonitoredNicks, m => m.Nick == "bob" && m.IsOnline);
    }

    [Fact]
    public async Task MonitorStatusChanged_UpdatesExistingEntry()
    {
        await ConnectAsync();
        await Pub(new MonitorStatusChanged(Server, "bob", IsOnline: true));
        await Pub(new MonitorStatusChanged(Server, "bob", IsOnline: false));

        var snap = _model.Apply(s => s);
        Assert.Single(snap.Servers[Server].MonitoredNicks, m => m.Nick == "bob" && !m.IsOnline);
    }

    // ---------------------------------------------------------------------------
    // Cross-server isolation
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task EventsFromOtherServer_AreIgnored()
    {
        await Pub(new ConnectionAttempted(Server, "irc.libera.chat", 6667, false));
        await Pub(new ConnectionEstablished(Server));
        await Pub(new WelcomeReceived(Server, "me"));

        // Publish everything on the other server ID.
        await Pub(new WelcomeReceived(OtherServer, "imposter"));
        await Pub(new JoinedChannel(OtherServer, "#test", "me", null));

        // The libera state should be unaffected.
        Assert.Equal("me", _model.CreateQuery().GetCurrentNick(Server));
        Assert.Empty(_model.CreateQuery().GetChannelUsers(Server, "#test"));
    }

    // ---------------------------------------------------------------------------
    // Snapshot isolation
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Snapshot_CapturedBeforeEvent_IsNotAffectedBySubsequentEvent()
    {
        await ConnectAsync("nick1");
        var oldQuery = _model.CreateQuery();

        await Pub(new NickChanged(Server, "nick1", "nick2"));

        Assert.Equal("nick1", oldQuery.GetCurrentNick(Server));   // stale snapshot unchanged
        Assert.Equal("nick2", _model.CreateQuery().GetCurrentNick(Server));
    }
}
