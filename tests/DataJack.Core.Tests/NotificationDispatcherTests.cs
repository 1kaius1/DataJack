// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.Events;
using DataJack.Core.State;
using DataJack.Platform.Notifications;
using Xunit;

namespace DataJack.Core.Tests;

// ---------------------------------------------------------------------------
// Fake notification service that records every call
// ---------------------------------------------------------------------------

sealed class RecordingNotificationService : INotificationService
{
    private readonly List<NotificationInfo> _received = new();

    public bool IsSupported => true;

    public IReadOnlyList<NotificationInfo> Received => _received;

    public Task NotifyAsync(NotificationInfo notification, CancellationToken ct = default)
    {
        _received.Add(notification);
        return Task.CompletedTask;
    }
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

public sealed class NotificationDispatcherTests : IAsyncDisposable
{
    private const string Server      = "libera";
    private const string OtherServer = "efnet";
    private const string MyNick      = "TestUser";
    private const string OtherNick   = "alice";
    private const string Channel     = "#general";

    private readonly EventDispatcher              _bus      = new();
    private readonly IRCStateModel                _model    = new();
    private readonly RecordingNotificationService _recorder = new();
    private readonly NotificationDispatcher       _dispatcher;

    public NotificationDispatcherTests()
    {
        _bus.Start();

        // Seed the state model so GetCurrentNick(Server) returns MyNick.
        _model.Apply(snap => snap with
        {
            Servers = new Dictionary<string, ServerState>
            {
                [Server] = ServerState.CreateDisconnected(Server, "irc.libera.chat", 6697, true) with
                {
                    IsConnected    = true,
                    RegisteredNick = MyNick,
                }
            }
        });

        _dispatcher = new NotificationDispatcher(_recorder, _model, _bus);
    }

    public ValueTask DisposeAsync()
    {
        _dispatcher.Dispose();
        return _bus.DisposeAsync();
    }

    // Publish an event and give the dispatch loop time to process it.
    private async Task Pub<T>(T evt) where T : struct
    {
        await _bus.PublishAsync(evt, EventPriority.Normal);
        await Task.Delay(50);
    }

    private static MessageReceived Pm(string from, string text, bool isSelf = false) =>
        new(Server, from, from, text, null, isSelf);

    private static MessageReceived Chan(string from, string text, bool isSelf = false) =>
        new(Server, Channel, from, text, null, isSelf);

    private static ActionReceived ChanAction(string from, string text) =>
        new(Server, Channel, from, text, null);

    private static ActionReceived PmAction(string from, string text) =>
        new(Server, from, from, text, null);

    // ---------------------------------------------------------------------------
    // Private messages
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task PrivateMessage_ToLocalNick_FiresNotification()
    {
        await Pub(Pm(OtherNick, "Hey, how are you?"));
        Assert.Single(_recorder.Received);
    }

    [Fact]
    public async Task PrivateMessage_NotificationKind_IsPrivateMessage()
    {
        await Pub(Pm(OtherNick, "Hey!"));
        Assert.Equal(NotificationKind.PrivateMessage, _recorder.Received[0].Kind);
    }

    [Fact]
    public async Task PrivateMessage_NotificationTitle_IsFromNick()
    {
        await Pub(Pm(OtherNick, "Hey!"));
        Assert.Equal(OtherNick, _recorder.Received[0].Title);
    }

    [Fact]
    public async Task PrivateMessage_NotificationBody_IsMessageText()
    {
        await Pub(Pm(OtherNick, "Hey, how are you?"));
        Assert.Equal("Hey, how are you?", _recorder.Received[0].Body);
    }

    [Fact]
    public async Task PrivateMessage_FromSelf_Suppressed()
    {
        await Pub(Pm(MyNick, "My own message", isSelf: true));
        Assert.Empty(_recorder.Received);
    }

    [Fact]
    public async Task MultiplePrivateMessages_EachFiresNotification()
    {
        await Pub(Pm(OtherNick, "First"));
        await Pub(Pm("bob", "Second"));
        Assert.Equal(2, _recorder.Received.Count);
    }

