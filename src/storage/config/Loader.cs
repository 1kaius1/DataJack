// SPDX-License-Identifier: GPL-3.0-or-later
// Reads, writes, and migrates the application configuration file. See ARCHITECTURE.md §14.
// Migration functions are registered in sequence; each receives the raw JsonDocument and
// returns a mutated JsonDocument. Mutations are one-way: old keys are left under a
// "_deprecated_" prefix for one major version before removal.

using System.Text.Json;
using System.Text.Json.Nodes;
using DataJack.Platform;

namespace DataJack.Core.Storage.Config;

/// <summary>
/// Loads and persists the application configuration file.
/// Thread-safe for concurrent reads after the initial <see cref="LoadAsync"/> call.
/// Writes must be serialized by the caller.
/// </summary>
public sealed class ConfigLoader
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly string _configPath;

    /// <summary>The configuration loaded or created during <see cref="LoadAsync"/>.</summary>
    public AppConfig Config { get; private set; } = AppConfig.Default();

    /// <param name="configPath">
    /// Full path to the configuration file. Defaults to the platform-resolved path.
    /// Provide an override for testing.
    /// </param>
    public ConfigLoader(string? configPath = null)
    {
        _configPath = configPath ?? Path.Combine(Paths.ConfigDirectory, "settings.json");
    }

    /// <summary>
    /// Load the configuration from disk, running any pending migrations.
    /// If the file does not exist a default configuration is created and persisted.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_configPath))
        {
            Config = AppConfig.Default();
            await WriteInitialConfigAsync(ct).ConfigureAwait(false);
            return;
        }

        await using var stream = File.OpenRead(_configPath);
        var node = await JsonNode.ParseAsync(stream,
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip },
                cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("Configuration file is not valid JSON.");

        int onDiskVersion = node["schema_version"]?.GetValue<int>() ?? 0;

        // Run any migrations needed to bring the file up to the current schema version.
        for (int v = onDiskVersion + 1; v <= AppConfig.CurrentVersion; v++)
            node = Migrate(node, v);

        Config = node.Deserialize<AppConfig>(s_jsonOptions)
            ?? throw new InvalidDataException("Configuration file could not be deserialized.");

        if (onDiskVersion < AppConfig.CurrentVersion)
            await SaveAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Persist the current in-memory configuration to disk.</summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        string? dir = Path.GetDirectoryName(_configPath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        // Write to a temp file first, then atomically replace, to avoid corruption on crash.
        string tmp = _configPath + ".tmp";
        await using (var stream = File.Create(tmp))
            await JsonSerializer.SerializeAsync(stream, Config, s_jsonOptions, ct).ConfigureAwait(false);

        File.Move(tmp, _configPath, overwrite: true);
    }

    /// <summary>Replace the in-memory configuration and persist it to disk.</summary>
    public async Task UpdateAsync(AppConfig updated, CancellationToken ct = default)
    {
        Config = updated;
        await SaveAsync(ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------
    // Migrations
    // Each migration receives the raw JSON node tree at version (targetVersion - 1)
    // and returns the tree updated to targetVersion.
    // ---------------------------------------------------------------------------

    private static JsonNode Migrate(JsonNode node, int targetVersion) => targetVersion switch
    {
        // v1 is the initial schema; a v0 file is treated as corrupted / non-existent.
        // Replace it entirely with defaults rather than attempting to salvage it.
        1 => JsonSerializer.SerializeToNode(AppConfig.Default(), s_jsonOptions)!,
        // v2 adds the "aliases" object for user-defined command aliases (AliasManager).
        2 => MigrateToV2(node),
        // v3 adds the "highlight_patterns" array for user-configured highlight patterns.
        3 => MigrateToV3(node),
        // v4 adds the "archive" object for log rotation and gzip compression settings.
        4 => MigrateToV4(node),
        // v5 adds the "dcc" object for DCC file transfer configuration.
        5 => MigrateToV5(node),
        // v6 adds "layout_mode" to the "appearance" object for tree/tabs navigation.
        6 => MigrateToV6(node),
        // v7 adds the "away" object for away message and auto-away-on-idle settings.
        7 => MigrateToV7(node),
        // v8 adds "log_debug" to the "advanced" object for optional raw-I/O debug logging.
        8 => MigrateToV8(node),
        _ => throw new NotSupportedException($"No migration defined for schema version {targetVersion}."),
    };

    private static JsonNode MigrateToV2(JsonNode node)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj && obj["aliases"] is null)
            obj["aliases"] = new System.Text.Json.Nodes.JsonObject();

        node["schema_version"] = 2;
        return node;
    }

    private static JsonNode MigrateToV3(JsonNode node)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj && obj["highlight_patterns"] is null)
            obj["highlight_patterns"] = new System.Text.Json.Nodes.JsonArray();

        node["schema_version"] = 3;
        return node;
    }

    private static JsonNode MigrateToV4(JsonNode node)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj && obj["archive"] is null)
        {
            obj["archive"] = new System.Text.Json.Nodes.JsonObject
            {
                ["enabled"]      = true,
                ["max_age_days"] = 90,
            };
        }

        node["schema_version"] = 4;
        return node;
    }

    private static JsonNode MigrateToV5(JsonNode node)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj && obj["dcc"] is null)
        {
            obj["dcc"] = new System.Text.Json.Nodes.JsonObject
            {
                ["download_directory"] = null,
                ["auto_accept"]        = false,
                ["max_file_size_mb"]   = 0,
            };
        }

        node["schema_version"] = 5;
        return node;
    }

    private static JsonNode MigrateToV6(JsonNode node)
    {
        if (node is System.Text.Json.Nodes.JsonObject root &&
            root["appearance"] is System.Text.Json.Nodes.JsonObject appearance &&
            appearance["layout_mode"] is null)
        {
            appearance["layout_mode"] = "tabs";
        }

        node["schema_version"] = 6;
        return node;
    }

    private static JsonNode MigrateToV7(JsonNode node)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj && obj["away"] is null)
        {
            obj["away"] = new System.Text.Json.Nodes.JsonObject
            {
                ["message"]              = "Away",
                ["auto_away_enabled"]    = false,
                ["auto_away_delay_sec"]  = 600,
            };
        }

        node["schema_version"] = 7;
        return node;
    }

    private static JsonNode MigrateToV8(JsonNode node)
    {
        if (node is System.Text.Json.Nodes.JsonObject root &&
            root["advanced"] is System.Text.Json.Nodes.JsonObject advanced &&
            advanced["log_debug"] is null)
        {
            advanced["log_debug"] = null;
        }

        node["schema_version"] = 8;
        return node;
    }

    // ---------------------------------------------------------------------------
    // Initial file write
    // Writes the default config with a commented-out example for optional fields,
    // so users editing the file by hand can see what each optional key looks like.
    // ---------------------------------------------------------------------------

    private async Task WriteInitialConfigAsync(CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(_configPath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        string json = JsonSerializer.Serialize(Config, s_jsonOptions);
        json = InsertDebugLogComment(json);

        string tmp = _configPath + ".tmp";
        await File.WriteAllTextAsync(tmp, json, System.Text.Encoding.UTF8, ct)
            .ConfigureAwait(false);
        File.Move(tmp, _configPath, overwrite: true);
    }

    // Inserts two comment lines before the "log_debug" entry to show users the example
    // path format. The key itself remains as null so the JSON stays valid without
    // requiring AllowTrailingCommas.
    private static string InsertDebugLogComment(string json)
    {
        var lines  = new List<string>(json.Split('\n'));
        int target = lines.FindIndex(l => l.Contains("\"log_debug\":"));
        if (target < 0) return json;

        int    spaces = lines[target].Length - lines[target].TrimStart().Length;
        string indent = new string(' ', spaces);

        lines.InsertRange(target, new[]
        {
            $"{indent}// To enable raw-I/O debug logging, replace null with an absolute path.",
            $"{indent}// \"log_debug\": \"/tmp/datajack-debug.log\"",
        });

        return string.Join('\n', lines);
    }
}
