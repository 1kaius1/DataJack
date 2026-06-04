// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.Storage.Config;
using Xunit;
#pragma warning disable CA1869 // suppress "cache JsonSerializerOptions" — not relevant in tests

namespace DataJack.Core.Tests;

public sealed class ConfigTests : IAsyncDisposable
{
    private readonly string _tempDir;

    public ConfigTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"datajack_config_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ---------------------------------------------------------------------------
    // Schema defaults
    // ---------------------------------------------------------------------------

    [Fact]
    public void Default_Config_HasCurrentSchemaVersion()
    {
        var config = AppConfig.Default();
        Assert.Equal(AppConfig.CurrentVersion, config.SchemaVersion);
    }

    [Fact]
    public void Default_Identity_HasNonEmptyNick()
    {
        var config = AppConfig.Default();
        Assert.False(string.IsNullOrWhiteSpace(config.Identity.Nick));
    }

    [Fact]
    public void Default_Servers_IsEmpty()
    {
        var config = AppConfig.Default();
        Assert.Empty(config.Servers);
    }

    [Fact]
    public void Default_Appearance_HasScrollbackLimit()
    {
        var config = AppConfig.Default();
        Assert.True(config.Appearance.ScrollbackLimit > 0);
    }

    // ---------------------------------------------------------------------------
    // ConfigLoader: create-on-missing
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Loader_MissingFile_CreatesDefaultConfig()
    {
        string path = Path.Combine(_tempDir, "settings.json");
        var loader = new ConfigLoader(path);

        await loader.LoadAsync();

        Assert.True(File.Exists(path), "Config file should be created.");
        Assert.Equal(AppConfig.CurrentVersion, loader.Config.SchemaVersion);
    }

    [Fact]
    public async Task Loader_RoundTrip_PreservesValues()
    {
        string path = Path.Combine(_tempDir, "settings.json");
        var loader = new ConfigLoader(path);
        await loader.LoadAsync();

        var updated = loader.Config with
        {
            Identity = loader.Config.Identity with { Nick = "TestNick" },
        };
        await loader.UpdateAsync(updated);

        // Load fresh instance to verify persisted data.
        var loader2 = new ConfigLoader(path);
        await loader2.LoadAsync();

        Assert.Equal("TestNick", loader2.Config.Identity.Nick);
    }

    [Fact]
    public async Task Loader_SaveIsAtomic_NoTmpFileLeft()
    {
        string path = Path.Combine(_tempDir, "settings.json");
        var loader = new ConfigLoader(path);
        await loader.LoadAsync();
        await loader.SaveAsync();

        // The .tmp file used during atomic write should be cleaned up.
        Assert.False(File.Exists(path + ".tmp"), "Temporary file should be removed after save.");
    }

    // ---------------------------------------------------------------------------
    // ServerEntry factory
    // ---------------------------------------------------------------------------

    [Fact]
    public void ServerEntry_New_HasSensibleDefaults()
    {
        var e = ServerEntry.New("Libera", "irc.libera.chat");
        Assert.Equal("Libera", e.NetworkName);
        Assert.Equal("irc.libera.chat", e.Address);
        Assert.Equal(6697, e.Port);
        Assert.True(e.Tls);
        Assert.False(e.AutoConnect);
        Assert.Empty(e.AutoJoinChannels);
        Assert.NotEqual(Guid.Empty, e.Id);
    }

    [Fact]
    public void ServerEntry_TwoNew_HaveDifferentIds()
    {
        var a = ServerEntry.New("A", "a.example.com");
        var b = ServerEntry.New("B", "b.example.com");
        Assert.NotEqual(a.Id, b.Id);
    }

    // ---------------------------------------------------------------------------
    // SettingsScope
    // ---------------------------------------------------------------------------

    [Fact]
    public void Scope_NoServer_UsesGlobalNick()
    {
        var config = AppConfig.Default() with
        {
            Identity = AppConfig.Default().Identity with { Nick = "GlobalNick" },
        };
        var scope = new SettingsScope(config);
        Assert.Equal("GlobalNick", scope.Nick);
    }

