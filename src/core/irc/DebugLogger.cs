// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.Events;

namespace DataJack.Core.Irc;

/// <summary>
/// Appends timestamped raw IRC I/O and connection lifecycle events to a file.
/// Activated when <c>advanced.log_debug</c> is non-null in settings.json.
///
/// <para>
/// Subscribes to <see cref="RawLineSent"/>, <see cref="RawLineReceived"/>,
/// <see cref="ConnectionAttempted"/>, <see cref="ConnectionEstablished"/>,
/// <see cref="ConnectionFailed"/>, and <see cref="ConnectionClosed"/> events
/// on the event bus. All handlers run on the dispatch thread; no locking is needed
/// for the writer itself.
/// </para>
/// <para>
/// <see cref="Redact"/> strips credential content from outbound lines before writing:
/// PASS and AUTHENTICATE (non-continuation) arguments are replaced with
/// <c>&lt;redacted&gt;</c>.
/// </para>
/// </summary>
public sealed class DebugLogger : IDisposable
{
    private readonly EventDispatcher _dispatcher;
    private readonly StreamWriter    _writer;
    private volatile bool            _disposed;

    // Stored as fields so each can be passed back to Unsubscribe exactly.
    private readonly Action<ConnectionAttempted>  _onAttempted;
    private readonly Action<ConnectionEstablished> _onEstablished;
    private readonly Action<ConnectionFailed>     _onFailed;
    private readonly Action<ConnectionClosed>     _onClosed;
    private readonly Action<RawLineSent>          _onSent;
    private readonly Action<RawLineReceived>      _onReceived;

    /// <param name="path">Absolute or relative path of the debug log file (append mode).</param>
    /// <param name="dispatcher">Event bus to subscribe to.</param>
    public DebugLogger(string path, EventDispatcher dispatcher)
    {
        _dispatcher = dispatcher;

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _writer = new StreamWriter(path, append: true, System.Text.Encoding.UTF8)
        {
            AutoFlush = true,
        };

        _writer.WriteLine(
            $"--- DataJack debug log opened {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} ---");

        _onAttempted   = e => Write(
            $"[CONNECTING]  {e.Server} -> {e.Address}:{e.Port} {(e.Tls ? "(TLS)" : "(plain)")}");
        _onEstablished = e => Write($"[CONNECTED]   {e.Server}");
        _onFailed      = e => Write($"[FAILED]      {e.Server}: {e.Reason}");
        _onClosed      = e => Write(e.Reason is null
            ? $"[CLOSED]      {e.Server}"
            : $"[CLOSED]      {e.Server}: {e.Reason}");
        _onSent        = e => Write($">> [{e.Server}] {Redact(e.Line)}");
        _onReceived    = e => Write($"<< [{e.Server}] {e.Line}");

        dispatcher.Subscribe(_onAttempted);
        dispatcher.Subscribe(_onEstablished);
        dispatcher.Subscribe(_onFailed);
        dispatcher.Subscribe(_onClosed);
        dispatcher.Subscribe(_onSent);
        dispatcher.Subscribe(_onReceived);
    }

    private void Write(string message)
    {
        if (_disposed) return;
        try
        {
            _writer.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} {message}");
        }
        catch
        {
            // Never let a debug-log write failure propagate to the caller.
        }
    }

    /// <summary>
    /// Replaces credential arguments in outbound IRC lines with <c>&lt;redacted&gt;</c>.
    /// <list type="bullet">
    ///   <item><c>PASS :anything</c> → <c>PASS :&lt;redacted&gt;</c></item>
    ///   <item><c>AUTHENTICATE &lt;token&gt;</c> → <c>AUTHENTICATE &lt;redacted&gt;</c>
    ///         (the <c>AUTHENTICATE +</c> continuation is left unchanged)</item>
    /// </list>
    /// All other lines are returned unmodified.
    /// </summary>
    internal static string Redact(string line)
    {
        var span = line.AsSpan().TrimStart();

        if (span.StartsWith("PASS ", StringComparison.OrdinalIgnoreCase))
            return "PASS :<redacted>";

        if (span.StartsWith("AUTHENTICATE ", StringComparison.OrdinalIgnoreCase))
        {
            // AUTHENTICATE + is the server-side "send your response" continuation — not sensitive.
            ReadOnlySpan<char> arg = span["AUTHENTICATE ".Length..].Trim();
            return arg.Equals("+", StringComparison.Ordinal) ? line : "AUTHENTICATE <redacted>";
        }

        return line;
    }

    /// <summary>
    /// Unsubscribes from the event bus, writes a close marker, and releases the file handle.
    /// Idempotent; safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _dispatcher.Unsubscribe(_onAttempted);
        _dispatcher.Unsubscribe(_onEstablished);
        _dispatcher.Unsubscribe(_onFailed);
        _dispatcher.Unsubscribe(_onClosed);
        _dispatcher.Unsubscribe(_onSent);
        _dispatcher.Unsubscribe(_onReceived);

        try
        {
            _writer.WriteLine(
                $"--- DataJack debug log closed {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} ---");
            _writer.Dispose();
        }
        catch { }
    }
}
