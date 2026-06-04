// SPDX-License-Identifier: GPL-3.0-or-later
// Root configuration schema. Schema version is incremented when any structural change
// is made; see Loader.cs for the migration runner. See ARCHITECTURE.md §14.

using System.Text.Json.Serialization;

namespace DataJack.Core.Storage.Config;

/// <summary>The root application configuration object, versioned for forward migration.</summary>
public sealed record AppConfig(
    [property: JsonPropertyName("schema_version")] int                        SchemaVersion,
    [property: JsonPropertyName("identity")]    IdentitySettings               Identity,
    [property: JsonPropertyName("servers")]     List<ServerEntry>              Servers,
    [property: JsonPropertyName("appearance")]  AppearanceSettings             Appearance,
    [property: JsonPropertyName("logging")]     LoggingSettings                Logging,
    [property: JsonPropertyName("advanced")]    AdvancedSettings               Advanced,
    [property: JsonPropertyName("aliases")]     Dictionary<string, string>     Aliases)
{
    /// <summary>Current schema version. Increment when adding fields that need migration.</summary>
    public const int CurrentVersion = 2;

    /// <summary>Factory for a fresh default configuration.</summary>
    public static AppConfig Default() => new(
        SchemaVersion: CurrentVersion,
        Identity:      IdentitySettings.Default(),
        Servers:       new List<ServerEntry>(),
        Appearance:    AppearanceSettings.Default(),
        Logging:       LoggingSettings.Default(),
        Advanced:      AdvancedSettings.Default(),
        Aliases:       new Dictionary<string, string>());
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
    [property: JsonPropertyName("connect_commands")] List<string>     ConnectCommands)
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
        ConnectCommands:  new List<string>());
}

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
