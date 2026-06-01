// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.Storage.Config;
using Xunit;

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
}
