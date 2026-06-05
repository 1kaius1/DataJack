// SPDX-License-Identifier: GPL-3.0-or-later
// Desktop notification interface, dispatcher, null implementation, and platform factory.
// Platform backends are in Linux.cs, Macos.cs, and Windows.cs.
// See ARCHITECTURE.md §15.

using System.Runtime.InteropServices;
using DataJack.Core.Events;
using DataJack.Core.Irc;
using DataJack.Core.State;
using DataJack.Core.Storage.Config;

namespace DataJack.Platform.Notifications;

// ---------------------------------------------------------------------------
// Public data types
// ---------------------------------------------------------------------------

/// <summary>Category of a desktop notification; backends may use this to choose icon or sound.</summary>
public enum NotificationKind
{
    /// <summary>A channel message matched a highlight pattern (including the current nick).</summary>
    Highlight,
    /// <summary>A private message directed at the local user.</summary>
    PrivateMessage,
    /// <summary>An incoming DCC file or chat offer.</summary>
    DccOffer,
    /// <summary>A watched nick (MONITOR) came online.</summary>
    WatchedNickOnline,
}

/// <summary>Data carried by one desktop notification request.</summary>
public sealed record NotificationInfo(
    /// <summary>Bold title line shown by the platform (e.g. from-nick or channel name).</summary>
    string Title,
    /// <summary>Body text (e.g. the message itself).</summary>
    string Body,
    /// <summary>Category for icon/sound selection.</summary>
    NotificationKind Kind);

// ---------------------------------------------------------------------------
// Interface
// ---------------------------------------------------------------------------

/// <summary>
/// Delivers desktop notifications. Obtain an instance via <see cref="NotificationServiceFactory.Create"/>.
/// Implementations must be safe to call from any thread and must swallow all delivery errors internally.
/// </summary>
public interface INotificationService
{
    /// <summary><c>true</c> when the backend can actually display notifications on this platform.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Attempt to show a desktop notification. Delivery failures are silently swallowed; the returned
    /// task always completes without faulting.
    /// </summary>
    Task NotifyAsync(NotificationInfo notification, CancellationToken ct = default);
}

// ---------------------------------------------------------------------------
// Null implementation
// ---------------------------------------------------------------------------

/// <summary>No-op notification service used for testing and unsupported platforms.</summary>
public sealed class NullNotificationService : INotificationService
{
    /// <inheritdoc/>
    public bool IsSupported => false;

    /// <inheritdoc/>
    public Task NotifyAsync(NotificationInfo notification, CancellationToken ct = default) =>
        Task.CompletedTask;
}

// ---------------------------------------------------------------------------
// Factory
// ---------------------------------------------------------------------------

/// <summary>
/// Selects the appropriate <see cref="INotificationService"/> implementation for the
/// current operating system at runtime.
/// </summary>
public static class NotificationServiceFactory
{
    /// <summary>
    /// Returns a <see cref="LinuxNotificationService"/> on Linux,
    /// <see cref="MacosNotificationService"/> on macOS,
    /// <see cref="WindowsNotificationService"/> on Windows,
    /// or a <see cref="NullNotificationService"/> on unrecognized platforms.
    /// </summary>
    public static INotificationService Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return new LinuxNotificationService();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return new MacosNotificationService();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new WindowsNotificationService();
        return new NullNotificationService();
    }
}

// ---------------------------------------------------------------------------
// Dispatcher
// ---------------------------------------------------------------------------

/// <summary>
/// Subscribes to the event bus and fires desktop notifications for private messages and
/// channel highlights. The current nick is read from the state model on every event so
/// nick changes take effect immediately without requiring a restart.
///
/// Trigger rules:
/// <list type="bullet">
///   <item>Any private PRIVMSG not sent by the local user → <see cref="NotificationKind.PrivateMessage"/>.</item>
///   <item>Any private CTCP ACTION not sent by the local user → <see cref="NotificationKind.PrivateMessage"/>.</item>
///   <item>Any channel PRIVMSG or ACTION for which <see cref="HighlightMatcher.IsHighlight"/>
///     returns true (implicit current-nick whole-word check plus user-configured patterns)
///     → <see cref="NotificationKind.Highlight"/>.</item>
/// </list>
/// </summary>
public sealed class NotificationDispatcher : IDisposable
{
    private readonly INotificationService                     _service;
    private readonly IRCStateModel                            _stateModel;
    private readonly EventDispatcher                          _bus;
    private readonly Func<IReadOnlyList<HighlightPattern>>?   _patternsGetter;

