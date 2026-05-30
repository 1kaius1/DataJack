// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using DataJack.Core.Events;
using DataJack.Net;

namespace DataJack.Core.Irc;

/// <summary>
/// Raw TCP/TLS connection to one IRC server. Responsibilities:
///   - Reconstruct newline-delimited lines from the byte stream.
///   - Respond to PING immediately, before the event bus, to prevent disconnect.
///   - Publish RawLineReceived for every inbound line and RawLineSent for every outbound line.
///   - Publish ConnectionAttempted, ConnectionEstablished, ConnectionFailed, ConnectionClosed.
///
/// This class has no knowledge of the IRC protocol beyond PING/PONG.
/// All parsing is done downstream by IRCParser.
/// </summary>
public sealed class IRCConnection : IAsyncDisposable
{
    /// <summary>
    /// Maximum bytes per line. IRC spec caps at 512; IRCv3 with tags allows up to 8192.
    /// Lines exceeding this limit have trailing bytes silently discarded.
    /// </summary>
    private const int MaxLineBytes = 8192;

    private readonly string _serverId;
    private readonly INetworkProvider _networkProvider;
    private readonly EventDispatcher _dispatcher;
    private readonly Encoding _encoding;

    // Guards concurrent writes from SendLineAsync (PONG from receive loop, user commands, etc.)
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private Stream? _stream;
    private Task? _receiveTask;
    private CancellationTokenSource? _receiveCts;
    private bool _disposed;

    /// <param name="serverId">
    /// Stable identifier for this server (network name or address string), used as the
    /// <c>Server</c> field in all events emitted by this connection.
    /// </param>
    /// <param name="encoding">
    /// Character encoding for line decoding/encoding. Defaults to UTF-8 with replacement
    /// characters for invalid byte sequences; never throws on bad bytes.
    /// </param>
    public IRCConnection(
        string serverId,
        INetworkProvider networkProvider,
        EventDispatcher dispatcher,
        Encoding? encoding = null)
    {
        _serverId = serverId;
        _networkProvider = networkProvider;
        _dispatcher = dispatcher;
        _encoding = encoding
            ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    }

    /// <summary>
    /// Open the connection and start the background receive loop.
    /// Publishes ConnectionAttempted → ConnectionEstablished (or ConnectionFailed).
    /// </summary>
    public async Task ConnectAsync(NetworkEndpoint endpoint, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stream is not null)
            throw new InvalidOperationException("Already connected.");

        await _dispatcher.PublishAsync(
            new ConnectionAttempted(_serverId, endpoint.Host, endpoint.Port, endpoint.UseTls),
            EventPriority.Critical, ct).ConfigureAwait(false);

