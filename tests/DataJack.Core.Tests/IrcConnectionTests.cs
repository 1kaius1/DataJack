// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.Events;
using DataJack.Core.Irc;
using DataJack.Net;
using Xunit;

namespace DataJack.Core.Tests;

public sealed class IrcConnectionTests : IAsyncDisposable
{
    private readonly EventDispatcher _dispatcher = new();
    private readonly DuplexPipeStream _stream = new();
    private readonly IRCConnection _connection;
    private static readonly NetworkEndpoint FakeEndpoint =
        new("irc.libera.chat", 6667, UseTls: false);

    public IrcConnectionTests()
    {
        _dispatcher.Start();
        _connection = new IRCConnection("libera", new FakeNetworkProvider(_stream), _dispatcher);
    }

    // ---------------------------------------------------------------------------
    // TryParsePing — pure logic, no network
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData("PING :irc.libera.chat", "irc.libera.chat")]
    [InlineData("PING irc.libera.chat",  "irc.libera.chat")]
    [InlineData(":irc.libera.chat PING :irc.libera.chat", "irc.libera.chat")]
    [InlineData("PING :",               "")]
    [InlineData("PING",                 "")]
    public void TryParsePing_ValidPingLine_ExtractsToken(string line, string expectedToken)
    {
        Assert.True(IRCConnection.TryParsePing(line, out var token));
        Assert.Equal(expectedToken, token);
    }

    [Theory]
    [InlineData(":irc.libera.chat 001 nick :Welcome")]
    [InlineData(":alice PRIVMSG #test :hello")]
    [InlineData("")]
    [InlineData(":server NOTICE * :hello")]
    public void TryParsePing_NonPingLine_ReturnsFalse(string line)
    {
        Assert.False(IRCConnection.TryParsePing(line, out _));
    }

    // ---------------------------------------------------------------------------
    // Integration tests via DuplexPipeStream
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ConnectAsync_PublishesConnectionAttemptedThenEstablished()
    {
        var attempted = new TaskCompletionSource<ConnectionAttempted>();
        var established = new TaskCompletionSource<ConnectionEstablished>();
        _dispatcher.Subscribe<ConnectionAttempted>(e => attempted.TrySetResult(e));
        _dispatcher.Subscribe<ConnectionEstablished>(e => established.TrySetResult(e));

        await _connection.ConnectAsync(FakeEndpoint);

        var a = await attempted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var e = await established.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("libera", a.Server);
        Assert.Equal("irc.libera.chat", a.Address);
        Assert.Equal("libera", e.Server);
    }

    [Fact]
    public async Task ReceiveLoop_PublishesRawLineReceivedForEachLine()
    {
        var received = new List<string>();
        var tcs = new TaskCompletionSource();
        _dispatcher.Subscribe<RawLineReceived>(e =>
        {
            received.Add(e.Line);
            if (received.Count >= 2) tcs.TrySetResult();
        });

        await _connection.ConnectAsync(FakeEndpoint);
        await _stream.SendServerDataAsync(":server 001 nick :Welcome\r\n:server 002 nick :more\r\n");

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, received.Count);
        Assert.Equal(":server 001 nick :Welcome", received[0]);
        Assert.Equal(":server 002 nick :more", received[1]);
    }

    [Fact]
    public async Task ReceiveLoop_AutoRespondsWithPong_OnPing()
    {
        await _connection.ConnectAsync(FakeEndpoint);
        await _stream.SendServerDataAsync("PING :irc.libera.chat\r\n");

        // Read past the PONG line the connection sends back
        var sent = await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("PONG :irc.libera.chat", sent);
    }

    [Fact]
    public async Task ReceiveLoop_PublishesRawLineReceived_EvenForPingLines()
    {
        var tcs = new TaskCompletionSource<RawLineReceived>();
        _dispatcher.Subscribe<RawLineReceived>(e => tcs.TrySetResult(e));

        await _connection.ConnectAsync(FakeEndpoint);
        await _stream.SendServerDataAsync("PING :irc.libera.chat\r\n");

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("PING :irc.libera.chat", evt.Line);
    }

    [Fact]
    public async Task ReceiveLoop_PublishesConnectionClosed_WhenServerClosesStream()
    {
        var tcs = new TaskCompletionSource<ConnectionClosed>();
        _dispatcher.Subscribe<ConnectionClosed>(e => tcs.TrySetResult(e));

        await _connection.ConnectAsync(FakeEndpoint);
        _stream.CloseServer();

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("libera", evt.Server);
    }

    [Fact]
    public async Task SendLineAsync_WritesLineThenPublishesRawLineSent()
    {
        var tcs = new TaskCompletionSource<RawLineSent>();
        _dispatcher.Subscribe<RawLineSent>(e => tcs.TrySetResult(e));

        await _connection.ConnectAsync(FakeEndpoint);
        await _connection.SendLineAsync("JOIN #test");

        var sent = await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("JOIN #test", sent);

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("JOIN #test", evt.Line);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        await _dispatcher.DisposeAsync();
        _stream.Dispose();
    }
}
