// SPDX-License-Identifier: GPL-3.0-or-later
using System.Threading.Channels;
using DataJack.Core.Events;
using DataJack.Core.Irc;
using DataJack.Net;
using Xunit;

namespace DataJack.Core.Tests;

public sealed class FloodControllerTests : IAsyncDisposable
{
    private readonly EventDispatcher _dispatcher = new();
    private readonly DuplexPipeStream _stream    = new();
    private readonly IRCConnection _connection;
    private static readonly NetworkEndpoint FakeEndpoint =
        new("irc.libera.chat", 6667, UseTls: false);

    public FloodControllerTests()
    {
        _dispatcher.Start();
        _connection = new IRCConnection("libera", new FakeNetworkProvider(_stream), _dispatcher);
    }

    // ---------------------------------------------------------------------------
    // CalculateCost — pure logic, no network
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData(0,   1.0)]  // well under 200 bytes → base cost
    [InlineData(200, 1.0)]  // exactly 200 bytes → base cost
    [InlineData(201, 1.0)]  // first byte over 200: only 0 full extra chunks
    [InlineData(300, 1.1)]  // 100 bytes over 200 = 1 full chunk
    [InlineData(301, 1.1)]  // 101 bytes → still 1 full chunk
    [InlineData(400, 1.2)]  // 200 bytes over → 2 full chunks
    [InlineData(500, 1.3)]
    public void CalculateCost_ReturnsExpectedTokenCost(int byteCount, double expected)
    {
        // Build a line that encodes to exactly `byteCount` ASCII bytes.
        var line = new string('x', byteCount);

        Assert.Equal(expected, FloodController.CalculateCost(line), precision: 9);
    }

    // ---------------------------------------------------------------------------
    // Channel isolation — verify BoundedChannel DropWrite semantics
    // ---------------------------------------------------------------------------

    // Confirm .NET 10 BoundedChannel.Wait semantics: TryWrite returns false when full.
    // (DropWrite is NOT used in FloodController because it returns true even when the item
    //  is silently discarded, making overflow detection unreliable.)
    [Fact]
    public void BoundedChannel_WaitMode_TryWriteReturnsFalseWhenFull()
    {
        var ch = Channel.CreateBounded<string>(
            new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.Wait });

        Assert.True(ch.Writer.TryWrite("a"));
        Assert.True(ch.Writer.TryWrite("b"));
        Assert.False(ch.Writer.TryWrite("c")); // at capacity → false
    }

    // ---------------------------------------------------------------------------
    // Bypass path — no tokens consumed
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SendBypassAsync_SendsImmediately_RegardlessOfTokenBalance()
    {
        // Use a bucket of 0 tokens so any rate-limited send would block.
        await using var ctrl = new FloodController("libera", _connection, _dispatcher,
            new FloodController.Config(TokenCapacity: 0.0, TokenDrainRate: 0.0001));

        await _connection.ConnectAsync(FakeEndpoint);
        await ctrl.SendBypassAsync("PONG :irc.libera.chat");

        var line = await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("PONG :irc.libera.chat", line);
    }

    // ---------------------------------------------------------------------------
    // Burst capacity — full token bucket drains before rate limiting kicks in
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task TrySend_BurstCapacity_SendsAllLinesBeforeRateLimitingKicksIn()
    {
        // Capacity 3, drain effectively disabled → exactly 3 immediate sends.
        await using var ctrl = new FloodController("libera", _connection, _dispatcher,
            new FloodController.Config(TokenCapacity: 3.0, TokenDrainRate: 0.0001));

        await _connection.ConnectAsync(FakeEndpoint);

        for (int i = 0; i < 3; i++)
            Assert.True(ctrl.TrySend($"JOIN #chan{i}"));

        for (int i = 0; i < 3; i++)
        {
            var line = await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal($"JOIN #chan{i}", line);
        }
    }

    // ---------------------------------------------------------------------------
    // Rate limiting — second line waits for token refill
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task TrySend_AfterBurstExhausted_SecondLineArrivesAfterRefill()
    {
        // 1 token capacity, 100 tokens/sec → 1 token refills in ~10 ms.
        await using var ctrl = new FloodController("libera", _connection, _dispatcher,
            new FloodController.Config(TokenCapacity: 1.0, TokenDrainRate: 100.0));

        await _connection.ConnectAsync(FakeEndpoint);

        ctrl.TrySend("PRIVMSG #a :first");  // uses the 1 available token immediately
        ctrl.TrySend("PRIVMSG #a :second"); // no tokens; must wait ~10 ms

        var first = await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("PRIVMSG #a :first", first);

        // Second line must also arrive, but only after the refill delay.
        var second = await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("PRIVMSG #a :second", second);
    }

    // ---------------------------------------------------------------------------
    // Priority — Normal before CTCP when both are queued
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task TrySend_NormalPrecedesCtcp_WhenBothAreWaitingInQueue()
    {
        // 2-token burst. We send 2 normal lines to exhaust the burst, then queue
        // one CTCP and one Normal. The drain loop tries the Normal queue first.
        await using var ctrl = new FloodController("libera", _connection, _dispatcher,
            new FloodController.Config(TokenCapacity: 2.0, TokenDrainRate: 200.0));

        await _connection.ConnectAsync(FakeEndpoint);

        ctrl.TrySend("JOIN #setup1");
        ctrl.TrySend("JOIN #setup2");

        // Consume the two burst lines so we know the drain loop is idle.
        await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));

        // Tokens are now exhausted (or close to it). Let the drain loop return
        // to its WaitToReadAsync state before queueing the priority test items.
        await Task.Delay(15);

        // Queue CTCP first, then Normal. The drain loop must pick Normal first.
        ctrl.TrySend("NOTICE alice :\x01VERSION\x01", LineKind.Ctcp);
        ctrl.TrySend("PRIVMSG #chan :hello");

        var third  = await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        var fourth = await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("PRIVMSG #chan :hello",       third);
        Assert.Equal("NOTICE alice :\x01VERSION\x01", fourth);
    }

    // ---------------------------------------------------------------------------
    // Queue-full — emits FloodQueueFull when at capacity
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task TrySend_WhenQueueFull_EmitsFloodQueueFull()
    {
        var tcs = new TaskCompletionSource<FloodQueueFull>();
        _dispatcher.Subscribe<FloodQueueFull>(e => tcs.TrySetResult(e));

        // Strategy: burst of 3 tokens lets the drain send the first 3 items immediately.
        // After the burst, each additional token takes 1 second to refill → drain is
        // blocked in the inner while loop (sleeping 100 ms per attempt) for the duration
        // of the test. With MaxQueueDepth=3 and 15 items total, at least 9 items will
        // overflow and emit FloodQueueFull regardless of exact drain timing.
        await using var ctrl = new FloodController("libera", _connection, _dispatcher,
            new FloodController.Config(
                TokenCapacity: 3.0,
                TokenDrainRate: 1.0,
                MaxQueueDepth: 3));

        await _connection.ConnectAsync(FakeEndpoint);

        var anyDropped = false;
        for (int i = 0; i < 15; i++)
            if (!ctrl.TrySend($"PRIVMSG #a :{i}"))
                anyDropped = true;

        Assert.True(anyDropped);

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("libera", evt.Server);
        Assert.Equal(1, evt.DroppedCount);
    }

    // ---------------------------------------------------------------------------
    // FIFO within each priority tier
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task TrySend_NormalLines_AreSentInFifoOrder()
    {
        await using var ctrl = new FloodController("libera", _connection, _dispatcher,
            new FloodController.Config(TokenCapacity: 5.0, TokenDrainRate: 200.0));

        await _connection.ConnectAsync(FakeEndpoint);

        for (int i = 1; i <= 5; i++)
            ctrl.TrySend($"PRIVMSG #c :msg{i}");

        for (int i = 1; i <= 5; i++)
        {
            var line = await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal($"PRIVMSG #c :msg{i}", line);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        await _dispatcher.DisposeAsync();
        _stream.Dispose();
    }
}
