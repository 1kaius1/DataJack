// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.Irc;
using Xunit;

namespace DataJack.Core.Tests;

public sealed class IrcTextParserTests
{
    // ---------------------------------------------------------------------------
    // Plain text (no formatting)
    // ---------------------------------------------------------------------------

    [Fact]
    public void Parse_PlainText_ReturnsSingleSpan()
    {
        var spans = IrcTextParser.Parse("hello world");
        Assert.Single(spans);
        Assert.Equal("hello world", spans[0].Text);
        Assert.False(spans[0].Bold);
        Assert.False(spans[0].Italic);
        Assert.False(spans[0].Underline);
    }

    [Fact]
    public void Parse_EmptyString_ReturnsEmpty()
    {
        Assert.Empty(IrcTextParser.Parse(string.Empty));
    }

    [Fact]
    public void Parse_NullEquivalent_ReturnsEmpty()
    {
        Assert.Empty(IrcTextParser.Parse(""));
    }

    // ---------------------------------------------------------------------------
    // Formatting codes
    // ---------------------------------------------------------------------------

    [Fact]
    public void Parse_Bold_SetsBoldFlag()
    {
        // \x02 hello \x02
        var spans = IrcTextParser.Parse("\x02hello\x02");
        var bold = spans.First(s => s.Text == "hello");
        Assert.True(bold.Bold);
    }

    [Fact]
    public void Parse_Italic_SetsItalicFlag()
    {
        var spans = IrcTextParser.Parse("\x1Ditalic\x1D");
        Assert.True(spans.First(s => s.Text == "italic").Italic);
    }

    [Fact]
    public void Parse_Underline_SetsUnderlineFlag()
    {
        var spans = IrcTextParser.Parse("\x1Funderline\x1F");
        Assert.True(spans.First(s => s.Text == "underline").Underline);
    }

    [Fact]
    public void Parse_Strikethrough_SetsStrikethroughFlag()
    {
        var spans = IrcTextParser.Parse("\x1Estrike\x1E");
        Assert.True(spans.First(s => s.Text == "strike").Strikethrough);
    }

    [Fact]
    public void Parse_Monospace_SetsMonospaceFlag()
    {
        var spans = IrcTextParser.Parse("\x11mono\x11");
        Assert.True(spans.First(s => s.Text == "mono").Monospace);
    }

    [Fact]
    public void Parse_Reverse_SetsReverseFlag()
    {
        var spans = IrcTextParser.Parse("\x16rev\x16");
        Assert.True(spans.First(s => s.Text == "rev").Reverse);
    }

    [Fact]
    public void Parse_Reset_ClearsAllFormatting()
    {
        var spans = IrcTextParser.Parse("\x02bold\x0Fnormal");
        var normal = spans.First(s => s.Text == "normal");
        Assert.False(normal.Bold);
        Assert.False(normal.Italic);
    }

    [Fact]
    public void Parse_MixedFormatting_MultipleFlagsSet()
    {
        // Use concatenation: "\x1D" followed by 'b' would be consumed as U+01DB by the greedy
        // C# hex escape parser, so keep the control char in its own string literal.
        var spans = IrcTextParser.Parse("\x02\x1D" + "bold-italic" + "\x0F");
        var span = spans.First(s => s.Text == "bold-italic");
        Assert.True(span.Bold);
        Assert.True(span.Italic);
    }

    // ---------------------------------------------------------------------------
    // mIRC color codes (\x03)
    // ---------------------------------------------------------------------------

    [Fact]
    public void Parse_ColorCode_SetsColorIndex()
    {
        // \x03 followed by digits: use concatenation to avoid the C# greedy hex escape
        // consuming the digit characters as part of the escape sequence.
        var spans = IrcTextParser.Parse("\x03" + "04red text" + "\x03");
        var colored = spans.First(s => s.Text == "red text");
        Assert.True(colored.Foreground.IsSet);
        Assert.Equal(4, colored.Foreground.Index);
    }

    [Fact]
    public void Parse_ColorCodeWithBackground_SetsBothColors()
    {
        var spans = IrcTextParser.Parse("\x03" + "0,1white-on-black" + "\x03");
        var colored = spans.First(s => s.Text == "white-on-black");
        Assert.Equal(0, colored.Foreground.Index);
        Assert.Equal(1, colored.Background.Index);
    }

