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
            await SaveAsync(ct).ConfigureAwait(false);
            return;
        }

        await using var stream = File.OpenRead(_configPath);
        var node = await JsonNode.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false)
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
        _ => throw new NotSupportedException($"No migration defined for schema version {targetVersion}."),
    };

    private static JsonNode MigrateToV2(JsonNode node)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj && obj["aliases"] is null)
            obj["aliases"] = new System.Text.Json.Nodes.JsonObject();

        node["schema_version"] = 2;
        return node;
    }
}
