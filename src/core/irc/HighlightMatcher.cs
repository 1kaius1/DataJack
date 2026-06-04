// SPDX-License-Identifier: GPL-3.0-or-later
// Highlight pattern matching logic: literal, wildcard (glob), and regex patterns.
// The current nick is always an additional implicit whole-word match.
// See ARCHITECTURE.md §15.3.

using System.Text;
using System.Text.RegularExpressions;
using DataJack.Core.Storage.Config;

namespace DataJack.Core.Irc;

/// <summary>
/// Stateless, thread-safe utility for evaluating highlight patterns against IRC message text.
///
/// Matching rules:
/// <list type="bullet">
///   <item><see cref="HighlightPatternKind.Literal"/> — case-insensitive (default) or
///     case-sensitive substring search. An empty expression always returns false.</item>
///   <item><see cref="HighlightPatternKind.Wildcard"/> — glob-style: <c>*</c> matches any
///     sequence of characters, <c>?</c> matches exactly one. Always case-insensitive.</item>
///   <item><see cref="HighlightPatternKind.Regex"/> — full .NET regex. Case sensitivity is
///     controlled by <see cref="HighlightPattern.CaseSensitive"/>. An invalid regex expression
///     silently returns false rather than throwing.</item>
/// </list>
///
/// The current nick is always an implicit highlight: it is checked as a whole word
/// (bounded by non-alphanumeric, non-underscore characters or string edges), case-insensitively,
/// regardless of the configured pattern list.
/// </summary>
public static class HighlightMatcher
{
    // ---------------------------------------------------------------------------
    // Primary API
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Returns <c>true</c> when any of the following is true:
    /// <list type="bullet">
    ///   <item><paramref name="currentNick"/> is non-null and appears in <paramref name="text"/>
    ///     as a whole word (case-insensitive).</item>
    ///   <item>Any element of <paramref name="patterns"/> matches <paramref name="text"/>.</item>
    /// </list>
    /// </summary>
    /// <param name="text">The message text to test.</param>
    /// <param name="currentNick">The local user's current nick, or null when not registered.</param>
    /// <param name="patterns">User-configured highlight patterns (may be empty).</param>
    public static bool IsHighlight(
        string text,
        string? currentNick,
        IReadOnlyList<HighlightPattern> patterns)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(patterns);

        if (currentNick is not null && ContainsNickAsWord(text, currentNick))
            return true;

        foreach (var pattern in patterns)
        {
            if (Matches(text, pattern))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Test a single <paramref name="pattern"/> against <paramref name="text"/>.
    /// Returns <c>false</c> for empty/whitespace expressions and for invalid regex patterns
    /// (no exception is thrown).
    /// </summary>
    public static bool Matches(string text, HighlightPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(pattern);

        if (string.IsNullOrEmpty(pattern.Expression)) return false;

        return pattern.Kind switch
        {
            HighlightPatternKind.Literal  => MatchesLiteral(text, pattern),
            HighlightPatternKind.Wildcard => MatchesWildcard(text, pattern),
            HighlightPatternKind.Regex    => MatchesRegex(text, pattern),
            _                             => false,
        };
    }

    // ---------------------------------------------------------------------------
    // Implementation — per-kind matchers
    // ---------------------------------------------------------------------------

    private static bool MatchesLiteral(string text, HighlightPattern pattern)
    {
        var comparison = pattern.CaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        return text.Contains(pattern.Expression, comparison);
    }

    private static bool MatchesWildcard(string text, HighlightPattern pattern)
    {
        // Wildcards are always case-insensitive; CaseSensitive is ignored.
        try
        {
            return Regex.IsMatch(
                text,
                GlobToRegex(pattern.Expression),
                RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(100));
        }
        catch
        {
            return false;
        }
    }

    private static bool MatchesRegex(string text, HighlightPattern pattern)
    {
        var options = pattern.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
        try
        {
            return Regex.IsMatch(text, pattern.Expression, options, TimeSpan.FromMilliseconds(100));
        }
        catch (RegexParseException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------------------
    // Glob → regex conversion
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Convert a glob pattern (<c>*</c> and <c>?</c> wildcards) to an anchored regex string.
    /// All other regex metacharacters in the glob are escaped.
    /// </summary>
    internal static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder("^", glob.Length + 4);
        foreach (char c in glob)
        {
            switch (c)
            {
                case '*': sb.Append(".*"); break;
                case '?': sb.Append('.'); break;
                // Escape all other regex metacharacters.
                case '.': case '+': case '(': case ')':
                case '[': case ']': case '{': case '}':
                case '^': case '$': case '|': case '\\':
                    sb.Append('\\'); sb.Append(c); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('$');
        return sb.ToString();
    }

    // ---------------------------------------------------------------------------
    // Whole-word nick detection
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Returns <c>true</c> if <paramref name="nick"/> appears in <paramref name="text"/> as a
    /// whole word: both adjacent characters (or string edges) must be non-alphanumeric and
    /// non-underscore. Comparison is case-insensitive. An empty <paramref name="nick"/> always
    /// returns <c>false</c>.
    /// </summary>
    public static bool ContainsNickAsWord(string text, string nick)
    {
        if (nick.Length == 0) return false;

        int idx = 0;
        while (true)
        {
            idx = text.IndexOf(nick, idx, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;

            bool leftBound  = idx == 0 || !IsWordChar(text[idx - 1]);
            int  end        = idx + nick.Length;
            bool rightBound = end >= text.Length || !IsWordChar(text[end]);

            if (leftBound && rightBound) return true;
            idx++;
        }
    }

    // Letters, digits, and underscores are treated as word-interior characters,
    // matching typical IRC nick character sets.
    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
