// SPDX-License-Identifier: GPL-3.0-or-later
// DCC file transfer I/O: DccReceiver (inbound) and DccSender (outbound).
// Session lifecycle management lives in Engine.cs. See ARCHITECTURE.md §11.

namespace DataJack.Core.Protocol.Dcc;

/// <summary>
/// Handles the data I/O for an inbound DCC RECV (we are downloading a file from a peer).
///
/// Protocol: read chunks from <paramref name="peerStream"/>, write them to disk, then
/// send a 4-byte big-endian ACK carrying the total bytes received so far (including any
/// resume offset). The ACK allows the sender to track progress and implement
/// sliding-window flow control.
/// </summary>
internal static class DccReceiver
{
    internal const int BufferSize = 32_768; // 32 KB read/write buffer

    /// <summary>
    /// Downloads a file from an already-established DCC stream.
    /// </summary>
    /// <param name="peerStream">Open stream connected to the sender.</param>
    /// <param name="outputPath">Destination file path (directory must already exist).</param>
    /// <param name="expectedSize">
    /// Total file size advertised in the DCC SEND offer. Transfer stops when this many
    /// bytes have been written in total (including <paramref name="resumeOffset"/>) or
    /// when the sender closes the connection, whichever comes first.
    /// Pass <see cref="long.MaxValue"/> when the size is unknown.
    /// </param>
    /// <param name="resumeOffset">
    /// Byte offset to resume from. When greater than zero the output file must already
    /// contain exactly this many bytes; the method opens it in append mode so those bytes
    /// are preserved. Pass 0 (the default) for a fresh transfer.
    /// </param>
    /// <param name="progress">
    /// Optional callback invoked after each buffer write with (total bytes on disk, bytes/sec).
    /// "Total bytes on disk" includes the resume offset so callers always see the real
    /// progress toward the full file size.
    /// </param>
    /// <param name="ct">Cancels the transfer and leaves the partial output file on disk.</param>
    /// <returns>Total bytes on disk after the transfer (resume offset + bytes received this session).</returns>
    internal static async Task<long> ReceiveAsync(
        Stream                                        peerStream,
        string                                        outputPath,
        long                                          expectedSize,
        long                                          resumeOffset,
        IProgress<(long bytesReceived, double rate)>? progress,
        CancellationToken                             ct)
    {
        // Open the output file: append to preserve already-received bytes when resuming,
        // or create/overwrite for a fresh transfer.
        await using Stream file = resumeOffset > 0
            ? new FileStream(outputPath, FileMode.Append, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true)
            : File.Create(outputPath);

        var buffer  = new byte[BufferSize];
        var ackBuf  = new byte[4];
        long sessionBytes = 0;          // bytes received in this session (not counting the offset)
        long remaining    = expectedSize - resumeOffset;
        var  startTime    = DateTime.UtcNow;

        while (sessionBytes < remaining)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining - sessionBytes);
            int read   = await peerStream.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
            if (read == 0)
                break; // sender closed the connection cleanly

            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            sessionBytes += read;

            // The ACK carries the cumulative total bytes the receiver holds, including the
            // offset. Clamped to uint.MaxValue for files >4 GB (wrapping is accepted
            // behaviour across all major DCC clients).
            uint ack = (uint)Math.Min(resumeOffset + sessionBytes, uint.MaxValue);
            ackBuf[0] = (byte)(ack >> 24);
            ackBuf[1] = (byte)(ack >> 16);
            ackBuf[2] = (byte)(ack >>  8);
            ackBuf[3] = (byte) ack;
            await peerStream.WriteAsync(ackBuf, ct).ConfigureAwait(false);

            double elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
            double rate    = elapsed > 0 ? sessionBytes / elapsed : 0;
            progress?.Report((resumeOffset + sessionBytes, rate));
        }

        return resumeOffset + sessionBytes;
    }
}

/// <summary>
/// Handles the data I/O for an outbound DCC SEND (we are uploading a file to a peer).
///
/// Protocol: seek to <paramref name="resumeOffset"/> in the file, then read chunks and
/// write them to the peer stream. The peer sends back 4-byte big-endian ACKs with the
/// total bytes received; we drain those ACKs in a background loop to prevent the peer's
/// TCP receive buffer from filling.
/// </summary>
internal static class DccSender
{
    internal const int BufferSize = 32_768; // 32 KB read/write buffer

    /// <summary>
    /// Uploads a file over an already-established DCC stream.
    /// </summary>
    /// <param name="peerStream">Open stream connected to the receiver.</param>
    /// <param name="filePath">Path of the file to send (must exist).</param>
    /// <param name="resumeOffset">
    /// Byte offset to start sending from. Pass 0 (the default) to send the whole file.
    /// When greater than zero the file is seeked to this position before reading starts.
    /// </param>
    /// <param name="progress">
    /// Optional callback invoked after each chunk with (total bytes sent including offset, bytes/sec).
    /// </param>
    /// <param name="ct">Cancels the transfer.</param>
    /// <returns>Total bytes the receiver should have on disk (resume offset + bytes sent this session).</returns>
    internal static async Task<long> SendAsync(
        Stream                                     peerStream,
        string                                     filePath,
        long                                       resumeOffset,
        IProgress<(long bytesSent, double rate)>?  progress,
        CancellationToken                          ct)
    {
        await using var file = File.OpenRead(filePath);

        if (resumeOffset > 0)
            file.Seek(resumeOffset, SeekOrigin.Begin);

        var  sendBuf      = new byte[BufferSize];
        long sessionSent  = 0;      // bytes sent in this session (not counting the offset)
        var  startTime    = DateTime.UtcNow;

        // Drain incoming ACKs on a background loop to prevent the receiver's TCP receive
        // buffer from filling, which would block our sends on platforms with small buffers.
        using var ackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ackDrainer = DrainAcksAsync(peerStream, ackCts.Token);

        try
        {
            while (true)
            {
                int read = await file.ReadAsync(sendBuf, ct).ConfigureAwait(false);
                if (read == 0)
                    break;

                await peerStream.WriteAsync(sendBuf.AsMemory(0, read), ct).ConfigureAwait(false);
                sessionSent += read;

                double elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                double rate    = elapsed > 0 ? sessionSent / elapsed : 0;
                progress?.Report((resumeOffset + sessionSent, rate));
            }
        }
        finally
        {
            ackCts.Cancel();
            try { await ackDrainer.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        return resumeOffset + sessionSent;
    }

    // Continuously reads and discards 4-byte ACK packets from the receiver until cancelled.
    private static async Task DrainAcksAsync(Stream stream, CancellationToken ct)
    {
        var ackBuf = new byte[4];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                int n = await stream.ReadAsync(ackBuf, ct).ConfigureAwait(false);
                if (n == 0)
                    break; // peer closed
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // Peer closed the connection; stop draining.
                break;
            }
        }
    }
}
