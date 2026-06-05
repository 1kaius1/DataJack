// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Net;
using Xunit;

namespace DataJack.Core.Tests;

// ---------------------------------------------------------------------------
// Fake bidirectional stream for protocol testing
// ---------------------------------------------------------------------------

/// <summary>
/// Pre-loads server-side response bytes for reading; captures all client writes.
/// Allows SOCKS5 handshake methods to be exercised without a real proxy.
/// </summary>
sealed class FakeProxyStream : Stream
{
    private readonly MemoryStream _toClient;   // proxy → client data (pre-loaded)
    private readonly MemoryStream _fromClient; // client → proxy data (captured)

    internal FakeProxyStream(byte[] serverResponses)
    {
        _toClient   = new MemoryStream(serverResponses);
        _fromClient = new MemoryStream();
    }

    /// <summary>All bytes the client wrote to the stream.</summary>
    internal byte[] ClientSent => _fromClient.ToArray();

    public override bool CanRead  => true;
    public override bool CanSeek  => false;
    public override bool CanWrite => true;

    // Reads come from the pre-loaded server response buffer.
    public override int Read(byte[] buffer, int offset, int count)
        => _toClient.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        => _toClient.ReadAsync(buffer, ct);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => _toClient.ReadAsync(buffer, offset, count, ct);

    // Writes are captured in the client-sent buffer.
    public override void Write(byte[] buffer, int offset, int count)
        => _fromClient.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        => _fromClient.WriteAsync(buffer, ct);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => _fromClient.WriteAsync(buffer, offset, count, ct);

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

    // Unused abstract members.
    public override long Length   => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value)                => throw new NotSupportedException();
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

public sealed class Socks5TransportTests
{
    // ---------------------------------------------------------------------------
    // Helpers — pre-built proxy response byte sequences
    // ---------------------------------------------------------------------------

    // Greeting: SOCKS5, no-auth selected.
    private static byte[] GreetingNoAuth() => new byte[] { 0x05, 0x00 };

    // Greeting: SOCKS5, username/password auth selected.
    private static byte[] GreetingPasswordAuth() => new byte[] { 0x05, 0x02 };

    // Greeting: SOCKS5, no acceptable method (0xFF).
    private static byte[] GreetingRejected() => new byte[] { 0x05, 0xFF };

    // Auth sub-negotiation: success.
    private static byte[] AuthSuccess() => new byte[] { 0x01, 0x00 };

    // Auth sub-negotiation: failure.
    private static byte[] AuthFailure() => new byte[] { 0x01, 0x01 };

    // CONNECT response: success, IPv4 bound address 0.0.0.0, port 0.
    private static byte[] ConnectSuccessIpv4() =>
        new byte[] { 0x05, 0x00, 0x00, 0x01,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00 };

    // CONNECT response: host unreachable (0x04), IPv4 bound address.
    private static byte[] ConnectHostUnreachable() =>
        new byte[] { 0x05, 0x04, 0x00, 0x01,  0x00, 0x00, 0x00, 0x00,  0x00, 0x00 };

    // CONNECT response: success, IPv6 bound address ::1, port 0.
    private static byte[] ConnectSuccessIpv6() =>
        new byte[]
        {
            0x05, 0x00, 0x00, 0x04,
            // IPv6 ::1
            0x00,0x00,0x00,0x00, 0x00,0x00,0x00,0x00,
            0x00,0x00,0x00,0x00, 0x00,0x00,0x00,0x01,
            0x00, 0x00,   // port 0
        };

    // Combine multiple byte arrays into one.
    private static byte[] Concat(params byte[][] parts) =>
        parts.SelectMany(p => p).ToArray();

    // ---------------------------------------------------------------------------
    // Phase 1 — NegotiateMethodAsync
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Negotiate_NoAuth_SendsCorrectGreeting()
    {
        var stream = new FakeProxyStream(GreetingNoAuth());
        await Socks5Transport.NegotiateMethodAsync(stream, false, CancellationToken.None);

        byte[] sent = stream.ClientSent;
        Assert.Equal(3, sent.Length);
        Assert.Equal(0x05, sent[0]);  // SOCKS5
        Assert.Equal(0x01, sent[1]);  // 1 method
        Assert.Equal(0x00, sent[2]);  // NO AUTH
    }

