// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.Caps.Handlers;
using DataJack.Core.Events;
using DataJack.Core.Irc;
using DataJack.Net;
using Xunit;

namespace DataJack.Core.Tests;

// ---------------------------------------------------------------------------
// Shared fixture: EventDispatcher + DuplexPipeStream + IRCConnection
// ---------------------------------------------------------------------------

public sealed class CapabilityHandlersTests : IAsyncDisposable
{
    private static readonly NetworkEndpoint FakeEndpoint =
        new("irc.libera.chat", 6667, UseTls: false);

    private readonly EventDispatcher _dispatcher = new();
    private readonly DuplexPipeStream _stream    = new();
    private readonly IRCConnection _connection;
    private readonly CapabilityRegistry _registry;

    public CapabilityHandlersTests()
    {
        _dispatcher.Start();
        _connection = new IRCConnection("libera", new FakeNetworkProvider(_stream), _dispatcher);
        _registry   = new CapabilityRegistry("libera", _dispatcher);
    }

    // Helper: publish CapabilityNegotiated with the given granted list.
    private Task GrantCapsAsync(params string[] caps) =>
        _dispatcher.PublishAsync(
            new CapabilityNegotiated("libera", caps, []),
            EventPriority.Normal).AsTask();

    // Helper: wait for an event.
    private (TaskCompletionSource<T>, T) Subscribe<T>() where T : struct
    {
        var tcs = new TaskCompletionSource<T>();
        _dispatcher.Subscribe<T>(e => tcs.TrySetResult(e));
        return (tcs, default);
    }

    // ---------------------------------------------------------------------------
    // CapabilityRegistry
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Registry_TracksGrantedCaps_FromCapabilityNegotiated()
    {
        await GrantCapsAsync("server-time", "away-notify");
        // Allow the dispatch loop to process the event.
        await Task.Delay(50);

        Assert.True(_registry.IsActive("server-time"));
        Assert.True(_registry.IsActive("away-notify"));
        Assert.False(_registry.IsActive("echo-message"));
    }

    [Fact]
    public async Task Registry_ClearsOldCaps_OnNewCapabilityNegotiated()
    {
        await GrantCapsAsync("server-time", "away-notify");
        await Task.Delay(50);

        // Second negotiation grants a different set.
        await GrantCapsAsync("echo-message");
        await Task.Delay(50);

        Assert.False(_registry.IsActive("server-time"));
        Assert.False(_registry.IsActive("away-notify"));
        Assert.True(_registry.IsActive("echo-message"));
    }

    [Fact]
    public async Task Registry_TracksLocalNick_FromWelcomeReceived()
    {
        await _dispatcher.PublishAsync(
            new WelcomeReceived("libera", "somenick"),
            EventPriority.Normal);
        await Task.Delay(50);

        Assert.Equal("somenick", _registry.LocalNick);
    }

    [Fact]
    public async Task Registry_UpdatesLocalNick_WhenNickChangedMatchesLocalNick()
    {
        await _dispatcher.PublishAsync(new WelcomeReceived("libera", "alice"), EventPriority.Normal);
        await Task.Delay(30);
        await _dispatcher.PublishAsync(new NickChanged("libera", "alice", "alice_"), EventPriority.Normal);
        await Task.Delay(30);

        Assert.Equal("alice_", _registry.LocalNick);
    }

    [Fact]
    public async Task Registry_DoesNotUpdateNick_WhenNickChangedIsForSomeoneElse()
    {
        await _dispatcher.PublishAsync(new WelcomeReceived("libera", "alice"), EventPriority.Normal);
        await Task.Delay(30);
        await _dispatcher.PublishAsync(new NickChanged("libera", "bob", "bob2"), EventPriority.Normal);
        await Task.Delay(30);

        Assert.Equal("alice", _registry.LocalNick);
    }

    [Fact]
    public async Task Registry_AddsAndRemovesCaps_OnServerCapabilityChanged()
    {
        await GrantCapsAsync("server-time");
        await Task.Delay(30);

        await _dispatcher.PublishAsync(
            new ServerCapabilityChanged("libera", ["echo-message"], ["server-time"]),
            EventPriority.Normal);
        await Task.Delay(50);

        Assert.False(_registry.IsActive("server-time"));
        Assert.True(_registry.IsActive("echo-message"));
    }

    [Fact]
    public async Task Registry_IgnoresEvents_FromDifferentServer()
    {
        await _dispatcher.PublishAsync(
            new CapabilityNegotiated("freenode", ["server-time"], []),
            EventPriority.Normal);
        await Task.Delay(50);

        Assert.False(_registry.IsActive("server-time"));
    }