    // ---------------------------------------------------------------------------
    // Channel highlights
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ChannelMessage_ContainingNick_FiresHighlightNotification()
    {
        await Pub(Chan(OtherNick, $"{MyNick}: ping!"));
        Assert.Single(_recorder.Received);
    }

    [Fact]
    public async Task ChannelMessage_HighlightNotificationKind_IsHighlight()
    {
        await Pub(Chan(OtherNick, $"{MyNick}: hello"));
        Assert.Equal(NotificationKind.Highlight, _recorder.Received[0].Kind);
    }

    [Fact]
    public async Task ChannelMessage_HighlightTitle_ContainsChannelName()
    {
        await Pub(Chan(OtherNick, $"{MyNick}: hello"));
        Assert.Contains(Channel, _recorder.Received[0].Title);
    }

    [Fact]
    public async Task ChannelMessage_HighlightBody_ContainsFromNickAndText()
    {
        await Pub(Chan(OtherNick, $"{MyNick}: hello"));
        Assert.Contains(OtherNick, _recorder.Received[0].Body);
        Assert.Contains("hello", _recorder.Received[0].Body);
    }

    [Fact]
    public async Task ChannelMessage_NickCaseInsensitive_Triggers()
    {
        await Pub(Chan(OtherNick, $"{MyNick.ToUpperInvariant()}: yo"));
        Assert.Single(_recorder.Received);
    }

    [Fact]
    public async Task ChannelMessage_NickNotInText_NoNotification()
    {
        await Pub(Chan(OtherNick, "hey everyone, anyone around?"));
        Assert.Empty(_recorder.Received);
    }

    [Fact]
    public async Task ChannelMessage_NickAsSubstringOnly_NoNotification()
    {
        // MyNick = "TestUser"; text contains "TestUserBot" — not a word boundary match.
        await Pub(Chan(OtherNick, "TestUserBot just joined"));
        Assert.Empty(_recorder.Received);
    }

    [Fact]
    public async Task ChannelMessage_FromSelf_Suppressed()
    {
        await Pub(Chan(MyNick, $"{MyNick}: test", isSelf: true));
        Assert.Empty(_recorder.Received);
    }

    // ---------------------------------------------------------------------------
    // Action messages (/me)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Action_ToLocalNick_FiresPrivateMessageNotification()
    {
        await Pub(PmAction(OtherNick, "waves at you"));
        Assert.Single(_recorder.Received);
        Assert.Equal(NotificationKind.PrivateMessage, _recorder.Received[0].Kind);
    }

    [Fact]
    public async Task Action_InChannel_ContainingNick_FiresHighlightNotification()
    {
        await Pub(ChanAction(OtherNick, $"pokes {MyNick} gently"));
        Assert.Single(_recorder.Received);
        Assert.Equal(NotificationKind.Highlight, _recorder.Received[0].Kind);
    }

    [Fact]
    public async Task Action_InChannel_NickNotInText_NoNotification()
    {
        await Pub(ChanAction(OtherNick, "dances around"));
        Assert.Empty(_recorder.Received);
    }

    [Fact]
    public async Task Action_FromSelf_Suppressed()
    {
        // ActionReceived has no IsSelf field; the dispatcher checks FromNick == myNick.
        await Pub(ChanAction(MyNick, $"pokes {MyNick}"));
        Assert.Empty(_recorder.Received);
    }

    // ---------------------------------------------------------------------------
    // State / isolation edge cases
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task NoRegisteredNick_PrivateMessage_Suppressed()
    {
        // Replace state with a server that has no registered nick.
        _model.Apply(snap => snap with
        {
            Servers = new Dictionary<string, ServerState>
            {
                [Server] = ServerState.CreateDisconnected(Server, "irc.libera.chat", 6697, true)
                // RegisteredNick stays null (not connected yet)
            }
        });

        await Pub(Pm(OtherNick, "hello?"));
        Assert.Empty(_recorder.Received);
    }

    [Fact]
    public async Task ServerIsolation_PmOnUnknownServer_Suppressed()
    {
        // Publish a private message on OtherServer (not in the state model).
        await _bus.PublishAsync(new MessageReceived(OtherServer, OtherNick, OtherNick, "hey", null, false),
            EventPriority.Normal);
        await Task.Delay(50);
        Assert.Empty(_recorder.Received);
    }
}