    [Fact]
    public async Task Negotiate_WithCredentials_OffersPasswordMethod()
    {
        var stream = new FakeProxyStream(GreetingPasswordAuth());
        await Socks5Transport.NegotiateMethodAsync(stream, true, CancellationToken.None);

        byte[] sent = stream.ClientSent;
        Assert.Equal(4, sent.Length);
        Assert.Equal(0x05, sent[0]);  // SOCKS5
        Assert.Equal(0x02, sent[1]);  // 2 methods
        Assert.Equal(0x00, sent[2]);  // NO AUTH
        Assert.Equal(0x02, sent[3]);  // USERNAME/PASSWORD
    }

    [Fact]
    public async Task Negotiate_ServerSelectsNoAuth_Returns0x00()
    {
        var stream = new FakeProxyStream(GreetingNoAuth());
        byte method = await Socks5Transport.NegotiateMethodAsync(stream, false, CancellationToken.None);
        Assert.Equal(0x00, method);
    }

    [Fact]
    public async Task Negotiate_ServerSelectsPassword_Returns0x02()
    {
        var stream = new FakeProxyStream(GreetingPasswordAuth());
        byte method = await Socks5Transport.NegotiateMethodAsync(stream, true, CancellationToken.None);
        Assert.Equal(0x02, method);
    }

    [Fact]
    public async Task Negotiate_WrongSocksVersion_ThrowsSocks5Exception()
    {
        // Proxy responds with SOCKS4 version byte.
        var stream = new FakeProxyStream(new byte[] { 0x04, 0x00 });
        await Assert.ThrowsAsync<Socks5Exception>(
            () => Socks5Transport.NegotiateMethodAsync(stream, false, CancellationToken.None));
    }

    // ---------------------------------------------------------------------------
    // Phase 2 — AuthenticateAsync
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Authenticate_SendsUsernameAndPassword()
    {
        var stream = new FakeProxyStream(AuthSuccess());
        await Socks5Transport.AuthenticateAsync(stream, "alice", "s3cr3t", CancellationToken.None);

        byte[] sent = stream.ClientSent;
        Assert.Equal(0x01, sent[0]);               // subneg version
        Assert.Equal(5, sent[1]);                  // "alice" length
        Assert.Equal("alice", System.Text.Encoding.UTF8.GetString(sent, 2, 5));
        Assert.Equal(6, sent[7]);                  // "s3cr3t" length
        Assert.Equal("s3cr3t", System.Text.Encoding.UTF8.GetString(sent, 8, 6));
    }

    [Fact]
    public async Task Authenticate_ServerAccepts_Succeeds()
    {
        var stream = new FakeProxyStream(AuthSuccess());
        // Should not throw.
        await Socks5Transport.AuthenticateAsync(stream, "user", "pass", CancellationToken.None);
    }

    [Fact]
    public async Task Authenticate_ServerRejects_ThrowsSocks5Exception()
    {
        var stream = new FakeProxyStream(AuthFailure());
        await Assert.ThrowsAsync<Socks5Exception>(
            () => Socks5Transport.AuthenticateAsync(stream, "user", "wrong", CancellationToken.None));
    }

    // ---------------------------------------------------------------------------
    // Phase 3 — SendConnectAsync
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SendConnect_UsesAtyp03_PreventingLocalDns()
    {
        var stream = new FakeProxyStream(Array.Empty<byte>());
        await Socks5Transport.SendConnectAsync(stream, "irc.libera.chat", 6697, CancellationToken.None);

        byte[] sent = stream.ClientSent;
        Assert.Equal(0x05, sent[0]);  // SOCKS5
        Assert.Equal(0x01, sent[1]);  // CONNECT
        Assert.Equal(0x00, sent[2]);  // reserved
        Assert.Equal(0x03, sent[3]);  // ATYP = domain name (remote DNS)
    }

