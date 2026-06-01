// SPDX-License-Identifier: GPL-3.0-or-later
// theme.json type definitions. See ARCHITECTURE.md §6.4 ThemeManager.
// A theme directory contains exactly one theme.json; the 16 IRC color palette,
// UI chrome colors, font settings, and message format strings are all defined there.

using System.Text.Json.Serialization;

namespace DataJack.Ui.Themes;

/// <summary>Deserialized representation of a theme.json file.</summary>
public sealed record ThemeData(
    /// <summary>Human-readable theme name used in the settings UI.</summary>
    [property: JsonPropertyName("name")]         string             Name,
    /// <summary>
    /// The 16 standard IRC color palette entries as #RRGGBB hex strings.
    /// Index 0 is white; index 1 is black; indices 2-15 follow the mIRC convention.
    /// </summary>
    [property: JsonPropertyName("irc_colors")]   IReadOnlyList<string> IrcColors,
    /// <summary>UI chrome colors.</summary>
    [property: JsonPropertyName("chrome")]       ChromeColors       Chrome,
    /// <summary>Font family name. Null uses the OS default monospace font.</summary>
    [property: JsonPropertyName("font_family")]  string?            FontFamily,
    /// <summary>Base font size in points. Null uses the OS default.</summary>
    [property: JsonPropertyName("font_size")]    double?            FontSize,
    /// <summary>C# format string for message timestamps (e.g. "HH:mm").</summary>
    [property: JsonPropertyName("timestamp_fmt")] string            TimestampFormat);

/// <summary>UI chrome color definitions. All values are #RRGGBB hex strings.</summary>
public sealed record ChromeColors(
    [property: JsonPropertyName("background")]          string Background,
    [property: JsonPropertyName("foreground")]          string Foreground,
    [property: JsonPropertyName("nicklist_bg")]         string NicklistBackground,
    [property: JsonPropertyName("input_bg")]            string InputBackground,
    [property: JsonPropertyName("input_fg")]            string InputForeground,
    [property: JsonPropertyName("tab_bg")]              string TabBackground,
    [property: JsonPropertyName("tab_fg")]              string TabForeground,
    [property: JsonPropertyName("tab_active_bg")]       string TabActiveBackground,
    [property: JsonPropertyName("tab_active_fg")]       string TabActiveForeground,
    [property: JsonPropertyName("tab_unread_fg")]       string TabUnreadForeground,
    [property: JsonPropertyName("status_bar_bg")]       string StatusBarBackground,
    [property: JsonPropertyName("status_bar_fg")]       string StatusBarForeground,
    [property: JsonPropertyName("nick_group_header_fg")] string NickGroupHeaderForeground,
    [property: JsonPropertyName("selection_bg")]        string SelectionBackground,
    [property: JsonPropertyName("link_fg")]             string LinkForeground);