    [Fact]
    public void Scope_ServerOverride_WinsOverGlobal()
    {
        var server = ServerEntry.New("TestNet", "test.example.com") with { Nick = "ServerNick" };
        var config = AppConfig.Default() with
        {
            Identity = AppConfig.Default().Identity with { Nick = "GlobalNick" },
            Servers  = new List<ServerEntry> { server },
        };

        var scope = new SettingsScope(config, "TestNet");
        Assert.Equal("ServerNick", scope.Nick);
    }

    [Fact]
    public void Scope_UnknownServer_FallsBackToGlobal()
    {
        var config = AppConfig.Default() with
        {
            Identity = AppConfig.Default().Identity with { Nick = "FallbackNick" },
        };
        var scope = new SettingsScope(config, "DoesNotExist");
        Assert.Equal("FallbackNick", scope.Nick);
    }

    [Fact]
    public void Scope_ServerEncodingOverride_ReturnsServerValue()
    {
        var server = ServerEntry.New("Legacy", "legacy.example.com") with { Encoding = "Latin-1" };
        var config = AppConfig.Default() with { Servers = new List<ServerEntry> { server } };
        var scope = new SettingsScope(config, "Legacy");
        Assert.Equal("Latin-1", scope.Encoding);
    }

    [Fact]
    public void Scope_NoServerEncodingOverride_ReturnsUtf8()
    {
        var scope = new SettingsScope(AppConfig.Default(), "Libera");
        Assert.Equal("UTF-8", scope.Encoding);
    }

    // ---------------------------------------------------------------------------
    // ProxySettings (no schema version change — nullable field, null default)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ServerEntry_WithProxySettings_RoundTripsInConfig()
    {
        string path = Path.Combine(_tempDir, "settings_proxy.json");
        var loader = new ConfigLoader(path);
        await loader.LoadAsync();

        var proxy  = new ProxySettings("proxy.example.com", 1080, "alice", "s3cr3t");
        var entry  = ServerEntry.New("libera", "irc.libera.chat") with { Proxy = proxy };
        var updated = loader.Config with { Servers = new List<ServerEntry> { entry } };
        await loader.UpdateAsync(updated);

        var loader2 = new ConfigLoader(path);
        await loader2.LoadAsync();

        var loaded = Assert.Single(loader2.Config.Servers);
        Assert.NotNull(loaded.Proxy);
        Assert.Equal("proxy.example.com", loaded.Proxy.Host);
        Assert.Equal(1080,    loaded.Proxy.Port);
        Assert.Equal("alice", loaded.Proxy.Username);
        Assert.Equal("s3cr3t", loaded.Proxy.Password);
    }

    [Fact]
    public async Task ServerEntry_WithoutProxy_DeserializesAsNull()
    {
        string path = Path.Combine(_tempDir, "settings_noproxy.json");
        var loader = new ConfigLoader(path);
        await loader.LoadAsync();

        var entry   = ServerEntry.New("libera", "irc.libera.chat");  // Proxy = null
        var updated = loader.Config with { Servers = new List<ServerEntry> { entry } };
        await loader.UpdateAsync(updated);

        var loader2 = new ConfigLoader(path);
        await loader2.LoadAsync();

        var loaded = Assert.Single(loader2.Config.Servers);
        Assert.Null(loaded.Proxy);
    }

    // ---------------------------------------------------------------------------
    // Aliases (schema v2)
    // ---------------------------------------------------------------------------

    [Fact]
    public void Default_Aliases_IsEmpty()
    {
        var config = AppConfig.Default();
        Assert.NotNull(config.Aliases);
        Assert.Empty(config.Aliases);
    }

