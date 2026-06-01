// SPDX-License-Identifier: GPL-3.0-or-later
// Avalonia rendering wrapper for IrcTextParser output. Converts IrcSpan[] into
// Avalonia Inline elements suitable for display in a TextBlock. See ARCHITECTURE.md §6.3.

using Avalonia.Controls.Documents;
using Avalonia.Media;
using DataJack.Core.Irc;
using DataJack.Ui.Themes;

namespace DataJack.Ui.Rendering;

/// <summary>
/// Converts raw IRC message text into Avalonia <see cref="Inline"/> elements.
/// Nick coloring uses a stable hash so the same nick always gets the same color.
/// </summary>
public static class IrcTextRenderer
{
    // Palette of colors used for nick coloring. Selected to be readable on dark backgrounds.
    private static readonly string[] s_nickPalette =
    {
        "#89B4FA", "#A6E3A1", "#F38BA8", "#FAB387",
        "#F9E2AF", "#94E2D5", "#CBA6F7", "#89DCEB",
    };

    /// <summary>
    /// Parse <paramref name="text"/> and return Avalonia <see cref="Inline"/> elements
    /// styled according to the active <paramref name="theme"/>.
    /// The caller is responsible for adding the inlines to a TextBlock.
    /// </summary>
    public static IEnumerable<Inline> Render(string text, ThemeManager theme)
    {
        var spans = IrcTextParser.Parse(text);
        return RenderSpans(spans, theme);
    }

    /// <summary>
    /// Returns a colored <see cref="Run"/> for a nick string. The color is derived
    /// from a stable hash of the lowercased nick so it never changes between messages.
    /// </summary>
    public static Run RenderNick(string nick)
    {
        int index = Math.Abs(nick.ToLowerInvariant().GetHashCode()) % s_nickPalette.Length;
        var brush = new SolidColorBrush(ThemeManager.ParseHex(s_nickPalette[index]));
        return new Run(nick) { Foreground = brush };
    }

    // ---------------------------------------------------------------------------
    // Internal rendering
    // ---------------------------------------------------------------------------

    private static IEnumerable<Inline> RenderSpans(IrcSpan[] spans, ThemeManager theme)
    {
        foreach (var span in spans)
        {
            var run = new Run(span.Text);

            if (span.Url is not null)
            {
                var linkColor = ThemeManager.ParseHex(theme.Theme.Chrome.LinkForeground);
                run.Foreground      = new SolidColorBrush(linkColor);
                run.TextDecorations = TextDecorations.Underline;
                // Formatting still applies on top of link styling.
                ApplyFormatting(run, span);
            }
            else
            {
                ApplyFormatting(run, span);
                ApplyColor(run, span, theme);
            }

            yield return run;
        }
    }

    private static void ApplyFormatting(Run run, IrcSpan span)
    {
        if (span.Bold)
            run.FontWeight = FontWeight.Bold;
        if (span.Italic)
            run.FontStyle = FontStyle.Italic;
        if (span.Underline && span.Strikethrough)
            run.TextDecorations = TextDecorations.Underline; // underline wins
        else if (span.Underline)
            run.TextDecorations = TextDecorations.Underline;
        else if (span.Strikethrough)
            run.TextDecorations = TextDecorations.Strikethrough;
        if (span.Monospace)
            run.FontFamily = new FontFamily("Courier New, Monospace");
    }

    private static void ApplyColor(Run run, IrcSpan span, ThemeManager theme)
    {
        Color? fg = ResolveColor(span.Foreground, theme);
        Color? bg = ResolveColor(span.Background, theme);

        if (span.Reverse) (fg, bg) = (bg, fg);

        if (fg.HasValue) run.Foreground = new SolidColorBrush(fg.Value);
        if (bg.HasValue) run.Background = new SolidColorBrush(bg.Value);
    }

    private static Color? ResolveColor(IrcColor color, ThemeManager theme)
    {
        if (!color.IsSet) return null;

        if (color.HexRgb.HasValue)
        {
            uint rgb = color.HexRgb.Value;
            return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        int idx = color.Index;
        if (idx >= 0 && idx <= 15) return theme.GetIrcColor(idx);
        if (idx >= 16 && idx <= 98)
        {
            uint? rgb = IrcTextParser.GetExtendedColorRgb(idx);
            if (rgb.HasValue)
                return Color.FromRgb((byte)(rgb.Value >> 16), (byte)(rgb.Value >> 8), (byte)rgb.Value);
        }

        return null;
    }
}