    [Fact]
    public async Task SendConnect_HostBytesCorrect()
    {
        var stream = new FakeProxyStream(Array.Empty<byte>());
        await Socks5Transport.SendConnectAsync(stream, "irc.libera.chat", 6697, CancellationToken.None);

        byte[] sent  = stream.ClientSent;
        int    hlen  = sent[4];  // hostname length byte
        string host  = System.Text.Encoding.UTF8.GetString(sent, 5, hlen);
        Assert.Equal("irc.libera.chat", host);
    }

    [Fact]
    public async Task SendConnect_PortBytesCorrect()
    {
        var stream = new FakeProxyStream(Array.Empty<byte>());
        await Socks5Transport.SendConnectAsync(stream, "irc.libera.chat", 6697, CancellationToken.None);

        byte[] sent = stream.ClientSent;
        int    hlen = sent[4];
        // Port follows: 2 bytes big-endian at [5+hlen] and [6+hlen]
        int port = (sent[5 + hlen] << 8) | sent[6 + hlen];
        Assert.Equal(6697, port);
    }

    // ---------------------------------------------------------------------------
    // Phase 4 — ReadConnectResponseAsync
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ReadConnectResponse_SuccessIpv4_Returns()
    {
        var stream = new FakeProxyStream(ConnectSuccessIpv4());
        // Should not throw.
        await Socks5Transport.ReadConnectResponseAsync(stream, CancellationToken.None);
    }

    [Fact]
    public async Task ReadConnectResponse_HostUnreachable_ThrowsWithCode()
    {
        var stream = new FakeProxyStream(ConnectHostUnreachable());
        var ex = await Assert.ThrowsAsync<Socks5Exception>(
            () => Socks5Transport.ReadConnectResponseAsync(stream, CancellationToken.None));
        Assert.Equal((byte)0x04, ex.ReplyCode);
    }

    [Fact]
    public async Task ReadConnectResponse_Ipv6BoundAddress_ParsedCorrectly()
    {
        // The response bound address is IPv6 (16 bytes); must be consumed without error.
        var stream = new FakeProxyStream(ConnectSuccessIpv6());
        await Socks5Transport.ReadConnectResponseAsync(stream, CancellationToken.None);
    }

    // ---------------------------------------------------------------------------
    // Full handshake — no-auth success path
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task FullHandshake_NoAuth_SuccessPath()
    {
        // Server bytes: greeting (no-auth) + CONNECT success
        var stream = new FakeProxyStream(Concat(GreetingNoAuth(), ConnectSuccessIpv4()));

        await Socks5Transport.NegotiateMethodAsync(stream, false, CancellationToken.None);
        await Socks5Transport.SendConnectAsync(stream, "irc.example.net", 6667, CancellationToken.None);
        await Socks5Transport.ReadConnectResponseAsync(stream, CancellationToken.None);

        // Verify client sent the right number of bytes for a no-auth full handshake:
        // greeting [3] + connect request [4+1+host_len+2]
        int hostLen = System.Text.Encoding.UTF8.GetByteCount("irc.example.net");
        int expected = 3 + 4 + 1 + hostLen + 2;
        Assert.Equal(expected, stream.ClientSent.Length);
    }

    [Fact]
    public async Task FullHandshake_WithAuth_SuccessPath()
    {
        var stream = new FakeProxyStream(Concat(
            GreetingPasswordAuth(), AuthSuccess(), ConnectSuccessIpv4()));

        byte method = await Socks5Transport.NegotiateMethodAsync(stream, true, CancellationToken.None);
        Assert.Equal(0x02, method);

        await Socks5Transport.AuthenticateAsync(stream, "bob", "hunter2", CancellationToken.None);
        await Socks5Transport.SendConnectAsync(stream, "irc.freenode.net", 6697, CancellationToken.None);
        await Socks5Transport.ReadConnectResponseAsync(stream, CancellationToken.None);

        // Spot-check: ATYP byte is still 0x03 (domain name) in the connect request.
        byte[] sent = stream.ClientSent;
        // Greeting: 4 bytes. Auth: 1+1+3+1+7 = 13. Connect: starts at offset 17.
        int greetingLen  = 4;
        int authLen      = 1 + 1 + 3 + 1 + 7;  // subneg + ulen + "bob" + plen + "hunter2"
        int connectStart = greetingLen + authLen;
        Assert.Equal(0x03, sent[connectStart + 3]);  // ATYP = domain name
    }
}
