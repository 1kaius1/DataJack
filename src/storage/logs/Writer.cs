// SPDX-License-Identifier: GPL-3.0-or-later
// Append-only per-buffer log writer. See ARCHITECTURE.md §12.1 and §12.2.
//
// Log format (one line per message):
//   ISO8601_TIMESTAMP <TAB> NICK_OR_SOURCE <TAB> MESSAGE_TYPE <TAB> TEXT
//
// One file per buffer per calendar day. Files are rotated automatically at midnight.
// Writes are serialized through a bounded Channel so the caller is never blocked on I/O.

using System.Text;
using System.Threading.Channels;

namespace DataJack.Core.Storage.Logs;

/// <summary>
/// Queues log entries on the calling thread and writes them to disk on a dedicated
/// background task. Dispose to flush and close all open files.
/// </summary>
public sealed class BufferLogWriter : IAsyncDisposable
{
    private readonly record struct LogEntry(
        string Server,
        string BufferId,
        DateTimeOffset Timestamp,
        string? Nick,
        string Kind,
        string Text);

    private readonly string _logDirectory;
    private readonly Channel<LogEntry> _queue;
    private readonly Task _writerTask;
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;

    // Open file handles keyed by "server|bufferId|yyyy-MM-dd".
    private readonly Dictionary<string, StreamWriter> _files = new();

    /// <param name="logDirectory">
    /// Base directory for log files. Created on first write if it does not exist.
    /// </param>
    /// <param name="queueCapacity">
    /// Bound on the number of pending log entries. When full, callers that call
    /// <see cref="Log"/> block asynchronously. 4096 is generous for normal use.
    /// </param>
    public BufferLogWriter(string logDirectory, int queueCapacity = 4096)
    {
        _logDirectory = logDirectory;
        _queue = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

        _writerTask = Task.Run(() => WriteLoopAsync(_cts.Token));
    }

    // ---------------------------------------------------------------------------
    // Public API
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Enqueue a message for logging. Returns immediately; I/O happens on the writer task.
    /// </summary>
    public void Log(string server, string bufferId, DateTimeOffset timestamp,
                    string? nick, string kind, string text)
    {
        var entry = new LogEntry(server, bufferId, timestamp, nick, kind, text);
        // Fast path: non-blocking when the channel has capacity.
        if (!_queue.Writer.TryWrite(entry))
        {
            // Fall back to async enqueue so no entry is silently dropped.
            _ = _queue.Writer.WriteAsync(entry, _cts.Token).AsTask()
                .ContinueWith(t => { /* swallow cancellation on shutdown */ },
                    TaskContinuationOptions.OnlyOnCanceled);
        }
    }

    /// <summary>Returns the expected log file path for a given buffer and date.</summary>
    public string GetLogPath(string server, string bufferId, DateOnly date)
    {
        string safeServer = MakeSafeFileName(server.Length > 0 ? server : "_");
        string safeBuf    = MakeSafeFileName(bufferId);
        string dir        = Path.Combine(_logDirectory, safeServer);
        return Path.Combine(dir, $"{date:yyyy-MM-dd}_{safeBuf}.log");
    }

    // ---------------------------------------------------------------------------
    // Writer loop (runs on a dedicated task)
    // ---------------------------------------------------------------------------

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var entry in _queue.Reader.ReadAllAsync(ct))
                await WriteEntryAsync(entry).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            // Drain remaining entries after cancellation.
            while (_queue.Reader.TryRead(out var entry))
                await WriteEntryAsync(entry).ConfigureAwait(false);

            foreach (var w in _files.Values)
            {
                await w.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                await w.DisposeAsync().ConfigureAwait(false);
            }
            _files.Clear();
        }
    }

    private async Task WriteEntryAsync(LogEntry entry)
    {
        var date = DateOnly.FromDateTime(entry.Timestamp.LocalDateTime);
        string key = $"{entry.Server}|{entry.BufferId}|{date:yyyy-MM-dd}";

        if (!_files.TryGetValue(key, out var writer))
        {
            string path = GetLogPath(entry.Server, entry.BufferId, date);
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null) Directory.CreateDirectory(dir);

            writer = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = false };
            _files[key] = writer;
            PruneOldHandles(date);
        }

        string nick = entry.Nick ?? "-";
        string line = $"{entry.Timestamp:O}\t{nick}\t{entry.Kind}\t{entry.Text}";
        await writer.WriteLineAsync(line).ConfigureAwait(false);
        await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void PruneOldHandles(DateOnly today)
    {
        var staleKeys = new List<string>();
        foreach (var k in _files.Keys)
        {
            int lastPipe = k.LastIndexOf('|');
            if (lastPipe >= 0
                && DateOnly.TryParse(k[(lastPipe + 1)..], out var d)
                && d < today)
            {
                staleKeys.Add(k);
            }
        }

        foreach (var k in staleKeys)
        {
            _files[k].Dispose();
            _files.Remove(k);
        }
    }

    private static string MakeSafeFileName(string raw)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString();
    }

    // ---------------------------------------------------------------------------
    // Disposal
    // ---------------------------------------------------------------------------

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.Complete();
        _cts.Cancel();
        try { await _writerTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _cts.Dispose();
    }
}