    /// <param name="service">Backend that delivers the OS notification.</param>
    /// <param name="stateModel">Queried on every event to retrieve the current nick.</param>
    /// <param name="bus">Event bus; the dispatcher subscribes to MessageReceived and ActionReceived.</param>
    /// <param name="patternsGetter">
    /// Optional delegate invoked on each channel message to obtain the current
    /// <see cref="HighlightPattern"/> list from config. When null, only the implicit
    /// current-nick match is checked.
    /// </param>
    public NotificationDispatcher(
        INotificationService                    service,
        IRCStateModel                           stateModel,
        EventDispatcher                         bus,
        Func<IReadOnlyList<HighlightPattern>>?  patternsGetter = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(stateModel);
        ArgumentNullException.ThrowIfNull(bus);

        _service        = service;
        _stateModel     = stateModel;
        _bus            = bus;
        _patternsGetter = patternsGetter;

        _bus.Subscribe<MessageReceived>(OnMessageReceived);
        _bus.Subscribe<ActionReceived>(OnActionReceived);
    }

    /// <summary>Unsubscribes all handlers from the event bus.</summary>
    public void Dispose()
    {
        _bus.Unsubscribe<MessageReceived>(OnMessageReceived);
        _bus.Unsubscribe<ActionReceived>(OnActionReceived);
    }

    // ---------------------------------------------------------------------------
    // Event handlers (called on the dispatch thread)
    // ---------------------------------------------------------------------------

    private void OnMessageReceived(MessageReceived e)
    {
        if (e.IsSelf) return;

        string? myNick = _stateModel.CreateQuery().GetCurrentNick(e.Server);
        if (myNick is null) return;

        if (!IsChannelTarget(e.Target))
        {
            Fire(new NotificationInfo(
                Title: e.FromNick,
                Body:  e.Text,
                Kind:  NotificationKind.PrivateMessage));
        }
        else if (IsChannelHighlight(e.Text, myNick))
        {
            Fire(new NotificationInfo(
                Title: $"Highlight in {e.Target}",
                Body:  $"<{e.FromNick}> {e.Text}",
                Kind:  NotificationKind.Highlight));
        }
    }

    private void OnActionReceived(ActionReceived e)
    {
        string? myNick = _stateModel.CreateQuery().GetCurrentNick(e.Server);
        if (myNick is null) return;

        // Suppress /me commands from ourselves (e.g. echo-message cap reflecting our own action).
        if (string.Equals(e.FromNick, myNick, StringComparison.OrdinalIgnoreCase)) return;

        if (!IsChannelTarget(e.Target))
        {
            Fire(new NotificationInfo(
                Title: e.FromNick,
                Body:  $"* {e.FromNick} {e.Text}",
                Kind:  NotificationKind.PrivateMessage));
        }
        else if (IsChannelHighlight(e.Text, myNick))
        {
            Fire(new NotificationInfo(
                Title: $"Highlight in {e.Target}",
                Body:  $"* {e.FromNick} {e.Text}",
                Kind:  NotificationKind.Highlight));
        }
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private void Fire(NotificationInfo info) =>
        _ = _service.NotifyAsync(info).ContinueWith(
            static t => { /* swallow delivery errors */ },
            TaskContinuationOptions.OnlyOnFaulted);

    private static bool IsChannelTarget(string target) =>
        target.Length > 0 && target[0] is '#' or '&' or '+' or '!';

    private bool IsChannelHighlight(string text, string myNick)
    {
        var patterns = _patternsGetter?.Invoke() ?? Array.Empty<HighlightPattern>();
        return HighlightMatcher.IsHighlight(text, myNick, patterns);
    }
}
