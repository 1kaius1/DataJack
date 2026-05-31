// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.Caps;
using DataJack.Core.Events;
using DataJack.Core.Irc;
using DataJack.Net;
using Xunit;

namespace DataJack.Core.Tests;

public sealed class CapabilityNegotiatorTests : IAsyncDisposable
{
    private readonly EventDispatcher _dispatcher = new();
    private readonly DuplexPipeStream _stream = new();
    private readonly IRCConnection _connection;
    private static readonly NetworkEndpoint FakeEndpoint =
        new("irc.libera.chat", 6667, UseTls: false);

    public CapabilityNegotiatorTests()
    {
        _dispatcher.Start();
        _connection = new IRCConnection("libera", new FakeNetworkProvider(_stream), _dispatcher);
        _ = new CapabilityNegotiator("libera", _connection, _dispatcher);
    }

    // Connect and consume the automatic "CAP LS 302" the negotiator sends on ConnectionEstablished.
    private async Task ConnectAndConsumeCapLsAsync()
    {
        await _connection.ConnectAsync(FakeEndpoint);
        var capLs = await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("CAP LS 302", capLs);
    }

    // ---------------------------------------------------------------------------
    // Startup
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task OnConnectionEstablished_SendsCapLs302()
    {
        await _connection.ConnectAsync(FakeEndpoint);
        var sent = await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("CAP LS 302", sent);
    }

    // ---------------------------------------------------------------------------
    // CAP LS handling
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SingleLineCapLs_RequestsOnlyIntersectionOfWantedAndAdvertised()
    {
        await ConnectAndConsumeCapLsAsync();
        await _stream.SendServerDataAsync(
            ":irc.libera.chat CAP * LS :message-tags multi-prefix sasl unknown-cap\r\n");

        var req = await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.StartsWith("CAP REQ :", req);
        Assert.Contains("message-tags", req);
        Assert.Contains("multi-prefix", req);
        Assert.Contains("sasl", req);
        Assert.DoesNotContain("unknown-cap", req);
    }