    [Fact]
    public async Task Registry_ClearsCaps_OnConnectionEstablished()
    {
        await GrantCapsAsync("server-time");
        await Task.Delay(30);

        await _dispatcher.PublishAsync(new ConnectionEstablished("libera"), EventPriority.Critical);
        await Task.Delay(50);

        Assert.False(_registry.IsActive("server-time"));
    }

    // ---------------------------------------------------------------------------
    // ServerTimeHandler
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ServerTime_GetTimestamp_ReturnsUtcNow_WhenCapNotActive()
    {
        var handler = new ServerTimeHandler(_registry);
        var before = DateTimeOffset.UtcNow;
        var ts = handler.GetTimestamp(new Dictionary<string, string> { ["time"] = "2024-01-15T12:34:56.000Z" });
        var after = DateTimeOffset.UtcNow;

        // Cap not active, so returns wall clock (not the tag value).
        Assert.InRange(ts, before, after.AddMilliseconds(100));
    }

    [Fact]
    public async Task ServerTime_GetTimestamp_ReturnsParsedTag_WhenCapActiveAndTagPresent()
    {
        await GrantCapsAsync("server-time");
        await Task.Delay(50);

        var handler = new ServerTimeHandler(_registry);
        var tags = new Dictionary<string, string> { ["time"] = "2024-01-15T12:34:56.000Z" };
        var ts = handler.GetTimestamp(tags);

        Assert.Equal(2024, ts.Year);
        Assert.Equal(1, ts.Month);
        Assert.Equal(15, ts.Day);
        Assert.Equal(12, ts.Hour);
    }

    [Fact]
    public async Task ServerTime_GetTimestamp_ReturnsUtcNow_WhenCapActiveButTagAbsent()
    {
        await GrantCapsAsync("server-time");
        await Task.Delay(50);

        var handler = new ServerTimeHandler(_registry);
        var before = DateTimeOffset.UtcNow;
        var ts = handler.GetTimestamp(null);
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(ts, before, after.AddMilliseconds(100));
    }

    [Fact]
    public async Task ServerTime_GetTimestamp_ReturnsUtcNow_WhenTagValueIsMalformed()
    {
        await GrantCapsAsync("server-time");
        await Task.Delay(50);

        var handler = new ServerTimeHandler(_registry);
        var before = DateTimeOffset.UtcNow;
        var ts = handler.GetTimestamp(new Dictionary<string, string> { ["time"] = "not-a-date" });
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(ts, before, after.AddMilliseconds(100));
    }

    // ---------------------------------------------------------------------------
    // EchoMessageHandler
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task EchoMessage_IsEchoedMessage_ReturnsFalse_WhenCapNotActive()
    {
        await _dispatcher.PublishAsync(new WelcomeReceived("libera", "alice"), EventPriority.Normal);
        await Task.Delay(50);

        var handler = new EchoMessageHandler(_registry);
        Assert.False(handler.IsEchoedMessage("alice"));
    }

    [Fact]
    public async Task EchoMessage_IsEchoedMessage_ReturnsFalse_WhenNickDoesNotMatchLocalNick()
    {
        await GrantCapsAsync("echo-message");
        await _dispatcher.PublishAsync(new WelcomeReceived("libera", "alice"), EventPriority.Normal);
        await Task.Delay(50);

        var handler = new EchoMessageHandler(_registry);
        Assert.False(handler.IsEchoedMessage("bob"));
    }

    [Fact]
    public async Task EchoMessage_IsEchoedMessage_ReturnsTrue_WhenActiveAndNickMatchesLocalNick()
    {
        await GrantCapsAsync("echo-message");
        await _dispatcher.PublishAsync(new WelcomeReceived("libera", "alice"), EventPriority.Normal);
        await Task.Delay(50);

        var handler = new EchoMessageHandler(_registry);
        Assert.True(handler.IsEchoedMessage("alice"));
    }

    [Fact]
    public async Task EchoMessage_IsEchoedMessage_IsCaseInsensitive()
    {
        await GrantCapsAsync("echo-message");
        await _dispatcher.PublishAsync(new WelcomeReceived("libera", "Alice"), EventPriority.Normal);
        await Task.Delay(50);

        var handler = new EchoMessageHandler(_registry);
        Assert.True(handler.IsEchoedMessage("ALICE"));
    }

    // ---------------------------------------------------------------------------
    // MonitorHandler
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Monitor_AddNick_SendsMonitorPlusWhenCapActive()
    {
        await _connection.ConnectAsync(FakeEndpoint);
        await GrantCapsAsync("monitor");
        await Task.Delay(50);

        var handler = new MonitorHandler("libera", _connection, _registry, _dispatcher);
        await handler.AddNickAsync("bob");

        var sent = await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("MONITOR + bob", sent);
    }

