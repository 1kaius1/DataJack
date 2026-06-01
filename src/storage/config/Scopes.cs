// SPDX-License-Identifier: GPL-3.0-or-later
// Scoped settings resolution: global -> server -> channel. See ARCHITECTURE.md §14.2.
// Narrower scopes override wider ones. Callers use SettingsScope to resolve a setting
// in the correct priority order without manually walking the tree.

using DataJack.Platform;

namespace DataJack.Core.Storage.Config;

/// <summary>
/// Resolves configuration values across the three scopes (global, server, channel).
/// Narrower scopes win; a null value at a given scope falls through to the next wider scope.
/// </summary>
public sealed class SettingsScope
{
    private readonly AppConfig _global;
    private readonly ServerEntry? _server;

    /// <param name="global">The loaded global application configuration.</param>
    /// <param name="serverId">
    /// The server network name to use for server-scope lookups.
    /// Pass null when no server context is relevant.
    /// </param>
    public SettingsScope(AppConfig global, string? serverId = null)
    {
        _global = global;
        _server = serverId is null
            ? null
            : global.Servers.Find(s => s.NetworkName.Equals(serverId, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------------------
    // Identity
    // ---------------------------------------------------------------------------

    /// <summary>Effective nick: server override wins over global setting.</summary>
    public string Nick => _server?.Nick ?? _global.Identity.Nick;

    /// <summary>Effective username.</summary>
    public string Username => _server?.Username ?? _global.Identity.Username;

    /// <summary>Effective realname.</summary>
    public string Realname => _server?.Realname ?? _global.Identity.Realname;

    /// <summary>Alternate nicks when the primary nick is in use.</summary>
    public IReadOnlyList<string> AltNicks => _global.Identity.AltNicks;

    // ---------------------------------------------------------------------------
    // Connection
    // ---------------------------------------------------------------------------

    /// <summary>Server-level password (not a channel key).</summary>
    public string? ServerPassword => _server?.Password;

    /// <summary>Character encoding for this server. Defaults to UTF-8.</summary>
    public string Encoding => _server?.Encoding ?? "UTF-8";

    /// <summary>SASL credentials for this server, or null if SASL is not configured.</summary>
    public SaslCredentials? Sasl => _server?.Sasl;

    // ---------------------------------------------------------------------------
    // Appearance
    // ---------------------------------------------------------------------------

    /// <summary>Active theme name.</summary>
    public string ThemeName => _global.Appearance.ThemeName;

    /// <summary>Font family override, or null to use the theme default.</summary>
    public string? FontFamily => _global.Appearance.FontFamily;

    /// <summary>Font size override in points, or null to use the theme default.</summary>
    public double? FontSize => _global.Appearance.FontSize;

    /// <summary>Whether message timestamps should be displayed.</summary>
    public bool ShowTimestamps => _global.Appearance.ShowTimestamps;

    /// <summary>Format string for displayed timestamps (e.g. "HH:mm").</summary>
    public string TimestampFormat => _global.Appearance.TimestampFormat;

    /// <summary>Maximum number of messages to keep in memory per buffer.</summary>
    public int ScrollbackLimit => _global.Appearance.ScrollbackLimit;

    // ---------------------------------------------------------------------------
    // Logging
    // ---------------------------------------------------------------------------

    /// <summary>Whether buffer logging is enabled.</summary>
    public bool LoggingEnabled => _global.Logging.Enabled;

    /// <summary>
    /// Resolved log directory. Uses the user-configured override if set;
    /// falls back to the platform log directory.
    /// </summary>
    public string LogDirectory =>
        _global.Logging.LogDirectory ?? Paths.LogDirectory;

    // ---------------------------------------------------------------------------
    // Advanced
    // ---------------------------------------------------------------------------

    /// <summary>Flood token bucket capacity.</summary>
    public double FloodTokenCapacity => _global.Advanced.FloodTokenCapacity;

    /// <summary>Flood token bucket drain rate in tokens per second.</summary>
    public double FloodDrainRate => _global.Advanced.FloodDrainRate;

    /// <summary>Initial reconnect delay in seconds.</summary>
    public int ReconnectInitialDelaySec => _global.Advanced.ReconnectInitialDelaySec;

    /// <summary>Maximum reconnect delay cap in seconds.</summary>
    public int ReconnectMaxDelaySec => _global.Advanced.ReconnectMaxDelaySec;

    /// <summary>Maximum reconnect attempts (0 = unlimited).</summary>
    public int ReconnectMaxAttempts => _global.Advanced.ReconnectMaxAttempts;
}
