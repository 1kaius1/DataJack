// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace DataJack.Core.Caps.Handlers;

/// <summary>
/// Provides timestamp resolution for the <c>server-time</c> capability.
///
/// When server-time is active the <c>time</c> IRCv3 message tag carries an ISO 8601
/// timestamp (e.g. <c>2024-01-15T12:34:56.000Z</c>) that should be used as the display
/// timestamp instead of the local wall clock. The UI layer calls
/// <see cref="GetTimestamp"/> to get the correct timestamp for any message event.
/// </summary>
public sealed class ServerTimeHandler
{
    private readonly CapabilityRegistry _registry;

    public ServerTimeHandler(CapabilityRegistry registry) => _registry = registry;

    /// <summary>True when the <c>server-time</c> capability is currently active.</summary>
    public bool IsActive => _registry.IsActive("server-time");

    /// <summary>
    /// Returns the timestamp to use when displaying a message.
    /// When server-time is active and the <c>time</c> tag is present and parseable,
    /// returns the tag value. Otherwise returns <see cref="DateTimeOffset.UtcNow"/>.
    /// </summary>
    public DateTimeOffset GetTimestamp(IReadOnlyDictionary<string, string>? tags)
    {
        if (IsActive
            && tags is not null
            && tags.TryGetValue("time", out var timeStr)
            && DateTimeOffset.TryParse(timeStr, null, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed;
        }

        return DateTimeOffset.UtcNow;
    }
}