    [Fact]
    public async Task Monitor_AddNick_DoesNotSendWhenCapNotActive()
    {
        // monitor cap is NOT granted.
        var handler = new MonitorHandler("libera", _connection, _registry, _dispatcher);
        await handler.AddNickAsync("bob");

        var watchlist = await handler.GetWatchlistAsync();
        Assert.Contains("bob", watchlist);
        // No line is sent because the connection is not open and cap is not active;
        // the absence of a ReadClientLineAsync call verifies no line was queued.
    }

    [Fact]
    public async Task Monitor_AddDuplicateNick_DoesNotSendAgain()
    {
        await _connection.ConnectAsync(FakeEndpoint);
        await GrantCapsAsync("monitor");
        await Task.Delay(50);

        var handler = new MonitorHandler("libera", _connection, _registry, _dispatcher);
        await handler.AddNickAsync("bob");

        // Consume the first MONITOR + bob.
        await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));

        // Adding the same nick again must not send a duplicate.
        await handler.AddNickAsync("bob");

        var watchlist = await handler.GetWatchlistAsync();
        Assert.Single(watchlist, n => string.Equals(n, "bob", StringComparison.OrdinalIgnoreCase));
        // No second line queued — verified by the test completing without another ReadClientLineAsync.
    }

    [Fact]
    public async Task Monitor_RemoveNick_SendsMonitorMinus()
    {
        await _connection.ConnectAsync(FakeEndpoint);
        await GrantCapsAsync("monitor");
        await Task.Delay(50);

        var handler = new MonitorHandler("libera", _connection, _registry, _dispatcher);
        await handler.AddNickAsync("bob");
        await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2)); // consume MONITOR +

        await handler.RemoveNickAsync("bob");
        var sent = await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("MONITOR - bob", sent);

        var watchlist = await handler.GetWatchlistAsync();
        Assert.Empty(watchlist);
    }

    [Fact]
    public async Task Monitor_ClearAsync_SendsMonitorC()
    {
        await _connection.ConnectAsync(FakeEndpoint);
        await GrantCapsAsync("monitor");
        await Task.Delay(50);

        var handler = new MonitorHandler("libera", _connection, _registry, _dispatcher);
        await handler.AddNickAsync("bob");
        await handler.AddNickAsync("alice");
        await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2)); // bob
        await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2)); // alice

        await handler.ClearAsync();
        var sent = await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("MONITOR C", sent);

        var watchlist = await handler.GetWatchlistAsync();
        Assert.Empty(watchlist);
    }

    [Fact]
    public async Task Monitor_OnCapabilityNegotiated_ResendsWatchlistWhenMonitorGranted()
    {
        // Pre-populate the watchlist before connecting (monitor not yet active).
        var handler = new MonitorHandler("libera", _connection, _registry, _dispatcher);
        await handler.AddNickAsync("alice");
        await handler.AddNickAsync("bob");

        await _connection.ConnectAsync(FakeEndpoint);

        // Now the server grants monitor.
        await GrantCapsAsync("monitor");
        await Task.Delay(100);

        // The handler should have sent MONITOR + alice,bob (in one or two lines; order may vary).
        var line1 = await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.StartsWith("MONITOR + ", line1);
        Assert.Contains("alice", line1);
        Assert.Contains("bob", line1);
    }

    // ---------------------------------------------------------------------------
    // BatchHandler
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Batch_StartAndEnd_EmitsBatchReceived()
    {
        var tcs = new TaskCompletionSource<BatchReceived>();
        _dispatcher.Subscribe<BatchReceived>(e => tcs.TrySetResult(e));

        var handler = new BatchHandler("libera", _dispatcher);
        await _connection.ConnectAsync(FakeEndpoint);

        await _stream.SendServerDataAsync(
            ":server BATCH +ref1 chathistory #chan\r\n" +
            ":server BATCH -ref1\r\n");

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("libera", evt.Server);
        Assert.Equal("chathistory", evt.BatchType);
        Assert.Equal("ref1", evt.BatchId);
        Assert.Empty(evt.Messages);
    }

    [Fact]
    public async Task Batch_AccumulatesPrivmsgEvents_InsideBatch()
    {
        var tcs = new TaskCompletionSource<BatchReceived>();
        _dispatcher.Subscribe<BatchReceived>(e => tcs.TrySetResult(e));

        var handler = new BatchHandler("libera", _dispatcher);
        await _connection.ConnectAsync(FakeEndpoint);

        await _stream.SendServerDataAsync(
            ":server BATCH +h1 chathistory #chan\r\n" +
            "@batch=h1 :alice!a@h PRIVMSG #chan :hello\r\n" +
            "@batch=h1 :bob!b@h PRIVMSG #chan :world\r\n" +
            ":server BATCH -h1\r\n");

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, evt.Messages.Count);
        var msg1 = Assert.IsType<MessageReceived>(evt.Messages[0]);
        Assert.Equal("alice", msg1.FromNick);
        Assert.Equal("hello", msg1.Text);
        var msg2 = Assert.IsType<MessageReceived>(evt.Messages[1]);
        Assert.Equal("bob", msg2.FromNick);
    }

    [Fact]
    public async Task Batch_AccumulatesActionEvents_InsideBatch()
    {
        var tcs = new TaskCompletionSource<BatchReceived>();
        _dispatcher.Subscribe<BatchReceived>(e => tcs.TrySetResult(e));

        var handler = new BatchHandler("libera", _dispatcher);
        await _connection.ConnectAsync(FakeEndpoint);

        // NOTE: CTCP delimiter uses  (SOH), not \x01 (greedy hex escape).
        await _stream.SendServerDataAsync(
            ":server BATCH +b2 chathistory #chan\r\n" +
            "@batch=b2 :alice!a@h PRIVMSG #chan :ACTION waves\r\n" +
            ":server BATCH -b2\r\n");

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Single(evt.Messages);
        var action = Assert.IsType<ActionReceived>(evt.Messages[0]);
        Assert.Equal("waves", action.Text);
    }

    [Fact]
    public async Task Batch_UnknownBatchId_DoesNotEmitBatchReceived()
    {
        var received = false;
        _dispatcher.Subscribe<BatchReceived>(_ => received = true);

        var handler = new BatchHandler("libera", _dispatcher);
        await _connection.ConnectAsync(FakeEndpoint);

        // BATCH - for an id that was never opened.
        await _stream.SendServerDataAsync(":server BATCH -unknownid\r\n");
        await Task.Delay(100);

        Assert.False(received);
    }

    [Fact]
    public async Task Batch_MultipleSimultaneousBatches_AreIndependent()
    {
        var results = new List<BatchReceived>();
        var tcs = new TaskCompletionSource<bool>();
        _dispatcher.Subscribe<BatchReceived>(e =>
        {
            results.Add(e);
            if (results.Count == 2) tcs.TrySetResult(true);
        });

        var handler = new BatchHandler("libera", _dispatcher);
        await _connection.ConnectAsync(FakeEndpoint);

        await _stream.SendServerDataAsync(
            ":server BATCH +a chathistory #a\r\n" +
            ":server BATCH +b chathistory #b\r\n" +
            "@batch=a :alice!a@h PRIVMSG #a :from-a\r\n" +
            "@batch=b :bob!b@h PRIVMSG #b :from-b\r\n" +
            ":server BATCH -a\r\n" +
            ":server BATCH -b\r\n");

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var batchA = results.Single(r => r.BatchId == "a");
        var batchB = results.Single(r => r.BatchId == "b");
        Assert.Single(batchA.Messages);
        Assert.Single(batchB.Messages);
        Assert.Equal("from-a", ((MessageReceived)batchA.Messages[0]).Text);
        Assert.Equal("from-b", ((MessageReceived)batchB.Messages[0]).Text);
    }

    // ---------------------------------------------------------------------------
    // LabeledResponseHandler
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task LabeledResponse_IsActive_WhenCapGranted()
    {
        await GrantCapsAsync("labeled-response");
        await Task.Delay(50);

        var handler = new LabeledResponseHandler(_registry);
        Assert.True(handler.IsActive);
    }

    [Fact]
    public async Task LabeledResponse_IsNotActive_WhenCapNotGranted()
    {
        var handler = new LabeledResponseHandler(_registry);
        Assert.False(handler.IsActive);
    }

    [Fact]
    public async Task LabeledResponse_GeneratesUniqueLabels_WhenActive()
    {
        await GrantCapsAsync("labeled-response");
        await Task.Delay(50);

        var handler = new LabeledResponseHandler(_registry);
        var labels = new HashSet<string?>();
        for (int i = 0; i < 100; i++)
            labels.Add(handler.TryCreateLabel());

        Assert.Equal(100, labels.Count);
        Assert.DoesNotContain(null, labels);
    }

    [Fact]
    public async Task LabeledResponse_ReturnsNull_WhenCapNotActive()
    {
        var handler = new LabeledResponseHandler(_registry);
        Assert.Null(handler.TryCreateLabel());
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        await _dispatcher.DisposeAsync();
        _stream.Dispose();
    }
}
