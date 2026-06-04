// SPDX-License-Identifier: GPL-3.0-or-later
// Serializes and deserializes the server address book to a documented JSON format
// for transferring entries between DataJack installations. See ARCHITECTURE.md §14.4.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataJack.Core.Storage.Config;

/// <summary>
/// Serializes and deserializes the server address book to a self-describing JSON format.
/// Passwords are exported in plaintext; AES-256-GCM credential encryption is a future phase feature.
/// </summary>
public static class ServerListExport
{
    /// <summary>Format version embedded in every exported file.</summary>
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        // ServerEntry properties carry explicit [JsonPropertyName] attributes that
        // already use snake_case, so the policy here only affects ExportEnvelope.
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    // ---------------------------------------------------------------------------
    // Export
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Serialize <paramref name="entries"/> to a self-describing JSON string.
    /// The format is versioned (<c>"datajack_server_list_version"</c>) and contains
    /// an <c>"exported_at"</c> ISO 8601 timestamp. All fields including passwords are
    /// written verbatim; credential encryption is a future feature.
    /// </summary>
    /// <exception cref="ArgumentNullException">If <paramref name="entries"/> is null.</exception>
    public static string ExportToJson(IEnumerable<ServerEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var envelope = new ExportEnvelope
        {
            ListFormatVersion = FormatVersion,
            ExportedAt        = DateTimeOffset.UtcNow,
            Servers           = entries.ToList(),
        };

        return JsonSerializer.Serialize(envelope, s_options);
    }

    // ---------------------------------------------------------------------------
    // Import
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Parse a JSON string produced by <see cref="ExportToJson"/> and return the server entries.
    /// Each imported entry receives a fresh <see cref="Guid"/> to avoid ID collisions with
    /// entries already in the address book. Null list fields (<c>auto_join</c>,
    /// <c>connect_commands</c>) are coerced to empty lists; missing <c>encoding</c> defaults
    /// to <c>"UTF-8"</c>.
    /// </summary>
    /// <exception cref="JsonException">The string is not valid JSON.</exception>
    /// <exception cref="InvalidDataException">
    /// The JSON does not contain a recognizable DataJack server list
    /// (the <c>"servers"</c> array is absent or null).
    /// </exception>
    public static List<ServerEntry> ImportFromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        ExportEnvelope? envelope = JsonSerializer.Deserialize<ExportEnvelope>(json, s_options);

        if (envelope?.Servers is null)
            throw new InvalidDataException(
                "The JSON does not contain a recognizable DataJack server list " +
                "(expected a top-level \"servers\" array).");

        return envelope.Servers.Select(Sanitize).ToList();
    }

    // ---------------------------------------------------------------------------
    // Implementation helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Assign a fresh ID and coerce null list/string fields that should never be null
    /// in a live ServerEntry.
    /// </summary>
    private static ServerEntry Sanitize(ServerEntry e) => e with
    {
        Id               = Guid.NewGuid(),
        AutoJoinChannels = e.AutoJoinChannels ?? new List<string>(),
        ConnectCommands  = e.ConnectCommands  ?? new List<string>(),
        Encoding         = string.IsNullOrEmpty(e.Encoding) ? "UTF-8" : e.Encoding,
    };

    // JSON document envelope — not part of the public API surface.
    private sealed class ExportEnvelope
    {
        [JsonPropertyName("datajack_server_list_version")]
        public int ListFormatVersion { get; init; }

        [JsonPropertyName("exported_at")]
        public DateTimeOffset ExportedAt { get; init; }

        [JsonPropertyName("servers")]
        public List<ServerEntry>? Servers { get; init; }
    }
}