    [Fact]
    public void Parse_BareColorCode_ResetsColor()
    {
        // \x03 with no digits resets color.
        var spans = IrcTextParser.Parse("\x03" + "04colored\x03plain");
        var plain = spans.First(s => s.Text == "plain");
        Assert.False(plain.Foreground.IsSet);
    }

    [Fact]
    public void Parse_TwoDigitColorIndex_Parsed()
    {
        var spans = IrcTextParser.Parse("\x03" + "15gray" + "\x03");
        Assert.Equal(15, spans.First(s => s.Text == "gray").Foreground.Index);
    }

    // ---------------------------------------------------------------------------
    // IRCv3 hex color (\x04)
    // ---------------------------------------------------------------------------

    [Fact]
    public void Parse_HexColor_SetsHexRgb()
    {
        // \x04 followed by 'F' would be consumed as part of the C# hex escape;
        // keep the control char in its own literal.
        var spans = IrcTextParser.Parse("\x04" + "FF0000red" + "\x04");
        var s = spans.First(sp => sp.Text == "red");
        Assert.True(s.Foreground.HexRgb.HasValue);
        Assert.Equal(0xFF0000u, s.Foreground.HexRgb!.Value);
    }

    [Fact]
    public void Parse_HexColorWithBackground_SetsBoth()
    {
        var spans = IrcTextParser.Parse("\x04" + "FFFFFF,000000text" + "\x04");
        var s = spans.First(sp => sp.Text == "text");
        Assert.Equal(0xFFFFFFu, s.Foreground.HexRgb!.Value);
        Assert.Equal(0x000000u, s.Background.HexRgb!.Value);
    }

    // ---------------------------------------------------------------------------
    // URL detection
    // ---------------------------------------------------------------------------

    [Fact]
    public void Parse_PlainUrl_CreatesUrlSpan()
    {
        var spans = IrcTextParser.Parse("visit https://example.com now");
        var url = spans.FirstOrDefault(s => s.Url is not null);
        Assert.NotNull(url.Url);
        Assert.Equal("https://example.com", url.Url);
    }

    [Fact]
    public void Parse_UrlInMiddle_SplitsIntoThreeSpans()
    {
        var spans = IrcTextParser.Parse("see https://x.org ok");
        Assert.Equal(3, spans.Length);
        Assert.Equal("see ", spans[0].Text);
        Assert.Equal("https://x.org", spans[1].Text);
        Assert.Equal(" ok", spans[2].Text);
    }

    [Fact]
    public void Parse_IrcUrl_RecognizedAsUrl()
    {
        var spans = IrcTextParser.Parse("irc://irc.libera.chat/datajack");
        Assert.NotNull(spans.FirstOrDefault(s => s.Url is not null).Url);
    }

    [Fact]
    public void Parse_NonUrl_NoUrlSpan()
    {
        var spans = IrcTextParser.Parse("no link here");
        Assert.All(spans, s => Assert.Null(s.Url));
    }

    // ---------------------------------------------------------------------------
    // Extended color palette
    // ---------------------------------------------------------------------------

    [Fact]
    public void GetExtendedColorRgb_Index16_ReturnsExpectedValue()
    {
        uint? rgb = IrcTextParser.GetExtendedColorRgb(16);
        Assert.True(rgb.HasValue);
        Assert.Equal(0x470000u, rgb!.Value);
    }

    [Fact]
    public void GetExtendedColorRgb_Index98_ReturnsExpectedValue()
    {
        uint? rgb = IrcTextParser.GetExtendedColorRgb(98);
        Assert.True(rgb.HasValue);
        Assert.Equal(0xFFFFFFu, rgb!.Value);
    }

    [Fact]
    public void GetExtendedColorRgb_OutOfRange_ReturnsNull()
    {
        Assert.Null(IrcTextParser.GetExtendedColorRgb(99));
        Assert.Null(IrcTextParser.GetExtendedColorRgb(-1));
        Assert.Null(IrcTextParser.GetExtendedColorRgb(15));
    }
}
