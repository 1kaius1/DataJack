// SPDX-License-Identifier: GPL-3.0-or-later
// ThemeManager: loads a theme.json from disk, falls back to the built-in default,
// and hot-reloads when the file changes. See ARCHITECTURE.md §6.4.

using System.Reflection;
using System.Text.Json;
using Avalonia.Media;
using DataJack.Platform;

namespace DataJack.Ui.Themes;

/// <summary>
/// Loads, caches, and hot-reloads themes. Owns the active <see cref="ThemeData"/>
/// and exposes parsed <see cref="Color"/> values for direct use by UI controls.
/// </summary>
public sealed class ThemeManager : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    // Embedded resource name: DataJack.Assets.default.theme.json
    private const string EmbeddedThemeName = "DataJack.Assets.default.theme.json";

    private FileSystemWatcher? _watcher;
    private string? _themeDirectory;

    /// <summary>The currently active theme data.</summary>
    public ThemeData Theme { get; private set; } = LoadEmbeddedDefault();

    /// <summary>Raised on the calling thread when the active theme is reloaded.</summary>
    public event Action<ThemeData>? ThemeChanged;

    // ---------------------------------------------------------------------------
    // Loading
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Load the theme named <paramref name="themeName"/> from the user's theme directory.
    /// Falls back to the built-in default on any error.
    /// </summary>
    public void Load(string themeName)
    {
        string themeDir = Path.Combine(Paths.ThemesDirectory, themeName);
        string themeFile = Path.Combine(themeDir, "theme.json");

        ThemeData? loaded = TryLoadFromFile(themeFile);
        Theme = loaded ?? LoadEmbeddedDefault();

        SetupWatcher(themeDir, themeFile);
    }

    private static ThemeData? TryLoadFromFile(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<ThemeData>(stream, s_jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static ThemeData LoadEmbeddedDefault()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(EmbeddedThemeName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{EmbeddedThemeName}' not found. " +
                "Check that the theme.json is included as an EmbeddedResource in DataJack.csproj.");

        return JsonSerializer.Deserialize<ThemeData>(stream, s_jsonOptions)
            ?? throw new InvalidDataException("Embedded default theme.json could not be deserialized.");
    }

    // ---------------------------------------------------------------------------
    // Hot-reload
    // ---------------------------------------------------------------------------

    private void SetupWatcher(string directory, string filePath)
    {
        _watcher?.Dispose();
        _watcher = null;
        _themeDirectory = directory;

        if (!Directory.Exists(directory)) return;

        _watcher = new FileSystemWatcher(directory, "theme.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };

        _watcher.Changed += (_, _) => OnThemeFileChanged(filePath);
    }

    private void OnThemeFileChanged(string path)
    {
        // Brief delay to avoid reading a partially-written file.
        Thread.Sleep(100);
        var loaded = TryLoadFromFile(path);
        if (loaded is null) return;
        Theme = loaded;
        ThemeChanged?.Invoke(Theme);
    }

    // ---------------------------------------------------------------------------
    // Config overrides
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Silently replaces the active theme's timestamp format string with a value
    /// derived from the application config (e.g. for the 12/24-hour toggle).
    /// Does not fire <see cref="ThemeChanged"/>; the caller is responsible for
    /// triggering a redraw via <c>ApplyTheme</c> if messages are already displayed.
    /// </summary>
    public void SetTimestampFormat(string format)
    {
        Theme = Theme with { TimestampFormat = format };
    }

    // ---------------------------------------------------------------------------
    // Color helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Returns the parsed <see cref="Color"/> for the given IRC palette index (0-15).
    /// Returns the theme foreground color for out-of-range indices.
    /// </summary>
    public Color GetIrcColor(int index)
    {
        var palette = Theme.IrcColors;
        if (index < 0 || index >= palette.Count) return ParseHex(Theme.Chrome.Foreground);
        return ParseHex(palette[index]);
    }

    /// <summary>Parses a #RRGGBB hex string into an Avalonia <see cref="Color"/>.</summary>
    public static Color ParseHex(string hex)
    {
        ReadOnlySpan<char> s = hex.AsSpan().TrimStart('#');
        if (s.Length != 6) return Colors.White;
        if (!uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
            return Colors.White;
        return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    /// <inheritdoc/>
    public void Dispose() => _watcher?.Dispose();
}
