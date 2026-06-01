// SPDX-License-Identifier: GPL-3.0-or-later
// IRC text formatting parser. Converts a raw IRC message string into a sequence of
// IrcSpan values, each carrying the text content and its current formatting state.
// This is pure logic with no UI dependency; the rendering layer converts IrcSpan[]
// into Avalonia inlines. See ARCHITECTURE.md §6.3.

using System.Text.RegularExpressions;

namespace DataJack.Core.Irc;

// ---------------------------------------------------------------------------
// Color representation
// ---------------------------------------------------------------------------

/// <summary>
/// An IRC text color: either a palette index (0-98) or a 24-bit hex value.
/// Unset colors are represented by <see cref="None"/>.
/// </summary>
public readonly record struct IrcColor(int Index, uint? HexRgb)
{
    /// <summary>No color specified; use the theme default.</summary>
    public static IrcColor None { get; } = new(-1, null);

    /// <summary>A palette-indexed color (0-15 theme-defined, 16-98 fixed).</summary>
    public static IrcColor FromIndex(int index) => new(index, null);

    /// <summary>A 24-bit RGB hex color from the IRCv3 \x04 extension.</summary>
    public static IrcColor FromHex(uint rgb) => new(-1, rgb);

    /// <summary>True when a color value is actually set.</summary>
    public bool IsSet => Index >= 0 || HexRgb.HasValue;
}

// ---------------------------------------------------------------------------
// Span
// ---------------------------------------------------------------------------

/// <summary>
/// One contiguous run of text with uniform formatting. Produced by
/// <see cref="IrcTextParser.Parse"/>; consumed by the Avalonia rendering layer.
/// </summary>
public readonly record struct IrcSpan(
    string     Text,
    bool       Bold,
    bool       Italic,
    bool       Underline,
    bool       Strikethrough,
    bool       Monospace,
    bool       Reverse,
    IrcColor   Foreground,
    IrcColor   Background,
    string?    Url);

// ---------------------------------------------------------------------------
// Parser
// ---------------------------------------------------------------------------

/// <summary>
/// Parses IRC-formatted text into a list of <see cref="IrcSpan"/> values.
/// All mIRC control codes and the IRCv3 hex-color extension are handled.
/// URL detection splits spans so that URLs are always their own span.
/// </summary>
public static class IrcTextParser
{
    // mIRC control character byte values.
    private const char Bold        = '\x02';
    private const char Color       = '\x03';
    private const char HexColor    = '\x04';
    private const char Reset       = '\x0F';
    private const char Monospace   = '\x11';
    private const char Reverse     = '\x16';
    private const char Italic      = '\x1D';
    private const char Underline   = '\x1F';
    private const char Strikethrough = '\x1E';

    private static readonly Regex s_urlRegex = new(
        @"(?:https?|ftp|ircs?|irc6?)://[^\s<>""]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parse <paramref name="text"/> into an array of styled spans.
    /// Returns a single plain span when the input contains no formatting codes.
    /// </summary>
    public static IrcSpan[] Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<IrcSpan>();

        var spans = new List<IrcSpan>();
        var sb = new System.Text.StringBuilder();

        bool bold = false, italic = false, underline = false,
             strikethrough = false, monospace = false, reverse = false;
        IrcColor fg = IrcColor.None, bg = IrcColor.None;

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            if (c == Bold || c == Italic || c == Underline || c == Strikethrough
                          || c == Monospace || c == Reverse || c == Reset
                          || c == Color || c == HexColor)
            {
                FlushBuffer(spans, sb, bold, italic, underline, strikethrough, monospace, reverse, fg, bg);

                switch (c)
                {
                    case Bold:          bold          = !bold;          i++; break;
                    case Italic:        italic        = !italic;        i++; break;
                    case Underline:     underline     = !underline;     i++; break;
                    case Strikethrough: strikethrough = !strikethrough; i++; break;
                    case Monospace:     monospace     = !monospace;     i++; break;
                    case Reverse:       reverse       = !reverse;       i++; break;

                    case Reset:
                        bold = italic = underline = strikethrough = monospace = reverse = false;
                        fg = bg = IrcColor.None;
                        i++;
                        break;

                    case Color:
                        i = ParseMircColor(text, i + 1, out fg, out bg);
                        break;

                    case HexColor:
                        i = ParseHexColor(text, i + 1, out fg, out bg);
                        break;
                }
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        FlushBuffer(spans, sb, bold, italic, underline, strikethrough, monospace, reverse, fg, bg);

        return SplitUrls(spans);
    }

    // Emit the current buffer as a span (or nothing if the buffer is empty).
    private static void FlushBuffer(
        List<IrcSpan> spans, System.Text.StringBuilder sb,
        bool bold, bool italic, bool underline, bool strikethrough,
        bool monospace, bool reverse,
        IrcColor fg, IrcColor bg)
    {
        if (sb.Length == 0) return;
        spans.Add(new IrcSpan(sb.ToString(), bold, italic, underline, strikethrough, monospace, reverse, fg, bg, null));
        sb.Clear();
    }

    // ---------------------------------------------------------------------------
    // mIRC color: \x03[fg[,bg]]
    // ---------------------------------------------------------------------------

    private static int ParseMircColor(string text, int i, out IrcColor fg, out IrcColor bg)
    {
        fg = IrcColor.None;
        bg = IrcColor.None;

        if (i >= text.Length || !char.IsAsciiDigit(text[i]))
            return i; // bare \x03 = color reset (no index)

        int fgIndex = ConsumeColorIndex(text, ref i);
        fg = IrcColor.FromIndex(fgIndex);

        if (i < text.Length && text[i] == ',' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]))
        {
            i++; // skip comma
            int bgIndex = ConsumeColorIndex(text, ref i);
            bg = IrcColor.FromIndex(bgIndex);
        }

