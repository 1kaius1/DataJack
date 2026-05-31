// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.State;
using Xunit;

namespace DataJack.Core.Tests;

public sealed class IrcStateModelTests
{
    private readonly IRCStateModel _model = new();

    [Fact]
    public void InitialSnapshot_HasNoServers()
    {
        var query = _model.CreateQuery();
        Assert.False(query.IsConnected("libera"));
        Assert.Null(query.GetCurrentNick("libera"));
        Assert.Empty(query.GetChannelUsers("libera", "#test"));
        Assert.Empty(query.GetActiveCapabilities("libera"));
    }

    [Fact]
    public void Apply_ReplacesSnapshotWithMutationResult()
    {
        var server = ServerState.CreateDisconnected("libera", "irc.libera.chat", 6697, true);

        _model.Apply(s => s with
        {
            Servers = new Dictionary<string, ServerState>(s.Servers) { [server.Id] = server }
        });

        var query = _model.CreateQuery();
        Assert.False(query.IsConnected("libera"));
        Assert.Null(query.GetCurrentNick("libera"));
    }

    [Fact]
    public void Apply_ConnectedState_IsReflectedInQuery()
    {
        var connected = ServerState.CreateDisconnected("libera", "irc.libera.chat", 6697, true) with
        {
            IsConnected    = true,
            ConnectedAt    = DateTimeOffset.UtcNow,
            RegisteredNick = "testuser",
        };

        _model.Apply(s => s with
        {
            Servers = new Dictionary<string, ServerState>(s.Servers) { [connected.Id] = connected }
        });

        var query = _model.CreateQuery();
        Assert.True(query.IsConnected("libera"));
        Assert.Equal("testuser", query.GetCurrentNick("libera"));
    }

    [Fact]
    public void CreateQuery_ReturnedQueryIsIsolatedFromSubsequentApply()
    {
        var initial = ServerState.CreateDisconnected("libera", "irc.libera.chat", 6697, true) with
        {
            IsConnected    = true,
            RegisteredNick = "nick1",
        };
        _model.Apply(s => s with
        {
            Servers = new Dictionary<string, ServerState>(s.Servers) { [initial.Id] = initial }
        });

        // Capture a query against the "nick1" snapshot.
        var oldQuery = _model.CreateQuery();
        Assert.Equal("nick1", oldQuery.GetCurrentNick("libera"));

        // Mutate to "nick2".
        var updated = initial with { RegisteredNick = "nick2" };
        _model.Apply(s => s with
        {
            Servers = new Dictionary<string, ServerState>(s.Servers) { [updated.Id] = updated }
        });

        // The old query still sees "nick1"; the new query sees "nick2".
        Assert.Equal("nick1", oldQuery.GetCurrentNick("libera"));
        Assert.Equal("nick2", _model.CreateQuery().GetCurrentNick("libera"));
    }

    [Fact]
    public void GetChannelUsers_UnknownServerOrChannel_ReturnsEmpty()
    {
        var query = _model.CreateQuery();
        Assert.Empty(query.GetChannelUsers("unknown", "#test"));

        var server = ServerState.CreateDisconnected("libera", "irc.libera.chat", 6697, true);
        _model.Apply(s => s with
        {
            Servers = new Dictionary<string, ServerState>(s.Servers) { [server.Id] = server }
        });

        Assert.Empty(_model.CreateQuery().GetChannelUsers("libera", "#nosuch"));
    }
}
