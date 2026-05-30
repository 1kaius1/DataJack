// SPDX-License-Identifier: GPL-3.0-or-later

namespace DataJack.Core.Events;

/// <summary>
/// Determines the processing tier of an event within a dispatch cycle.
/// The dispatch loop drains Critical completely before touching Normal, and Normal before Low.
/// </summary>
public enum EventPriority
{
    /// <summary>Errors, disconnects, and PING responses. Must not be starved by lower tiers.</summary>
    Critical = 0,

    /// <summary>Messages, joins, parts, and most IRC traffic. Default for new event types.</summary>
    Normal = 1,

    /// <summary>WHOIS replies, ban list entries, MODE floods, and other bulk informational responses.</summary>
    Low = 2,
}