        return i;
    }

    // Reads 1 or 2 ASCII digits and returns the numeric value; advances i past them.
    private static int ConsumeColorIndex(string text, ref int i)
    {
        int value = text[i++] - '0';
        if (i < text.Length && char.IsAsciiDigit(text[i]))
            value = value * 10 + (text[i++] - '0');
        return value;
    }

    // ---------------------------------------------------------------------------
    // IRCv3 hex color: \x04RRGGBB[,RRGGBB]
    // ---------------------------------------------------------------------------

    private static int ParseHexColor(string text, int i, out IrcColor fg, out IrcColor bg)
    {
        fg = IrcColor.None;
        bg = IrcColor.None;

        if (!TryConsumeHex6(text, ref i, out uint fgRgb)) return i;
        fg = IrcColor.FromHex(fgRgb);

        if (i < text.Length && text[i] == ',')
        {
            i++;
            if (TryConsumeHex6(text, ref i, out uint bgRgb))
                bg = IrcColor.FromHex(bgRgb);
        }

        return i;
    }

    private static bool TryConsumeHex6(string text, ref int i, out uint rgb)
    {
        rgb = 0;
        if (i + 6 > text.Length) return false;

        if (!uint.TryParse(text.AsSpan(i, 6), System.Globalization.NumberStyles.HexNumber, null, out rgb))
            return false;

        i += 6;
        return true;
    }

    // ---------------------------------------------------------------------------
    // URL splitting
    // ---------------------------------------------------------------------------

    // Walk the span list and split any span whose text contains a URL into
    // pre-URL, URL, and post-URL sub-spans.
    private static IrcSpan[] SplitUrls(List<IrcSpan> spans)
    {
        bool hasUrls = false;
        foreach (var s in spans)
            if (s_urlRegex.IsMatch(s.Text)) { hasUrls = true; break; }

        if (!hasUrls) return spans.ToArray();

        var result = new List<IrcSpan>(spans.Count + 4);
        foreach (var span in spans)
        {
            var match = s_urlRegex.Match(span.Text);
            if (!match.Success)
            {
                result.Add(span);
                continue;
            }

            int pos = 0;
            while (match.Success)
            {
                if (match.Index > pos)
                    result.Add(span with { Text = span.Text[pos..match.Index] });

                result.Add(span with { Text = match.Value, Url = match.Value });
                pos = match.Index + match.Length;
                match = match.NextMatch();
            }

            if (pos < span.Text.Length)
                result.Add(span with { Text = span.Text[pos..] });
        }

        return result.ToArray();
    }

    // ---------------------------------------------------------------------------
    // mIRC extended color palette (colors 16-98)
    // This table is defined by the IRCv3 working group and is fixed regardless of theme.
    // ---------------------------------------------------------------------------

    private static readonly uint[] s_extendedColors = new uint[]
    {
        // Colors 16-27: 6-step red channel with green/blue sweep
        0x470000, 0x472100, 0x474700, 0x324700, 0x004700, 0x00472C,
        0x004747, 0x002747, 0x000047, 0x2E0047, 0x470047, 0x47002A,
        // 28-39
        0x740000, 0x743A00, 0x747400, 0x517400, 0x007400, 0x007449,
        0x007474, 0x004074, 0x000074, 0x4B0074, 0x740074, 0x740045,
        // 40-51
        0xB50000, 0xB56300, 0xB5B500, 0x7DB500, 0x00B500, 0x00B571,
        0x00B5B5, 0x0063B5, 0x0000B5, 0x7500B5, 0xB500B5, 0xB5006B,
        // 52-63
        0xFF0000, 0xFF8C00, 0xFFFF00, 0xB2FF00, 0x00FF00, 0x00FFA0,
        0x00FFFF, 0x008CFF, 0x0000FF, 0xA500FF, 0xFF00FF, 0xFF0098,
        // 64-75
        0xFF5959, 0xFFB459, 0xFFFF71, 0xCFFF60, 0x6FFF6F, 0x65FFC9,
        0x6DFFFF, 0x59B4FF, 0x5959FF, 0xC459FF, 0xFF66FF, 0xFF59BC,
        // 76-87
        0xFF9C9C, 0xFFD39C, 0xFFFF9C, 0xE2FF9C, 0x9CFF9C, 0x9CFFDB,
        0x9CFFFF, 0x9CD3FF, 0x9C9CFF, 0xDC9CFF, 0xFF9CFF, 0xFF94D3,
        // 88-98: grayscale
        0x000000, 0x131313, 0x282828, 0x363636, 0x4D4D4D, 0x656565,
        0x818181, 0x9F9F9F, 0xBCBCBC, 0xE2E2E2, 0xFFFFFF,
    };

    /// <summary>
    /// Returns the fixed RGB value for an extended color (index 16-98).
    /// Returns null for out-of-range indices.
    /// </summary>
    public static uint? GetExtendedColorRgb(int index)
    {
        int tableIndex = index - 16;
        if (tableIndex < 0 || tableIndex >= s_extendedColors.Length) return null;
        return s_extendedColors[tableIndex];
    }
}
