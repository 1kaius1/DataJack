// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using DataJack.Core.Storage.Config;
using Xunit;

namespace DataJack.Core.Tests;

/// <summary>
/// Unit tests for <see cref="ServerListExport"/>.
/// These tests exercise export serialization and import deserialization/sanitization
/// without touching any UI code.
/// </summary>
public sealed class ServerListExportTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static ServerEntry MakeEntry(
        string networkName  = "TestNet",
        string address      = "irc.example.com",
        int port            = 6697,
        bool tls            = true,
        string? password    = null,
        string? nick        = null,
        string? username    = null,
        string? realname    = null,
        SaslCredentials? sasl = null,
        List<string>? autoJoin    = null,
        List<string>? connectCmds = null,
        string encoding   = "UTF-8",
        bool autoConnect  = false) => new(
        Id:               Guid.NewGuid(),
        NetworkName:      networkName,
        Address:          address,
        Port:             port,
        Tls:              tls,
        Password:         password,
        Nick:             nick,
        Username:         username,
        Realname:         realname,
        Sasl:             sasl,
        AutoJoinChannels: autoJoin     ?? new List<string>(),
        AutoConnect:      autoConnect,
        Encoding:         encoding,
        ConnectCommands:  connectCmds  ?? new List<string>());

    // ---------------------------------------------------------------------------
    // Export — structural
    // ---------------------------------------------------------------------------

    [Fact]
    public void ExportToJson_EmptyList_ReturnsValidJson()
    {
        string json = ServerListExport.ExportToJson(Enumerable.Empty<ServerEntry>());
        // Must parse without throwing.
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public void ExportToJson_ContainsFormatVersionKey()
    {
        string json = ServerListExport.ExportToJson(Enumerable.Empty<ServerEntry>());
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("datajack_server_list_version", out var ver));
        Assert.Equal(ServerListExport.FormatVersion, ver.GetInt32());
    }

    [Fact]
    public void ExportToJson_ContainsExportedAtKey()
    {
        string json = ServerListExport.ExportToJson(Enumerable.Empty<ServerEntry>());
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("exported_at", out _));
    }

    [Fact]
    public void ExportToJson_ContainsServersArray()
    {
        string json = ServerListExport.ExportToJson(new[] { MakeEntry() });
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("servers", out var arr));
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        Assert.Equal(1, arr.GetArrayLength());
    }

    // ---------------------------------------------------------------------------
    // Export — field preservation
    // ---------------------------------------------------------------------------

    [Fact]
    public void ExportToJson_PreservesNetworkName()
    {
        string json = ServerListExport.ExportToJson(new[] { MakeEntry(networkName: "Libera") });
        using var doc = JsonDocument.Parse(json);
        var entry = doc.RootElement.GetProperty("servers")[0];
        Assert.Equal("Libera", entry.GetProperty("network_name").GetString());
    }

    [Fact]
    public void ExportToJson_PreservesPortAndTls()
    {
        string json = ServerListExport.ExportToJson(new[] { MakeEntry(port: 6660, tls: false) });
        using var doc = JsonDocument.Parse(json);
        var entry = doc.RootElement.GetProperty("servers")[0];
        Assert.Equal(6660, entry.GetProperty("port").GetInt32());
        Assert.False(entry.GetProperty("tls").GetBoolean());
    }

    [Fact]
    public void ExportToJson_PreservesPasswordInPlaintext()
    {
        string json = ServerListExport.ExportToJson(new[] { MakeEntry(password: "s3cr3t") });
        using var doc = JsonDocument.Parse(json);
        var entry = doc.RootElement.GetProperty("servers")[0];
        Assert.Equal("s3cr3t", entry.GetProperty("password").GetString());
    }

    [Fact]
    public void ExportToJson_NullPassword_WrittenAsJsonNull()
    {
        string json = ServerListExport.ExportToJson(new[] { MakeEntry(password: null) });
        using var doc = JsonDocument.Parse(json);
        var entry = doc.RootElement.GetProperty("servers")[0];
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("password").ValueKind);
    }

    [Fact]
    public void ExportToJson_PreservesSaslCredentials()
    {
        var sasl = new SaslCredentials("SCRAM-SHA-512", "alice", "pass123");
        string json = ServerListExport.ExportToJson(new[] { MakeEntry(sasl: sasl) });
        using var doc = JsonDocument.Parse(json);
        var saslNode = doc.RootElement.GetProperty("servers")[0].GetProperty("sasl");
        Assert.Equal("SCRAM-SHA-512", saslNode.GetProperty("mechanism").GetString());
        Assert.Equal("alice",         saslNode.GetProperty("account").GetString());
        Assert.Equal("pass123",       saslNode.GetProperty("password").GetString());
    }

    [Fact]
    public void ExportToJson_NullSasl_WrittenAsJsonNull()
    {
        string json = ServerListExport.ExportToJson(new[] { MakeEntry(sasl: null) });
        using var doc = JsonDocument.Parse(json);
        var entry = doc.RootElement.GetProperty("servers")[0];
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("sasl").ValueKind);
    }

    [Fact]
    public void ExportToJson_PreservesAutoJoinChannels()
    {
        var autoJoin = new List<string> { "#general", "#dev" };
        string json = ServerListExport.ExportToJson(new[] { MakeEntry(autoJoin: autoJoin) });
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement.GetProperty("servers")[0].GetProperty("auto_join");
        Assert.Equal(2, arr.GetArrayLength());
        Assert.Equal("#general", arr[0].GetString());
        Assert.Equal("#dev",     arr[1].GetString());
    }

    [Fact]
    public void ExportToJson_PreservesConnectCommands()
    {
        var cmds = new List<string> { "/msg NickServ identify pass", "/join #dev" };
        string json = ServerListExport.ExportToJson(new[] { MakeEntry(connectCmds: cmds) });
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement.GetProperty("servers")[0].GetProperty("connect_commands");
        Assert.Equal(2, arr.GetArrayLength());
        Assert.Equal("/msg NickServ identify pass", arr[0].GetString());
        Assert.Equal("/join #dev",                  arr[1].GetString());
    }

    [Fact]
    public void ExportToJson_PreservesEncoding()
    {
        string json = ServerListExport.ExportToJson(new[] { MakeEntry(encoding: "Latin-1") });
        using var doc = JsonDocument.Parse(json);
        var entry = doc.RootElement.GetProperty("servers")[0];
        Assert.Equal("Latin-1", entry.GetProperty("encoding").GetString());
    }

    [Fact]
    public void ExportToJson_MultipleEntries_AllPresent()
    {
        var entries = new[] { MakeEntry("Net1"), MakeEntry("Net2"), MakeEntry("Net3") };
        string json = ServerListExport.ExportToJson(entries);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(3, doc.RootElement.GetProperty("servers").GetArrayLength());
    }

    // ---------------------------------------------------------------------------
    // Import — basic
    // ---------------------------------------------------------------------------

    [Fact]
    public void ImportFromJson_EmptyServersArray_ReturnsEmptyList()
    {
        string json = ServerListExport.ExportToJson(Enumerable.Empty<ServerEntry>());
        var result = ServerListExport.ImportFromJson(json);
        Assert.Empty(result);
    }

    [Fact]
    public void ImportFromJson_RoundTrip_PreservesNetworkName()
    {
        string json = ServerListExport.ExportToJson(new[] { MakeEntry(networkName: "Libera") });
        var result = ServerListExport.ImportFromJson(json);
        Assert.Single(result);
        Assert.Equal("Libera", result[0].NetworkName);
    }

    [Fact]
    public void ImportFromJson_RoundTrip_PreservesAllFields()
    {
        var sasl = new SaslCredentials("SCRAM-SHA-512", "alice", "s3cr3t");
        var entry = MakeEntry(
            networkName: "TestNet",
            address:     "irc.test.com",
            port:        6667,
            tls:         false,
            password:    "svpass",
            nick:        "testuser",
            username:    "tu",
            realname:    "Test User",
            sasl:        sasl,
            autoJoin:    new List<string> { "#chan" },
            connectCmds: new List<string> { "/msg NS identify" },
            encoding:    "UTF-8",
            autoConnect: true);

        string json = ServerListExport.ExportToJson(new[] { entry });
        var result = ServerListExport.ImportFromJson(json)[0];

        Assert.Equal("TestNet",            result.NetworkName);
        Assert.Equal("irc.test.com",       result.Address);
        Assert.Equal(6667,                 result.Port);
        Assert.False(result.Tls);
        Assert.Equal("svpass",             result.Password);
        Assert.Equal("testuser",           result.Nick);
        Assert.Equal("tu",                 result.Username);
        Assert.Equal("Test User",          result.Realname);
        Assert.NotNull(result.Sasl);
        Assert.Equal("SCRAM-SHA-512",      result.Sasl!.Mechanism);
        Assert.Equal("alice",              result.Sasl.Account);
        Assert.Equal("s3cr3t",             result.Sasl.Password);
        Assert.Equal(new[] { "#chan" },    result.AutoJoinChannels);
        Assert.Equal(new[] { "/msg NS identify" }, result.ConnectCommands);
        Assert.Equal("UTF-8",              result.Encoding);
        Assert.True(result.AutoConnect);
    }

    // ---------------------------------------------------------------------------
    // Import — ID assignment
    // ---------------------------------------------------------------------------

    [Fact]
    public void ImportFromJson_AssignsFreshIds_DifferentFromOriginal()
    {
        var original = MakeEntry();
        string json  = ServerListExport.ExportToJson(new[] { original });
        var imported = ServerListExport.ImportFromJson(json)[0];
        Assert.NotEqual(original.Id, imported.Id);
    }

    [Fact]
    public void ImportFromJson_MultipleEntries_EachHasUniqueId()
    {
        var entries = new[] { MakeEntry("A"), MakeEntry("B") };
        string json = ServerListExport.ExportToJson(entries);
        var result  = ServerListExport.ImportFromJson(json);
        Assert.Equal(2, result.Count);
        Assert.NotEqual(result[0].Id, result[1].Id);
    }

    // ---------------------------------------------------------------------------
    // Import — sanitization
    // ---------------------------------------------------------------------------

    [Fact]
    public void ImportFromJson_SanitizesNullAutoJoin_ReturnsEmptyList()
    {
        // Craft JSON with explicit null for auto_join.
        const string json = """
            {
              "datajack_server_list_version": 1,
              "exported_at": "2026-01-01T00:00:00Z",
              "servers": [{
                "id": "00000000-0000-0000-0000-000000000001",
                "network_name": "X",
                "address": "irc.x.com",
                "port": 6697,
                "tls": true,
                "password": null,
                "nick": null,
                "username": null,
                "realname": null,
                "sasl": null,
                "auto_join": null,
                "auto_connect": false,
                "encoding": "UTF-8",
                "connect_commands": []
              }]
            }
            """;
        var result = ServerListExport.ImportFromJson(json);
        Assert.Single(result);
        Assert.NotNull(result[0].AutoJoinChannels);
        Assert.Empty(result[0].AutoJoinChannels);
    }

    [Fact]
    public void ImportFromJson_SanitizesNullConnectCommands_ReturnsEmptyList()
    {
        const string json = """
            {
              "datajack_server_list_version": 1,
              "exported_at": "2026-01-01T00:00:00Z",
              "servers": [{
                "id": "00000000-0000-0000-0000-000000000002",
                "network_name": "Y",
                "address": "irc.y.com",
                "port": 6697,
                "tls": true,
                "password": null,
                "nick": null,
                "username": null,
                "realname": null,
                "sasl": null,
                "auto_join": [],
                "auto_connect": false,
                "encoding": "UTF-8",
                "connect_commands": null
              }]
            }
            """;
        var result = ServerListExport.ImportFromJson(json);
        Assert.Single(result);
        Assert.NotNull(result[0].ConnectCommands);
        Assert.Empty(result[0].ConnectCommands);
    }

    [Fact]
    public void ImportFromJson_SanitizesMissingEncoding_DefaultsToUtf8()
    {
        const string json = """
            {
              "datajack_server_list_version": 1,
              "exported_at": "2026-01-01T00:00:00Z",
              "servers": [{
                "id": "00000000-0000-0000-0000-000000000003",
                "network_name": "Z",
                "address": "irc.z.com",
                "port": 6697,
                "tls": true,
                "password": null,
                "nick": null,
                "username": null,
                "realname": null,
                "sasl": null,
                "auto_join": [],
                "auto_connect": false,
                "encoding": "",
                "connect_commands": []
              }]
            }
            """;
        var result = ServerListExport.ImportFromJson(json);
        Assert.Equal("UTF-8", result[0].Encoding);
    }

    // ---------------------------------------------------------------------------
    // Import — error cases
    // ---------------------------------------------------------------------------

    [Fact]
    public void ImportFromJson_InvalidJson_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() => ServerListExport.ImportFromJson("not json at all {{{"));
    }

    [Fact]
    public void ImportFromJson_MissingServersKey_ThrowsInvalidDataException()
    {
        // Valid JSON but no "servers" property.
        Assert.Throws<InvalidDataException>(() =>
            ServerListExport.ImportFromJson("""{ "datajack_server_list_version": 1 }"""));
    }
}