    [Fact]
    public async Task Loader_RoundTrip_PreservesAliases()
    {
        string path = Path.Combine(_tempDir, "settings_aliases.json");
        var loader = new ConfigLoader(path);
        await loader.LoadAsync();

        var updated = loader.Config with
        {
            Aliases = new Dictionary<string, string> { ["weather"] = "/msg #weather %1" },
        };
        await loader.UpdateAsync(updated);

        var loader2 = new ConfigLoader(path);
        await loader2.LoadAsync();

        Assert.True(loader2.Config.Aliases.ContainsKey("weather"));
        Assert.Equal("/msg #weather %1", loader2.Config.Aliases["weather"]);
    }

    // ---------------------------------------------------------------------------
    // HighlightPatterns (schema v3)
    // ---------------------------------------------------------------------------

    [Fact]
    public void Default_HighlightPatterns_IsEmpty()
    {
        var config = AppConfig.Default();
        Assert.NotNull(config.HighlightPatterns);
        Assert.Empty(config.HighlightPatterns);
    }

    [Fact]
    public void Default_Config_SchemaVersionIsSix()
    {
        Assert.Equal(6, AppConfig.CurrentVersion);
        Assert.Equal(6, AppConfig.Default().SchemaVersion);
    }

    [Fact]
    public void Default_Appearance_LayoutModeIsTabs()
    {
        var config = AppConfig.Default();
        Assert.Equal("tabs", config.Appearance.LayoutMode);
    }

    [Fact]
    public void Default_Dcc_HasExpectedDefaults()
    {
        var config = AppConfig.Default();
        Assert.NotNull(config.Dcc);
        Assert.Null(config.Dcc.DownloadDirectory);
        Assert.False(config.Dcc.AutoAccept);
        Assert.Equal(0, config.Dcc.MaxFileSizeMb);
    }

    [Fact]
    public void Default_Archive_IsEnabledWithMaxAge90()
    {
        var config = AppConfig.Default();
        Assert.NotNull(config.Archive);
        Assert.True(config.Archive.Enabled);
        Assert.Equal(90, config.Archive.MaxAgeDays);
    }

    [Fact]
    public async Task Loader_MigratesV3ToV4_AddsArchiveSettings()
    {
        string path = Path.Combine(_tempDir, "settings_v3.json");

        string v3Json = """
            {
              "schema_version": 3,
              "identity": { "nick": "tester", "alt_nicks": [], "username": "tester", "realname": "tester" },
              "servers": [],
              "appearance": {
                "theme_name": "default",
                "font_family": null,
                "font_size": null,
                "show_timestamps": true,
                "timestamp_format": "HH:mm",
                "scrollback_limit": 5000
              },
              "logging": { "enabled": true, "log_directory": null },
              "advanced": {
                "flood_token_capacity": 10.0,
                "flood_drain_rate": 2.0,
                "reconnect_initial_delay_sec": 2,
                "reconnect_max_delay_sec": 300,
                "reconnect_max_attempts": 0
              },
              "aliases": {},
              "highlight_patterns": []
            }
            """;
        await File.WriteAllTextAsync(path, v3Json);

        var loader = new ConfigLoader(path);
        await loader.LoadAsync();

        // All pending migrations run, so the result is always the current version.
        Assert.Equal(AppConfig.CurrentVersion, loader.Config.SchemaVersion);
        Assert.NotNull(loader.Config.Archive);
        Assert.True(loader.Config.Archive.Enabled);
        Assert.Equal(90, loader.Config.Archive.MaxAgeDays);
        // v4->v5 and v5->v6 also run.
        Assert.NotNull(loader.Config.Dcc);
        Assert.Equal("tabs", loader.Config.Appearance.LayoutMode);
    }

