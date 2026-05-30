// SPDX-License-Identifier: GPL-3.0-or-later
// Shared test infrastructure used by IRCConnection, IRCParser, and other integration tests.

using System.IO.Pipelines;
using System.Text;
using DataJack.Net;

namespace DataJack.Core.Tests;

/// <summary>
/// A bidirectional in-process stream backed by two <see cref="Pipe"/> instances.
/// The test writes "server data" via <see cref="SendServerDataAsync"/> and reads
/// "client data" (what IRCConnection sent) via <see cref="ReadClientLineAsync"/>.
/// </summary>
internal sealed class DuplexPipeStream : Stream
{
    private readonly Pipe _inbound  = new(); // server → client (test writes, connection reads)
    private readonly Pipe _outbound = new(); // client → server (connection writes, test reads)

    // StreamReader/Stream wrappers created lazily for test helpers only.
    private Stream?       _inboundWriterStream;
    private StreamReader? _outboundStreamReader;

    // -----------------------------------------------------------------------
    // Test-facing helpers
    // -----------------------------------------------------------------------

    /// <summary>Write a string as if the IRC server sent it to the client.</summary>
    public async Task SendServerDataAsync(string data)
    {
        _inboundWriterStream ??= _inbound.Writer.AsStream();
        var bytes = Encoding.UTF8.GetBytes(data);
        await _inboundWriterStream.WriteAsync(bytes).ConfigureAwait(false);
        await _inboundWriterStream.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>Signal EOF from the server (causes IRCConnection to see a clean disconnect).</summary>
    public void CloseServer() => _inbound.Writer.Complete();

    /// <summary>
    /// Read one line from what the client sent. Strips the trailing CRLF.
    /// Returns null on EOF.
    /// </summary>
    public async Task<string?> ReadClientLineAsync(CancellationToken ct = default)
    {
        _outboundStreamReader ??= new StreamReader(_outbound.Reader.AsStream(), Encoding.UTF8);
        return await _outboundStreamReader.ReadLineAsync(ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // Stream overrides — IRCConnection calls these
    // -----------------------------------------------------------------------

    public override bool CanRead  => true;
    public override bool CanWrite => true;
    public override bool CanSeek  => false;
    public override long Length   => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value)                => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Use ReadAsync.");
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Use WriteAsync.");

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var result = await _inbound.Reader.ReadAsync(ct).ConfigureAwait(false);

        if (result.IsCompleted && result.Buffer.IsEmpty)
            return 0; // EOF

        var sequence = result.Buffer;
        int toCopy = (int)Math.Min(sequence.Length, buffer.Length);

        // Copy each segment of the sequence into the destination buffer.
        int offset = 0;
        foreach (ReadOnlyMemory<byte> segment in sequence.Slice(0, toCopy))
        {
            segment.Span.CopyTo(buffer.Span[offset..]);
            offset += segment.Length;
        }

        _inbound.Reader.AdvanceTo(sequence.GetPosition(toCopy));
        return toCopy;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await _outbound.Writer.WriteAsync(buffer, ct).ConfigureAwait(false);
        await _outbound.Writer.FlushAsync(ct).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inbound.Writer.Complete();
            _outbound.Writer.Complete();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Returns a pre-built stream instead of opening a real socket.
/// </summary>
internal sealed class FakeNetworkProvider(Stream stream) : INetworkProvider
{
    public Task<Stream> ConnectAsync(NetworkEndpoint endpoint, CancellationToken ct = default) =>
        Task.FromResult(stream);
}
