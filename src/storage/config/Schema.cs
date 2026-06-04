// SPDX-License-Identifier: GPL-3.0-or-later
// Root configuration schema. Schema version is incremented when any structural change
// is made; see Loader.cs for the migration runner. See ARCHITECTURE.md §14.

using System.Text.Json.Serialization;

namespace DataJack.Core.Storage.Config;

/// <summary>The root application configuration object, versioned for forward migration.</summary>
public sealed record AppConfig(
    [property: JsonPropertyName("schema_version")]     int                        SchemaVersion,
    [property: JsonPropertyName("identity")]           IdentitySettings           Identity,
    [property: JsonPropertyName("servers")]            List<ServerEntry>          Servers,
    [property: JsonPropertyName("appearance")]         AppearanceSettings         Appearance,
    [property: JsonPropertyName("logging")]            LoggingSettings            Logging,
    [property: JsonPropertyName("advanced")]           AdvancedSettings           Advanced,
    [property: JsonPropertyName("aliases")]            Dictionary<string, string> Aliases,
    [property: JsonPropertyName("highlight_patterns")] List<HighlightPattern>     HighlightPatterns,
    [property: JsonPropertyName("archive")]            ArchiveSettings            Archive,
    [property: JsonPropertyName("dcc")]                DccSettings                Dcc)
{
    /// <summary>Current schema version. Increment when adding fields that need migration.</summary>
    public const int CurrentVersion = 5;

    /// <summary>Factory for a fresh default configuration.</summary>
    public static AppConfig Default() => new(
        SchemaVersion:     CurrentVersion,
        Identity:          IdentitySettings.Default(),
        Servers:           new List<ServerEntry>(),
        Appearance:        AppearanceSettings.Default(),
        Logging:           LoggingSettings.Default(),
        Advanced:          AdvancedSettings.Default(),
        Aliases:           new Dictionary<string, string>(),
        HighlightPatterns: new List<HighlightPattern>(),
        Archive:           ArchiveSettings.Default(),
        Dcc:               DccSettings.Default());
}

/// <summary>User identity settings. All fields may be overridden per-server.</summary>
public sealed record IdentitySettings(
    [property: JsonPropertyName("nick")]      string        Nick,
    [property: JsonPropertyName("alt_nicks")] List<string>  AltNicks,
    [property: JsonPropertyName("username")]  string        Username,
    [property: JsonPropertyName("realname")]  string        Realname)
{
    internal static IdentitySettings Default() => new(
        Nick:      Environment.UserName,
        AltNicks:  new List<string> { Environment.UserName + "_", Environment.UserName + "__" },
        Username:  Environment.UserName,
        Realname:  Environment.UserName);
}

/// <summary>One server address book entry. See ARCHITECTURE.md §14.3 Servers.</summary>
public sealed record ServerEntry(
    [property: JsonPropertyName("id")]               Guid             Id,
    [property: JsonPropertyName("network_name")]     string           NetworkName,
    [property: JsonPropertyName("address")]          string           Address,
    [property: JsonPropertyName("port")]             int              Port,
    [property: JsonPropertyName("tls")]              bool             Tls,
    [property: JsonPropertyName("password")]         string?          Password,
    [property: JsonPropertyName("nick")]             string?          Nick,
    [property: JsonPropertyName("username")]         string?          Username,
    [property: JsonPropertyName("realname")]         string?          Realname,
    [property: JsonPropertyName("sasl")]             SaslCredentials? Sasl,
    [property: JsonPropertyName("auto_join")]        List<string>     AutoJoinChannels,
    [property: JsonPropertyName("auto_connect")]     bool             AutoConnect,
    [property: JsonPropertyName("encoding")]         string           Encoding,
    [property: JsonPropertyName("connect_commands")] List<string>     ConnectCommands,
    [property: JsonPropertyName("proxy")]             ProxySettings?   Proxy = null)
{
    /// <summary>Convenience factory for a new blank server entry.</summary>
    public static ServerEntry New(string networkName, string address) => new(
        Id:               Guid.NewGuid(),
        NetworkName:      networkName,
        Address:          address,
        Port:             6697,
        Tls:              true,
        Password:         null,
        Nick:             null,
        Username:         null,
        Realname:         null,
        Sasl:             null,
        AutoJoinChannels: new List<string>(),
        AutoConnect:      false,
        Encoding:         "UTF-8",
        ConnectCommands:  new List<string>(),
        Proxy:            null);
}

/// <summary>SOCKS5 proxy configuration for a server connection.</summary>
public sealed record ProxySettings(
    [property: JsonPropertyName("host")]     string  Host,
    [property: JsonPropertyName("port")]     int     Port,
    [property: JsonPropertyName("username")] string? Username = null,
    [property: JsonPropertyName("password")] string? Password = null);