    [Fact]
    public async Task Loader_MigratesV4ToV5_AddsDccSettings()
    {
        string path = Path.Combine(_tempDir, "settings_v4.json");

        string v4Json = """
            {
              "schema_version": 4,
              "identity": { "nick": "tester", "alt_nicks": [], "username": "tester", "realname": "tester" },
              "servers": [],
              "appearance": {
                "theme_name": "default",
                "font_family": null,
                "font_size": null,
                "show_timestamps": true,
                "timestamp_format": "HH:mm",
                "scrollback_limit": 5000
              },
              "logging": { "enabled": true, "log_directory": null },
              "advanced": {
                "flood_token_capacity": 10.0,
                "flood_drain_rate": 2.0,
                "reconnect_initial_delay_sec": 2,
                "reconnect_max_delay_sec": 300,
                "reconnect_max_attempts": 0
              },
              "aliases": {},
              "highlight_patterns": [],
              "archive": { "enabled": true, "max_age_days": 90 }
            }
            """;
        await File.WriteAllTextAsync(path, v4Json);

        var loader = new ConfigLoader(path);
        await loader.LoadAsync();

        // v4 → v5 → v6 all run, result is always current version.
        Assert.Equal(AppConfig.CurrentVersion, loader.Config.SchemaVersion);
        Assert.NotNull(loader.Config.Dcc);
        Assert.Null(loader.Config.Dcc.DownloadDirectory);
        Assert.False(loader.Config.Dcc.AutoAccept);
        Assert.Equal(0, loader.Config.Dcc.MaxFileSizeMb);
        Assert.Equal("tabs", loader.Config.Appearance.LayoutMode);
    }

    [Fact]
    public async Task Loader_MigratesV5ToV6_AddsLayoutMode()
    {
        string path = Path.Combine(_tempDir, "settings_v5.json");

        string v5Json = """
            {
              "schema_version": 5,
              "identity": { "nick": "tester", "alt_nicks": [], "username": "tester", "realname": "tester" },
              "servers": [],
              "appearance": {
                "theme_name": "default",
                "font_family": null,
                "font_size": null,
                "show_timestamps": true,
                "timestamp_format": "HH:mm",
                "scrollback_limit": 5000
              },
              "logging": { "enabled": true, "log_directory": null },
              "advanced": {
                "flood_token_capacity": 10.0,
                "flood_drain_rate": 2.0,
                "reconnect_initial_delay_sec": 2,
                "reconnect_max_delay_sec": 300,
                "reconnect_max_attempts": 0
              },
              "aliases": {},
              "highlight_patterns": [],
              "archive": { "enabled": true, "max_age_days": 90 },
              "dcc": { "download_directory": null, "auto_accept": false, "max_file_size_mb": 0 }
            }
            """;
        await File.WriteAllTextAsync(path, v5Json);

        var loader = new ConfigLoader(path);
        await loader.LoadAsync();

        Assert.Equal(6, loader.Config.SchemaVersion);
        Assert.Equal("tabs", loader.Config.Appearance.LayoutMode);
    }

    [Fact]
    public async Task Loader_RoundTrip_PreservesLayoutMode()
    {
        string path = Path.Combine(_tempDir, "settings_layout.json");
        var loader = new ConfigLoader(path);
        await loader.LoadAsync();

        var updated = loader.Config with
        {
            Appearance = loader.Config.Appearance with { LayoutMode = "tree" },
        };
        await loader.UpdateAsync(updated);

        var loader2 = new ConfigLoader(path);
        await loader2.LoadAsync();

        Assert.Equal("tree", loader2.Config.Appearance.LayoutMode);
    }

    [Fact]
    public async Task Loader_RoundTrip_PreservesDccSettings()
    {
        string path = Path.Combine(_tempDir, "settings_dcc.json");
        var loader = new ConfigLoader(path);
        await loader.LoadAsync();

        var updated = loader.Config with
        {
            Dcc = new DataJack.Core.Storage.Config.DccSettings("/my/downloads", true, 50),
        };
        await loader.UpdateAsync(updated);

        var loader2 = new ConfigLoader(path);
        await loader2.LoadAsync();

        Assert.Equal("/my/downloads", loader2.Config.Dcc.DownloadDirectory);
        Assert.True(loader2.Config.Dcc.AutoAccept);
        Assert.Equal(50, loader2.Config.Dcc.MaxFileSizeMb);
    }

