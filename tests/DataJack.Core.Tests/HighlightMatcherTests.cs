// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.Irc;
using DataJack.Core.Storage.Config;
using Xunit;

namespace DataJack.Core.Tests;

/// <summary>
/// Unit tests for <see cref="HighlightMatcher"/> covering ContainsNickAsWord, Matches
/// (Literal / Wildcard / Regex), GlobToRegex, and IsHighlight with nick + patterns.
/// </summary>
public sealed class HighlightMatcherTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static HighlightPattern Lit(string expr, bool caseSensitive = false)
        => new(expr, HighlightPatternKind.Literal, caseSensitive);

    private static HighlightPattern Wild(string expr)
        => new(expr, HighlightPatternKind.Wildcard, false);

    private static HighlightPattern Rgx(string expr, bool caseSensitive = false)
        => new(expr, HighlightPatternKind.Regex, caseSensitive);

    private static readonly IReadOnlyList<HighlightPattern> NoPatterns =
        Array.Empty<HighlightPattern>();

    // ---------------------------------------------------------------------------
    // ContainsNickAsWord
    // ---------------------------------------------------------------------------

    [Fact]
    public void ContainsNickAsWord_EmptyNick_ReturnsFalse()
        => Assert.False(HighlightMatcher.ContainsNickAsWord("hello world", ""));

    [Fact]
    public void ContainsNickAsWord_NickAlone_ReturnsTrue()
        => Assert.True(HighlightMatcher.ContainsNickAsWord("TestUser", "TestUser"));

    [Fact]
    public void ContainsNickAsWord_NickAtStart_ReturnsTrue()
        => Assert.True(HighlightMatcher.ContainsNickAsWord("TestUser: hello!", "TestUser"));

    [Fact]
    public void ContainsNickAsWord_NickAtEnd_ReturnsTrue()
        => Assert.True(HighlightMatcher.ContainsNickAsWord("hey TestUser", "TestUser"));

    [Fact]
    public void ContainsNickAsWord_NickInMiddle_ReturnsTrue()
        => Assert.True(HighlightMatcher.ContainsNickAsWord("hello TestUser, how are you?", "TestUser"));

    [Fact]
    public void ContainsNickAsWord_EmbeddedInLongerWord_ReturnsFalse()
        => Assert.False(HighlightMatcher.ContainsNickAsWord("TestUserX is here", "TestUser"));

    [Fact]
    public void ContainsNickAsWord_WithLeadingUnderscore_ReturnsFalse()
        => Assert.False(HighlightMatcher.ContainsNickAsWord("_TestUser is here", "TestUser"));

    [Fact]
    public void ContainsNickAsWord_CaseInsensitive_ReturnsTrue()
        => Assert.True(HighlightMatcher.ContainsNickAsWord("TESTUSER: hi", "TestUser"));

    [Fact]
    public void ContainsNickAsWord_RepeatedOccurrence_SecondIsWordBound_ReturnsTrue()
        // First occurrence is "_TestUser" (not a word boundary), second is "TestUser."
        => Assert.True(HighlightMatcher.ContainsNickAsWord("_TestUser then TestUser.", "TestUser"));

    // ---------------------------------------------------------------------------
    // Matches — Literal
    // ---------------------------------------------------------------------------

    [Fact]
    public void Matches_Literal_CaseInsensitive_Matches()
        => Assert.True(HighlightMatcher.Matches("Hello World", Lit("world")));

    [Fact]
    public void Matches_Literal_ExactString_Matches()
        => Assert.True(HighlightMatcher.Matches("urgent", Lit("urgent")));

    [Fact]
    public void Matches_Literal_SubstringInMiddle_Matches()
        => Assert.True(HighlightMatcher.Matches("This is urgent please", Lit("urgent")));

    [Fact]
    public void Matches_Literal_NoMatch_ReturnsFalse()
        => Assert.False(HighlightMatcher.Matches("Hello World", Lit("goodbye")));

    [Fact]
    public void Matches_Literal_EmptyExpression_ReturnsFalse()
        => Assert.False(HighlightMatcher.Matches("any text", Lit("")));

    [Fact]
    public void Matches_Literal_CaseSensitive_ExactCase_Matches()
        => Assert.True(HighlightMatcher.Matches("Hello World", Lit("World", caseSensitive: true)));

    [Fact]
    public void Matches_Literal_CaseSensitive_WrongCase_ReturnsFalse()
        => Assert.False(HighlightMatcher.Matches("Hello World", Lit("world", caseSensitive: true)));

    // ---------------------------------------------------------------------------
    // Matches — Wildcard
    // ---------------------------------------------------------------------------

    [Fact]
    public void Matches_Wildcard_StarAlone_MatchesAnyText()
        => Assert.True(HighlightMatcher.Matches("anything here", Wild("*")));

    [Fact]
    public void Matches_Wildcard_StarAtBothEnds_MatchesSubstring()
        => Assert.True(HighlightMatcher.Matches("xfoobar x", Wild("*foo*")));

    [Fact]
    public void Matches_Wildcard_StarInMiddle_MatchesSegments()
        => Assert.True(HighlightMatcher.Matches("helloworld", Wild("hello*world")));

    [Fact]
    public void Matches_Wildcard_QuestionMark_MatchesSingleChar()
        => Assert.True(HighlightMatcher.Matches("hello", Wild("hell?")));

    [Fact]
    public void Matches_Wildcard_QuestionMark_NoMatchTooShort()
        => Assert.False(HighlightMatcher.Matches("hell", Wild("hell?")));

    [Fact]
    public void Matches_Wildcard_CaseInsensitive_Matches()
        => Assert.True(HighlightMatcher.Matches("HELLO WORLD", Wild("hello*")));

    [Fact]
    public void Matches_Wildcard_NoMatch_ReturnsFalse()
        => Assert.False(HighlightMatcher.Matches("goodbye world", Wild("hello*")));

    [Fact]
    public void Matches_Wildcard_EmptyExpression_ReturnsFalse()
        => Assert.False(HighlightMatcher.Matches("text", Wild("")));

    [Fact]
    public void Matches_Wildcard_DotEscaped_LiteralDotRequired()
    {
        // The glob "a.b" should match exactly "a.b" (not "axb"), because '.' is escaped.
        Assert.True(HighlightMatcher.Matches("a.b", Wild("a.b")));
        Assert.False(HighlightMatcher.Matches("axb", Wild("a.b")));
    }

    // ---------------------------------------------------------------------------
    // Matches — Regex
    // ---------------------------------------------------------------------------

    [Fact]
    public void Matches_Regex_SimplePattern_Matches()
        => Assert.True(HighlightMatcher.Matches("hello world", Rgx("hel+o")));

    [Fact]
    public void Matches_Regex_CaseInsensitiveByDefault_Matches()
        => Assert.True(HighlightMatcher.Matches("HELLO world", Rgx("hello")));

    [Fact]
    public void Matches_Regex_CaseSensitive_WrongCase_ReturnsFalse()
        => Assert.False(HighlightMatcher.Matches("HELLO world", Rgx("hello", caseSensitive: true)));

    [Fact]
    public void Matches_Regex_CaseSensitive_CorrectCase_Matches()
        => Assert.True(HighlightMatcher.Matches("hello world", Rgx("hello", caseSensitive: true)));

    [Fact]
    public void Matches_Regex_WordBoundary_Matches()
        => Assert.True(HighlightMatcher.Matches("say hello there", Rgx(@"\bhello\b")));

    [Fact]
    public void Matches_Regex_InvalidPattern_ReturnsFalse()
        => Assert.False(HighlightMatcher.Matches("text", Rgx("[")));   // unclosed character class

    [Fact]
    public void Matches_Regex_EmptyExpression_ReturnsFalse()
        => Assert.False(HighlightMatcher.Matches("text", Rgx("")));

    // ---------------------------------------------------------------------------
    // GlobToRegex (internal)
    // ---------------------------------------------------------------------------

    [Fact]
    public void GlobToRegex_StarBecomesWildcard()
    {
        string re = HighlightMatcher.GlobToRegex("hel*");
        Assert.Contains("hel", re);
        Assert.Contains(".*", re);
    }

    [Fact]
    public void GlobToRegex_QuestionMarkBecomesDot()
    {
        string re = HighlightMatcher.GlobToRegex("hel?o");
        Assert.Contains("hel", re);
        Assert.Contains(".o", re);
        Assert.DoesNotContain("?", re);
    }

    [Fact]
    public void GlobToRegex_DotIsEscaped()
    {
        string re = HighlightMatcher.GlobToRegex("a.b");
        Assert.Contains(@"\.", re);
    }

    [Fact]
    public void GlobToRegex_IsAnchored()
    {
        string re = HighlightMatcher.GlobToRegex("foo");
        Assert.StartsWith("^", re);
        Assert.EndsWith("$", re);
    }

    // ---------------------------------------------------------------------------
    // IsHighlight — nick + patterns together
    // ---------------------------------------------------------------------------

    [Fact]
    public void IsHighlight_NullNick_EmptyPatterns_ReturnsFalse()
        => Assert.False(HighlightMatcher.IsHighlight("hello", null, NoPatterns));

    [Fact]
    public void IsHighlight_NickMatches_ReturnsTrue()
        => Assert.True(HighlightMatcher.IsHighlight("hey TestUser!", "TestUser", NoPatterns));

    [Fact]
    public void IsHighlight_NickNoMatch_EmptyPatterns_ReturnsFalse()
        => Assert.False(HighlightMatcher.IsHighlight("hey alice!", "TestUser", NoPatterns));

    [Fact]
    public void IsHighlight_LiteralPattern_Matches_ReturnsTrue()
    {
        var patterns = new[] { Lit("urgent") };
        Assert.True(HighlightMatcher.IsHighlight("this is urgent", null, patterns));
    }

    [Fact]
    public void IsHighlight_WildcardPattern_Matches_ReturnsTrue()
    {
        var patterns = new[] { Wild("prio*") };
        Assert.True(HighlightMatcher.IsHighlight("prio1: fix this", null, patterns));
    }

    [Fact]
    public void IsHighlight_RegexPattern_Matches_ReturnsTrue()
    {
        var patterns = new[] { Rgx(@"\bping\b") };
        Assert.True(HighlightMatcher.IsHighlight("ping?", null, patterns));
    }

    [Fact]
    public void IsHighlight_FirstPatternMatches_ReturnsTrue()
    {
        var patterns = new[] { Lit("foo"), Lit("bar") };
        Assert.True(HighlightMatcher.IsHighlight("foo is here", null, patterns));
    }

    [Fact]
    public void IsHighlight_SecondPatternMatches_ReturnsTrue()
    {
        var patterns = new[] { Lit("foo"), Lit("bar") };
        Assert.True(HighlightMatcher.IsHighlight("bar is here", null, patterns));
    }

    [Fact]
    public void IsHighlight_NoPatternMatchesAndNickNull_ReturnsFalse()
    {
        var patterns = new[] { Lit("foo") };
        Assert.False(HighlightMatcher.IsHighlight("nothing matching", null, patterns));
    }

    [Fact]
    public void IsHighlight_PatternMatchesButNickDoesNot_StillReturnsTrue()
    {
        // Verifies patterns are checked even when the nick doesn't appear.
        var patterns = new[] { Lit("urgent") };
        Assert.True(HighlightMatcher.IsHighlight("urgent problem here", "TestUser", patterns));
    }

    [Fact]
    public void IsHighlight_NickMatchesButPatternDoes_Not_StillReturnsTrue()
    {
        // Nick match alone is sufficient; pattern list is irrelevant.
        var patterns = new[] { Lit("nomatch") };
        Assert.True(HighlightMatcher.IsHighlight("TestUser: hello", "TestUser", patterns));
    }
}