/// <summary>SASL credentials for a server connection.</summary>
public sealed record SaslCredentials(
    [property: JsonPropertyName("mechanism")] string Mechanism,
    [property: JsonPropertyName("account")]   string Account,
    [property: JsonPropertyName("password")]  string Password);

/// <summary>Visual appearance settings.</summary>
public sealed record AppearanceSettings(
    [property: JsonPropertyName("theme_name")]        string  ThemeName,
    [property: JsonPropertyName("font_family")]       string? FontFamily,
    [property: JsonPropertyName("font_size")]         double? FontSize,
    [property: JsonPropertyName("show_timestamps")]   bool    ShowTimestamps,
    [property: JsonPropertyName("timestamp_format")]  string  TimestampFormat,
    [property: JsonPropertyName("scrollback_limit")]  int     ScrollbackLimit)
{
    internal static AppearanceSettings Default() => new(
        ThemeName:       "default",
        FontFamily:      null,
        FontSize:        null,
        ShowTimestamps:  true,
        TimestampFormat: "HH:mm",
        ScrollbackLimit: 5000);
}

/// <summary>Log file settings.</summary>
public sealed record LoggingSettings(
    [property: JsonPropertyName("enabled")]       bool    Enabled,
    [property: JsonPropertyName("log_directory")] string? LogDirectory)
{
    internal static LoggingSettings Default() => new(Enabled: true, LogDirectory: null);
}

// ---------------------------------------------------------------------------
// Highlight pattern types
// ---------------------------------------------------------------------------

/// <summary>Determines how a highlight pattern expression is interpreted.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum HighlightPatternKind
{
    /// <summary>Case-insensitive (by default) substring match.</summary>
    Literal,
    /// <summary>Glob-style pattern: <c>*</c> matches any sequence, <c>?</c> matches one character. Always case-insensitive.</summary>
    Wildcard,
    /// <summary>Full .NET regular expression. Case sensitivity is controlled by <see cref="HighlightPattern.CaseSensitive"/>.</summary>
    Regex,
}

/// <summary>
/// One user-configured highlight pattern. The current nick is always an additional implicit
/// pattern; it never needs to be added here.
/// </summary>
public sealed record HighlightPattern(
    [property: JsonPropertyName("expression")]     string              Expression,
    [property: JsonPropertyName("kind")]           HighlightPatternKind Kind,
    [property: JsonPropertyName("case_sensitive")] bool                CaseSensitive = false);

/// <summary>Settings for automatic log file rotation and gzip compression.</summary>
public sealed record ArchiveSettings(
    [property: JsonPropertyName("enabled")]      bool Enabled,
    [property: JsonPropertyName("max_age_days")] int  MaxAgeDays)
{
    /// <summary>Default: enabled, archive files older than 90 days.</summary>
    internal static ArchiveSettings Default() => new(Enabled: true, MaxAgeDays: 90);
}

/// <summary>DCC file transfer configuration. See ARCHITECTURE.md §11.</summary>
public sealed record DccSettings(
    /// <summary>
    /// Directory where received files are saved. When null the platform Downloads folder is used
    /// (<c>~/Downloads</c> on Linux/macOS, <c>%USERPROFILE%\Downloads</c> on Windows).
    /// </summary>
    [property: JsonPropertyName("download_directory")] string? DownloadDirectory,
    /// <summary>
    /// When true, incoming DCC SEND offers are automatically accepted without prompting.
    /// Off by default — incoming offers always require explicit user confirmation.
    /// </summary>
    [property: JsonPropertyName("auto_accept")]        bool    AutoAccept,
    /// <summary>
    /// Maximum size in megabytes of an auto-accepted file. 0 means no limit.
    /// Only relevant when <see cref="AutoAccept"/> is true.
    /// </summary>
    [property: JsonPropertyName("max_file_size_mb")]   int     MaxFileSizeMb)
{
    /// <summary>Factory for safe default DCC settings: no auto-accept, platform download directory.</summary>
    internal static DccSettings Default() => new(
        DownloadDirectory: null,
        AutoAccept:        false,
        MaxFileSizeMb:     0);
}

/// <summary>Advanced tuning settings for flood control and reconnect behavior.</summary>
public sealed record AdvancedSettings(
    [property: JsonPropertyName("flood_token_capacity")]         double FloodTokenCapacity,
    [property: JsonPropertyName("flood_drain_rate")]             double FloodDrainRate,
    [property: JsonPropertyName("reconnect_initial_delay_sec")]  int    ReconnectInitialDelaySec,
    [property: JsonPropertyName("reconnect_max_delay_sec")]      int    ReconnectMaxDelaySec,
    [property: JsonPropertyName("reconnect_max_attempts")]       int    ReconnectMaxAttempts)
{
    internal static AdvancedSettings Default() => new(
        FloodTokenCapacity:         10.0,
        FloodDrainRate:             2.0,
        ReconnectInitialDelaySec:   2,
        ReconnectMaxDelaySec:       300,
        ReconnectMaxAttempts:       0);
}