    [Fact]
    public async Task Loader_RoundTrip_PreservesHighlightPatterns()
    {
        string path = Path.Combine(_tempDir, "settings_hp.json");
        var loader = new ConfigLoader(path);
        await loader.LoadAsync();

        var updated = loader.Config with
        {
            HighlightPatterns = new List<HighlightPattern>
            {
                new("urgent", HighlightPatternKind.Literal, false),
                new(@"\bping\b", HighlightPatternKind.Regex, true),
            },
        };
        await loader.UpdateAsync(updated);

        var loader2 = new ConfigLoader(path);
        await loader2.LoadAsync();

        Assert.Equal(2, loader2.Config.HighlightPatterns.Count);
        Assert.Equal("urgent",          loader2.Config.HighlightPatterns[0].Expression);
        Assert.Equal(HighlightPatternKind.Literal, loader2.Config.HighlightPatterns[0].Kind);
        Assert.Equal(@"\bping\b",       loader2.Config.HighlightPatterns[1].Expression);
        Assert.Equal(HighlightPatternKind.Regex,   loader2.Config.HighlightPatterns[1].Kind);
        Assert.True(loader2.Config.HighlightPatterns[1].CaseSensitive);
    }

    [Fact]
    public async Task Loader_MigratesV2ToV3_AddsHighlightPatternsArray()
    {
        string path = Path.Combine(_tempDir, "settings_v2.json");

        // Write a minimal v2 config (no highlight_patterns key).
        string v2Json = """
            {
              "schema_version": 2,
              "identity": { "nick": "tester", "alt_nicks": [], "username": "tester", "realname": "tester" },
              "servers": [],
              "appearance": {
                "theme_name": "default",
                "font_family": null,
                "font_size": null,
                "show_timestamps": true,
                "timestamp_format": "HH:mm",
                "scrollback_limit": 5000
              },
              "logging": { "enabled": true, "log_directory": null },
              "advanced": {
                "flood_token_capacity": 10.0,
                "flood_drain_rate": 2.0,
                "reconnect_initial_delay_sec": 2,
                "reconnect_max_delay_sec": 300,
                "reconnect_max_attempts": 0
              },
              "aliases": {}
            }
            """;
        await File.WriteAllTextAsync(path, v2Json);

        var loader = new ConfigLoader(path);
        await loader.LoadAsync();

        Assert.Equal(AppConfig.CurrentVersion, loader.Config.SchemaVersion);
        Assert.NotNull(loader.Config.HighlightPatterns);
        Assert.Empty(loader.Config.HighlightPatterns);
        Assert.NotNull(loader.Config.Archive);
    }

    [Fact]
    public async Task Loader_MigratesV1ToV2_AddsEmptyAliases()
    {
        string path = Path.Combine(_tempDir, "settings_v1.json");

        // Write a minimal v1 config by hand (no "aliases" key).
        string v1Json = """
            {
              "schema_version": 1,
              "identity": { "nick": "tester", "alt_nicks": [], "username": "tester", "realname": "tester" },
              "servers": [],
              "appearance": {
                "theme_name": "default",
                "font_family": null,
                "font_size": null,
                "show_timestamps": true,
                "timestamp_format": "HH:mm",
                "scrollback_limit": 5000
              },
              "logging": { "enabled": true, "log_directory": null },
              "advanced": {
                "flood_token_capacity": 10.0,
                "flood_drain_rate": 2.0,
                "reconnect_initial_delay_sec": 2,
                "reconnect_max_delay_sec": 300,
                "reconnect_max_attempts": 0
              }
            }
            """;
        await File.WriteAllTextAsync(path, v1Json);

        var loader = new ConfigLoader(path);
        await loader.LoadAsync();

        // v1 → v2 → v3 all run, so final version is the current version.
        Assert.Equal(AppConfig.CurrentVersion, loader.Config.SchemaVersion);
        Assert.NotNull(loader.Config.Aliases);
        Assert.Empty(loader.Config.Aliases);
        Assert.NotNull(loader.Config.HighlightPatterns);
        Assert.Empty(loader.Config.HighlightPatterns);
    }
}
