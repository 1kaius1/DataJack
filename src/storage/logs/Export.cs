// SPDX-License-Identifier: GPL-3.0-or-later
// Log export to plain text and HTML. See ARCHITECTURE.md §12.4.
//
// Plain text format (one line per entry):
//   [yyyy-MM-dd HH:mm:ss] <nick> text         (message)
//   [yyyy-MM-dd HH:mm:ss] * nick text          (action)
//   [yyyy-MM-dd HH:mm:ss] -nick- text          (notice)
//   [yyyy-MM-dd HH:mm:ss] *** text             (server message)
//
// HTML format: a self-contained document with embedded CSS. Each line is a <div>
// with CSS classes for kind-specific colour coding. Special characters in nick and
// text are HTML-encoded so the output is safe to open in any browser.

using System.Net;
using System.Text;

namespace DataJack.Core.Storage.Logs;

/// <summary>Output format for <see cref="ExportManager"/>.</summary>
public enum ExportFormat
{
    /// <summary>One line per entry; human-readable, suitable for grep and archiving.</summary>
    PlainText,
    /// <summary>Self-contained HTML document with embedded CSS and colour-coded entries.</summary>
    Html,
}

/// <summary>
/// Exports a sequence of <see cref="LogEntry"/> objects to a stream in either plain-text
/// or HTML format. All timestamps are rendered in UTC.
/// </summary>
public static class ExportManager
{
    private const string HtmlHeader = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="UTF-8">
        <title>IRC Log Export</title>
        <style>
        body{font-family:monospace;background:#1e1e1e;color:#d4d4d4;padding:1em;margin:0}
        .log{line-height:1.5}
        .line{white-space:pre-wrap;margin:0;padding:1px 0}
        .ts{color:#555}
        .nick{color:#4ec9b0;font-weight:bold}
        .action .nick,.action .text{color:#9cdcfe;font-style:italic}
        .notice .nick,.notice .text{color:#dcdcaa}
        .server .text{color:#888}
        </style>
        </head>
        <body>
        <div class="log">
        """;

    private const string HtmlFooter = """
        </div>
        </body>
        </html>
        """;

    // ---------------------------------------------------------------------------
    // Public API
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Write all <paramref name="entries"/> to <paramref name="output"/> in the
    /// requested <paramref name="format"/>. The stream is not closed on return.
    /// </summary>
    public static async Task ExportAsync(
        IEnumerable<LogEntry> entries,
        Stream                output,
        ExportFormat          format,
        CancellationToken     ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(output);

        // No-BOM UTF-8: the caller should not receive a BOM marker they didn't ask for.
        // leaveOpen: true so the caller retains ownership of the stream.
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);

        if (format == ExportFormat.Html)
        {
            await writer.WriteAsync(HtmlHeader).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(FormatHtmlLine(entry)).ConfigureAwait(false);
            }
            await writer.WriteAsync(HtmlFooter).ConfigureAwait(false);
        }
        else
        {
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(FormatPlainLine(entry)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Export all <paramref name="entries"/> to a UTF-8 string.
    /// </summary>
    public static async Task<string> ExportToStringAsync(
        IEnumerable<LogEntry> entries,
        ExportFormat          format,
        CancellationToken     ct = default)
    {
        using var ms = new MemoryStream();
        await ExportAsync(entries, ms, format, ct).ConfigureAwait(false);
        return new UTF8Encoding(false).GetString(ms.ToArray());
    }

    // ---------------------------------------------------------------------------
    // Formatting helpers
    // ---------------------------------------------------------------------------

    private static string FormatPlainLine(LogEntry entry)
    {
        string ts = entry.Timestamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        return entry.Kind switch
        {
            LogEntryKind.Message       => $"[{ts}] <{entry.FromNick}> {entry.Text}",
            LogEntryKind.Action        => $"[{ts}] * {entry.FromNick} {entry.Text}",
            LogEntryKind.Notice        => $"[{ts}] -{entry.FromNick}- {entry.Text}",
            LogEntryKind.ServerMessage => $"[{ts}] *** {entry.Text}",
            _                          => $"[{ts}] {entry.Text}",
        };
    }

    private static string FormatHtmlLine(LogEntry entry)
    {
        string ts   = entry.Timestamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        string nick = WebUtility.HtmlEncode(entry.FromNick);
        string text = WebUtility.HtmlEncode(entry.Text);

        // CSS class encodes the entry kind for colour styling.
        string cssClass = entry.Kind switch
        {
            LogEntryKind.Action        => "line action",
            LogEntryKind.Notice        => "line notice",
            LogEntryKind.ServerMessage => "line server",
            _                          => "line message",
        };

        // Inner HTML differs by kind; nick is omitted for server messages.
        string inner = entry.Kind switch
        {
            LogEntryKind.Message
                => $"<span class=\"nick\">&lt;{nick}&gt;</span> <span class=\"text\">{text}</span>",
            LogEntryKind.Action
                => $"<span class=\"nick\">* {nick}</span> <span class=\"text\">{text}</span>",
            LogEntryKind.Notice
                => $"<span class=\"nick\">-{nick}-</span> <span class=\"text\">{text}</span>",
            _   // ServerMessage and unknown kinds: text only
                => $"<span class=\"text\">{text}</span>",
        };

        return $"<div class=\"{cssClass}\"><span class=\"ts\">[{ts}]</span> {inner}</div>";
    }
}
