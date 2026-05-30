// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.Events;
using Xunit;

namespace DataJack.Core.Tests;

public sealed class EventDispatcherTests : IAsyncDisposable
{
    private readonly EventDispatcher _dispatcher = new();

    [Fact]
    public async Task Subscribe_HandlerIsCalledWhenEventIsPublished()
    {
        _dispatcher.Start();

        var tcs = new TaskCompletionSource<MessageReceived>();
        _dispatcher.Subscribe<MessageReceived>(evt => tcs.TrySetResult(evt));

        var expected = new MessageReceived("libera", "#test", "alice", "hello", null, false);
        await _dispatcher.PublishAsync(expected);

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(expected, received);
    }

    [Fact]
    public async Task Subscribe_MultipleHandlersAreAllCalled()
    {
        _dispatcher.Start();

        int callCount = 0;
        var tcs = new TaskCompletionSource();

        _dispatcher.Subscribe<ConnectionEstablished>(_ => Interlocked.Increment(ref callCount));
        _dispatcher.Subscribe<ConnectionEstablished>(_ =>
        {
            Interlocked.Increment(ref callCount);
            tcs.TrySetResult();
        });

        await _dispatcher.PublishAsync(new ConnectionEstablished("libera"));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task Unsubscribe_HandlerIsNotCalledAfterRemoval()
    {
        _dispatcher.Start();

        int callCount = 0;
        var firstTcs = new TaskCompletionSource();
        var secondTcs = new TaskCompletionSource();

        // First handler counts calls; second signals when it runs (used to sequence the test).
        void FirstHandler(RawLineReceived _) => Interlocked.Increment(ref callCount);
        _dispatcher.Subscribe<RawLineReceived>(FirstHandler);
        _dispatcher.Subscribe<RawLineReceived>(_ => firstTcs.TrySetResult());

        await _dispatcher.PublishAsync(new RawLineReceived("libera", ":server 001 nick :Welcome"));
        await firstTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        _dispatcher.Unsubscribe<RawLineReceived>(FirstHandler);

        _dispatcher.Subscribe<RawLineReceived>(_ => secondTcs.TrySetResult());
        await _dispatcher.PublishAsync(new RawLineReceived("libera", ":server 002 nick :more"));
        await secondTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // FirstHandler ran once before unsubscribe; must not have run a second time.
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task PublishAsync_NoHandlersRegistered_DoesNotThrow()
    {
        _dispatcher.Start();

        // No subscriber for ConnectionClosed -- should be a silent no-op, not an exception.
        await _dispatcher.PublishAsync(new ConnectionClosed("libera", "Ping timeout"));
    }

    public async ValueTask DisposeAsync() => await _dispatcher.DisposeAsync();
}
