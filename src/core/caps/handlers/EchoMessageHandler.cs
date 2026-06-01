// SPDX-License-Identifier: GPL-3.0-or-later

namespace DataJack.Core.Caps.Handlers;

/// <summary>
/// Tracks the <c>echo-message</c> capability state and provides the predicate used by
/// callers to identify messages that are server echoes of the client's own output.
///
/// When echo-message is active the server sends back every PRIVMSG/NOTICE the local client
/// sends, using the local nick as the prefix. Without dedup the message would appear twice:
/// once optimistically in the UI (if the client renders on send) and once from the server.
/// Callers check <see cref="IsEchoedMessage"/> to mark such messages as "self" and suppress
/// the duplicate. Full labeled-response-based dedup is a Phase 4 refinement.
/// </summary>
public sealed class EchoMessageHandler
{
    private readonly CapabilityRegistry _registry;

    public EchoMessageHandler(CapabilityRegistry registry) => _registry = registry;

    /// <summary>True when the <c>echo-message</c> capability is currently active.</summary>
    public bool IsActive => _registry.IsActive("echo-message");

    /// <summary>
    /// Returns true when <paramref name="fromNick"/> is the local client's nick and
    /// echo-message is active, indicating this is a server echo of a sent message.
    /// </summary>
    public bool IsEchoedMessage(string fromNick) =>
        IsActive &&
        _registry.LocalNick is { } local &&
        string.Equals(local, fromNick, StringComparison.OrdinalIgnoreCase);
}
