// SPDX-License-Identifier: GPL-3.0-or-later
// DCC file transfer I/O: DccReceiver (inbound) and DccSender (outbound).
// Session lifecycle management lives in Engine.cs. See ARCHITECTURE.md §11.

namespace DataJack.Core.Protocol.Dcc;

/// <summary>
/// Handles the data I/O for an inbound DCC RECV (we are downloading a file from a peer).
///
/// Protocol: read chunks from <paramref name="peerStream"/>, write them to disk, then
/// send a 4-byte big-endian ACK carrying the total bytes received so far. The ACK allows
/// the sender to track progress and implement sliding-window flow control.
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
    /// Number of bytes advertised in the DCC SEND offer. Transfer stops when this many
    /// bytes are received or when the sender closes the connection, whichever comes first.
    /// Pass <see cref="long.MaxValue"/> when the size is unknown.
    /// </param>
    /// <param name="progress">
    /// Optional callback invoked after each buffer write with (total bytes received, bytes/sec).
    /// The callback is invoked synchronously on the calling thread; marshal to the UI thread
    /// in the caller if required.
    /// </param>
    /// <param name="ct">Cancels the transfer and leaves the partial output file on disk.</param>
    /// <returns>Total bytes written to <paramref name="outputPath"/>.</returns>
    internal static async Task<long> ReceiveAsync(
        Stream                                       peerStream,
        string                                       outputPath,
        long                                         expectedSize,
        IProgress<(long bytesReceived, double rate)>? progress,
        CancellationToken                            ct)
    {
        await using var file = File.Create(outputPath);
        var buffer = new byte[BufferSize];
        var ackBuf = new byte[4];
        long totalReceived = 0;
        var startTime = DateTime.UtcNow;

        while (totalReceived < expectedSize)
        {
            int toRead = (int)Math.Min(buffer.Length, expectedSize - totalReceived);
            int read = await peerStream.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
            if (read == 0)
                break; // sender closed the connection cleanly

            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            totalReceived += read;

            // Send 4-byte big-endian ACK with the running total bytes received.
            // Clamped to uint.MaxValue for very large files (>4 GB); the 32-bit ACK
            // wraps in the DCC protocol once the counter overflows, which is accepted
            // behaviour across all major DCC clients.
            uint ack = (uint)Math.Min(totalReceived, uint.MaxValue);
            ackBuf[0] = (byte)(ack >> 24);
            ackBuf[1] = (byte)(ack >> 16);
            ackBuf[2] = (byte)(ack >>  8);
            ackBuf[3] = (byte) ack;
            await peerStream.WriteAsync(ackBuf, ct).ConfigureAwait(false);

            double elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
            double rate    = elapsed > 0 ? totalReceived / elapsed : 0;
            progress?.Report((totalReceived, rate));
        }

        return totalReceived;
    }
}

/// <summary>
/// Handles the data I/O for an outbound DCC SEND (we are uploading a file to a peer).
///
/// Protocol: read chunks from the file and write them to the peer stream. The peer
/// sends back 4-byte big-endian ACKs with the total bytes received; we drain those
/// ACKs in a background loop to prevent the peer's TCP receive buffer from filling.
/// </summary>
internal static class DccSender
{
    internal const int BufferSize = 32_768; // 32 KB read/write buffer

    /// <summary>
    /// Uploads a file over an already-established DCC stream.
    /// </summary>
    /// <param name="peerStream">Open stream connected to the receiver.</param>
    /// <param name="filePath">Path of the file to send (must exist).</param>
    /// <param name="progress">
    /// Optional callback invoked after each chunk with (total bytes sent, bytes/sec).
    /// </param>
    /// <param name="ct">Cancels the transfer.</param>
    /// <returns>Total bytes sent from <paramref name="filePath"/>.</returns>
    internal static async Task<long> SendAsync(
        Stream                                      peerStream,
        string                                      filePath,
        IProgress<(long bytesSent, double rate)>?  progress,
        CancellationToken                           ct)
    {
        await using var file = File.OpenRead(filePath);
        var sendBuf = new byte[BufferSize];
        long totalSent = 0;
        var startTime = DateTime.UtcNow;

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
                totalSent += read;

                double elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                double rate    = elapsed > 0 ? totalSent / elapsed : 0;
                progress?.Report((totalSent, rate));
            }
        }
        finally
        {
            ackCts.Cancel();
            try { await ackDrainer.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        return totalSent;
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