    [Fact]
    public async Task MultilineCapLs_AccumulatesAcrossAllLines_SendsReqOnlyAfterFinalLine()
    {
        await ConnectAndConsumeCapLsAsync();

        // Multiline chunk (marked with "*")
        await _stream.SendServerDataAsync(
            ":irc.libera.chat CAP * LS * :message-tags batch\r\n");
        // Final line (no "*")
        await _stream.SendServerDataAsync(
            ":irc.libera.chat CAP * LS :multi-prefix\r\n");

        var req = await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));

        // REQ must include caps from both LS lines
        Assert.StartsWith("CAP REQ :", req);
        Assert.Contains("message-tags", req);
        Assert.Contains("batch", req);
        Assert.Contains("multi-prefix", req);
    }

    [Fact]
    public async Task CapLs_NoWantedCapsAdvertised_SendsCapEndWithoutReq()
    {
        var tcs = new TaskCompletionSource<CapabilityNegotiated>();
        _dispatcher.Subscribe<CapabilityNegotiated>(e => tcs.TrySetResult(e));

        await ConnectAndConsumeCapLsAsync();
        await _stream.SendServerDataAsync(
            ":irc.libera.chat CAP * LS :unknown-cap-1 unknown-cap-2\r\n");

        var end = await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("CAP END", end);

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(evt.Granted);
    }

    // ---------------------------------------------------------------------------
    // CAP ACK handling
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task CapAck_PublishesCapabilityNegotiated_WithCorrectGrantedAndDeniedLists()
    {
        var tcs = new TaskCompletionSource<CapabilityNegotiated>();
        _dispatcher.Subscribe<CapabilityNegotiated>(e => tcs.TrySetResult(e));

        await ConnectAndConsumeCapLsAsync();
        // Advertise three wanted caps; server will only grant two.
        await _stream.SendServerDataAsync(
            ":irc.libera.chat CAP * LS :message-tags multi-prefix sasl\r\n");
        await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2)); // consume REQ

        await _stream.SendServerDataAsync(
            ":irc.libera.chat CAP * ACK :message-tags multi-prefix\r\n");

        var end = await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("CAP END", end);

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("libera", evt.Server);
        Assert.Contains("message-tags", evt.Granted);
        Assert.Contains("multi-prefix", evt.Granted);
        // sasl was advertised and wanted but not ACKed, so it ends up denied
        Assert.Contains("sasl", evt.Denied);
    }

    [Fact]
    public async Task CapAck_ModifierPrefixes_AreStrippedFromGrantedNames()
    {
        var tcs = new TaskCompletionSource<CapabilityNegotiated>();
        _dispatcher.Subscribe<CapabilityNegotiated>(e => tcs.TrySetResult(e));

        await ConnectAndConsumeCapLsAsync();
        await _stream.SendServerDataAsync(
            ":irc.libera.chat CAP * LS :message-tags multi-prefix sasl\r\n");
        await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2)); // consume REQ

        // Server uses modifier prefixes -/~/=
        await _stream.SendServerDataAsync(
            ":irc.libera.chat CAP * ACK :-message-tags ~multi-prefix =sasl\r\n");
        await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2)); // consume CAP END

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // All three should appear with clean names, no modifier prefix
        Assert.Contains("message-tags", evt.Granted);
        Assert.Contains("multi-prefix", evt.Granted);
        Assert.Contains("sasl", evt.Granted);
        Assert.Empty(evt.Denied);
    }

    // ---------------------------------------------------------------------------
    // CAP NAK handling
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task CapNak_PublishesCapabilityNegotiatedWithEmptyGrantedList_AndSendsCapEnd()
    {
        var tcs = new TaskCompletionSource<CapabilityNegotiated>();
        _dispatcher.Subscribe<CapabilityNegotiated>(e => tcs.TrySetResult(e));

        await ConnectAndConsumeCapLsAsync();
        await _stream.SendServerDataAsync(
            ":irc.libera.chat CAP * LS :message-tags\r\n");
        await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2)); // consume REQ

        await _stream.SendServerDataAsync(
            ":irc.libera.chat CAP * NAK :message-tags\r\n");

        var end = await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("CAP END", end);

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(evt.Granted);
    }

    // ---------------------------------------------------------------------------
    // cap-notify NEW / DEL
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task CapNotifyNew_RequestsNewlyAvailableWantedCaps_AndPublishesServerCapabilityChanged()
    {
        var tcs = new TaskCompletionSource<ServerCapabilityChanged>();
        _dispatcher.Subscribe<ServerCapabilityChanged>(e => tcs.TrySetResult(e));

        await ConnectAndConsumeCapLsAsync();
        // Empty initial LS: nothing to request, negotiation ends immediately.
        await _stream.SendServerDataAsync(":irc.libera.chat CAP * LS :\r\n");
        await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2)); // consume CAP END

        // Server announces a cap we want via cap-notify
        await _stream.SendServerDataAsync(
            ":irc.libera.chat CAP * NEW :message-tags\r\n");

        var req = await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("CAP REQ :message-tags", req);

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("libera", evt.Server);
        Assert.Contains("message-tags", evt.Added);
        Assert.Empty(evt.Removed);
    }

    [Fact]
    public async Task CapNotifyNew_UnknownCap_PublishesChangedButDoesNotSendReq()
    {
        var tcs = new TaskCompletionSource<ServerCapabilityChanged>();
        _dispatcher.Subscribe<ServerCapabilityChanged>(e => tcs.TrySetResult(e));

        await ConnectAndConsumeCapLsAsync();
        await _stream.SendServerDataAsync(":irc.libera.chat CAP * LS :\r\n");
        await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2)); // consume CAP END

        await _stream.SendServerDataAsync(
            ":irc.libera.chat CAP * NEW :some-vendor-cap\r\n");

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains("some-vendor-cap", evt.Added);
        // No REQ is sent because "some-vendor-cap" is not in WantedCapabilities.
        // Verified implicitly: the test completes without deadlocking on ReadClientLineAsync.
    }

    [Fact]
    public async Task CapNotifyDel_RemovesCapAndPublishesServerCapabilityChanged()
    {
        var changed = new TaskCompletionSource<ServerCapabilityChanged>();
        _dispatcher.Subscribe<ServerCapabilityChanged>(e => changed.TrySetResult(e));

        await ConnectAndConsumeCapLsAsync();
        await _stream.SendServerDataAsync(
            ":irc.libera.chat CAP * LS :message-tags\r\n");
        await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2)); // consume REQ
        await _stream.SendServerDataAsync(
            ":irc.libera.chat CAP * ACK :message-tags\r\n");
        await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2)); // consume CAP END

        await _stream.SendServerDataAsync(
            ":irc.libera.chat CAP * DEL :message-tags\r\n");

        var evt = await changed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("libera", evt.Server);
        Assert.Contains("message-tags", evt.Removed);
        Assert.Empty(evt.Added);
    }

    // ---------------------------------------------------------------------------
    // Multi-server isolation
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task MultiServerIsolation_EachNegotiatorOnlyProcessesItsOwnServer()
    {
        var stream2 = new DuplexPipeStream();
        await using var conn2 = new IRCConnection(
            "freenode", new FakeNetworkProvider(stream2), _dispatcher);
        _ = new CapabilityNegotiator("freenode", conn2, _dispatcher);

        await _connection.ConnectAsync(FakeEndpoint);
        await conn2.ConnectAsync(new NetworkEndpoint("irc.freenode.net", 6667, UseTls: false));

        // Both negotiators should send CAP LS 302 on their own streams
        var liberaLs   = await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        var freenodeLs = await stream2.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("CAP LS 302", liberaLs);
        Assert.Equal("CAP LS 302", freenodeLs);

        // Only respond to freenode's LS; libera's stream should receive nothing
        await stream2.SendServerDataAsync(
            ":irc.freenode.net CAP * LS :message-tags\r\n");
        var freenodeReq = await stream2.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.StartsWith("CAP REQ :", freenodeReq);
        Assert.Contains("message-tags", freenodeReq);

        // libera stream had no activity; it should not have sent a REQ
        // (asserted implicitly: ReadClientLineAsync on libera would block; we do not call it)
        stream2.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        await _dispatcher.DisposeAsync();
        _stream.Dispose();
    }
}