        try
        {
            _stream = await _networkProvider.ConnectAsync(endpoint, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _dispatcher.PublishAsync(
                new ConnectionFailed(_serverId, ex.Message),
                EventPriority.Critical).ConfigureAwait(false);
            throw;
        }

        await _dispatcher.PublishAsync(
            new ConnectionEstablished(_serverId),
            EventPriority.Critical, ct).ConfigureAwait(false);

        _receiveCts = new CancellationTokenSource();
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));
    }

    /// <summary>
    /// Send a single raw IRC line. The caller must not include the trailing CRLF.
    /// Lines longer than the protocol limit are silently truncated at the byte level.
    /// Thread-safe; may be called from any thread.
    /// </summary>
    public async Task SendLineAsync(string line, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stream is null)
            throw new InvalidOperationException("Not connected.");

        var encoded = _encoding.GetBytes(line);

        // Enforce protocol line length (exclude the 2-byte CRLF from the budget).
        if (encoded.Length > MaxLineBytes - 2)
            encoded = encoded[..(MaxLineBytes - 2)];

        var bytes = new byte[encoded.Length + 2];
        encoded.CopyTo(bytes, 0);
        bytes[^2] = (byte)'\r';
        bytes[^1] = (byte)'\n';

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        await _dispatcher.PublishAsync(
            new RawLineSent(_serverId, line),
            EventPriority.Normal, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Send QUIT and close the connection cleanly.
    /// </summary>
    public async Task DisconnectAsync(string reason = "Disconnecting", CancellationToken ct = default)
    {
        if (_stream is null) return;

        try { await SendLineAsync($"QUIT :{reason}", ct).ConfigureAwait(false); }
        catch { /* stream may already be closing */ }

        await CloseInternalAsync().ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------
    // Receive loop
    // ---------------------------------------------------------------------------

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var readBuffer = new byte[4096];
        var lineBuffer = new List<byte>(MaxLineBytes);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int bytesRead = await _stream!.ReadAsync(readBuffer.AsMemory(), ct)
                    .ConfigureAwait(false);

                if (bytesRead == 0)
                {
                    // Clean EOF from the server.
                    await _dispatcher.PublishAsync(
                        new ConnectionClosed(_serverId, Reason: null),
                        EventPriority.Critical).ConfigureAwait(false);
                    return;
                }

                for (int i = 0; i < bytesRead; i++)
                {
                    byte b = readBuffer[i];

                    if (b == '\n')
                    {
                        // Strip the preceding \r if present (standard IRC CRLF).
                        if (lineBuffer.Count > 0 && lineBuffer[^1] == '\r')
                            lineBuffer.RemoveAt(lineBuffer.Count - 1);

                        if (lineBuffer.Count > 0)
                        {
                            var line = _encoding.GetString(lineBuffer.ToArray());
                            lineBuffer.Clear();
                            await HandleLineAsync(line, ct).ConfigureAwait(false);
                        }
                    }
                    else if (lineBuffer.Count < MaxLineBytes)
                    {
                        lineBuffer.Add(b);
                    }
                    // else: line exceeds MaxLineBytes — byte discarded (malicious server guard)
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            await _dispatcher.PublishAsync(
                new ConnectionClosed(_serverId, ex.Message),
                EventPriority.Critical).ConfigureAwait(false);
        }
    }

    private async Task HandleLineAsync(string line, CancellationToken ct)
    {
        // PING is answered here, before the event bus, to minimise round-trip latency.
        // RawLineReceived is still published so IRCParser can update the lag measurement.
        if (TryParsePing(line, out var token))
        {
            var pong = string.IsNullOrEmpty(token) ? "PONG" : $"PONG :{token}";
            await SendLineAsync(pong, ct).ConfigureAwait(false);
        }

        await _dispatcher.PublishAsync(
            new RawLineReceived(_serverId, line),
            EventPriority.Normal, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Extract the PING token from a raw IRC line.
    /// Handles both bare form ("PING :token") and prefixed form (":server PING :token").
    /// Exposed as internal for unit testing.
    /// </summary>
    internal static bool TryParsePing(string line, out string token)
    {
        var span = line.AsSpan().TrimStart();

        // Skip optional ":prefix " at the start.
        if (span.StartsWith(":"))
        {
            int space = span.IndexOf(' ');
            if (space < 0) { token = ""; return false; }
            span = span[(space + 1)..].TrimStart();
        }

        // Must begin with PING (case-insensitive).
        if (!span.StartsWith("PING", StringComparison.OrdinalIgnoreCase))
        {
            token = "";
            return false;
        }

        span = span[4..]; // skip "PING"

        if (span.IsEmpty || span[0] != ' ')
        {
            // Bare PING with no parameters.
            token = "";
            return true;
        }

        span = span[1..].TrimStart(); // skip the space(s) after PING

        // Strip the leading colon that marks a trailing parameter.
        if (!span.IsEmpty && span[0] == ':')
            span = span[1..];

        token = span.ToString();
        return true;
    }

    // ---------------------------------------------------------------------------
    // Lifecycle helpers
    // ---------------------------------------------------------------------------

    private async Task CloseInternalAsync()
    {
        _receiveCts?.Cancel();

        if (_receiveTask is not null)
        {
            try { await _receiveTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await CloseInternalAsync().ConfigureAwait(false);
        _receiveCts?.Dispose();
        _writeLock.Dispose();
    }
}
