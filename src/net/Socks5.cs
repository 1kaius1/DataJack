// SPDX-License-Identifier: GPL-3.0-or-later
// SOCKS5 proxy transport. See ARCHITECTURE.md §10.4.
//
// Handshake phases:
//   1. Greeting: client offers supported auth methods; proxy selects one.
//   2. Auth: if method 0x02 (username/password), credentials are exchanged.
//   3. CONNECT: client sends the target host+port; proxy connects on its behalf.
//   4. Response: proxy reports success or failure and the bound address.
//
// Remote DNS: the CONNECT request uses ATYP=0x03 (domain name). The proxy
// resolves the hostname — no local DNS query is ever made for the target,
// preventing the DNS leak that would occur with ATYP=0x01/0x04.
//
// If the endpoint requires TLS, TlsTransport.WrapAsync layers it on top of
// the already-established proxied TCP stream.

using System.Net.Sockets;
using System.Text;
using DataJack.Core.Storage.Config;

namespace DataJack.Net;

/// <summary>
/// Thrown when the SOCKS5 proxy returns an error or speaks an unexpected protocol.
/// </summary>
public sealed class Socks5Exception : IOException
{
    /// <summary>The SOCKS5 reply code from the proxy, or <c>null</c> for protocol errors.</summary>
    public byte? ReplyCode { get; }

    /// <inheritdoc cref="Socks5Exception(string, byte?)"/>
    public Socks5Exception(string message) : base(message) { }

    /// <param name="message">Human-readable description of the failure.</param>
    /// <param name="replyCode">The reply code byte from the SOCKS5 CONNECT response.</param>
    public Socks5Exception(string message, byte? replyCode) : base(message)
    {
        ReplyCode = replyCode;
    }
}

/// <summary>
/// Connects to a target host through a SOCKS5 proxy. DNS resolution for the target
/// host is always performed by the proxy (ATYP=0x03), preventing local DNS leaks.
/// Optionally layers TLS on top of the proxied TCP connection.
/// </summary>
public sealed class Socks5Transport : INetworkProvider
{
    private readonly string  _proxyHost;
    private readonly int     _proxyPort;
    private readonly string? _username;
    private readonly string? _password;

    /// <param name="proxyHost">SOCKS5 proxy hostname or IP address.</param>
    /// <param name="proxyPort">SOCKS5 proxy port (typically 1080).</param>
    /// <param name="username">Optional username for method 0x02 auth.</param>
    /// <param name="password">Optional password for method 0x02 auth.</param>
    public Socks5Transport(
        string  proxyHost,
        int     proxyPort,
        string? username = null,
        string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proxyHost);
        _proxyHost = proxyHost;
        _proxyPort = proxyPort;
        _username  = username;
        _password  = password;
    }

    /// <summary>Convenience constructor from persisted config.</summary>
    public Socks5Transport(ProxySettings settings)
        : this(settings.Host, settings.Port, settings.Username, settings.Password) { }

    // ---------------------------------------------------------------------------
    // INetworkProvider
    // ---------------------------------------------------------------------------

    /// <inheritdoc/>
    /// <exception cref="Socks5Exception">Proxy handshake failure.</exception>
    /// <exception cref="System.Net.Sockets.SocketException">TCP-level failure (proxy unreachable).</exception>
    /// <exception cref="TlsCertificateException">TLS certificate validation failure (TLS endpoints only).</exception>
    public async Task<Stream> ConnectAsync(NetworkEndpoint endpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        // Connect to the proxy using the endpoint's address-family preference (not the target's).
        var socket = await HappyEyeballs.ConnectAsync(
            _proxyHost, _proxyPort, endpoint.AddressFamily, ct).ConfigureAwait(false);

        Stream stream = new NetworkStream(socket, ownsSocket: true);
        try
        {
            bool hasCredentials = _username is not null;

            // Phase 1: greeting — agree on an auth method.
            byte method = await NegotiateMethodAsync(stream, hasCredentials, ct).ConfigureAwait(false);
            if (method == 0xFF)
                throw new Socks5Exception("Proxy rejected all offered authentication methods.");
            if (method == 0x02)
                await AuthenticateAsync(stream, _username!, _password ?? string.Empty, ct).ConfigureAwait(false);
            else if (method != 0x00)
                throw new Socks5Exception($"Proxy selected unsupported auth method 0x{method:X2}.");

            // Phase 3: CONNECT using domain-name ATYP (remote DNS, no local leak).
            await SendConnectAsync(stream, endpoint.Host, endpoint.Port, ct).ConfigureAwait(false);

            // Phase 4: read the proxy's CONNECT response.
            await ReadConnectResponseAsync(stream, ct).ConfigureAwait(false);

            // Optional: wrap the proxied stream in TLS.
            if (endpoint.UseTls)
                stream = await TlsTransport.WrapAsync(stream, endpoint, ct).ConfigureAwait(false);

            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // ---------------------------------------------------------------------------
    // Phase 1 — auth-method negotiation (internal for testing)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Send the SOCKS5 greeting and return the method selected by the proxy.
    /// Offers NO AUTH (0x00) always; also offers USERNAME/PASSWORD (0x02) when
    /// <paramref name="offerPasswordAuth"/> is true.
    /// </summary>
    /// <returns>The selected method byte (0x00, 0x02, or 0xFF = no acceptable method).</returns>
    internal static async Task<byte> NegotiateMethodAsync(
        Stream stream, bool offerPasswordAuth, CancellationToken ct)
    {
        int numMethods = offerPasswordAuth ? 2 : 1;
        var greeting   = new byte[2 + numMethods];
        greeting[0] = 0x05;  // SOCKS5
        greeting[1] = (byte)numMethods;
        greeting[2] = 0x00;  // NO AUTH
        if (offerPasswordAuth) greeting[3] = 0x02;  // USERNAME/PASSWORD

        await stream.WriteAsync(greeting, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var response = await ReadExactlyAsync(stream, 2, ct).ConfigureAwait(false);
        if (response[0] != 0x05)
            throw new Socks5Exception(
                $"Proxy returned unexpected SOCKS version 0x{response[0]:X2} in greeting.");

        return response[1];  // Selected method
    }

    // ---------------------------------------------------------------------------
    // Phase 2 — username/password authentication (internal for testing)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Perform RFC 1929 username/password authentication. Throws on failure.
    /// </summary>
    internal static async Task AuthenticateAsync(
        Stream stream, string username, string password, CancellationToken ct)
    {
        byte[] userBytes = Encoding.UTF8.GetBytes(username);
        byte[] passBytes = Encoding.UTF8.GetBytes(password);

        if (userBytes.Length > 255)
            throw new ArgumentException("Username must be 255 UTF-8 bytes or fewer.", nameof(username));
        if (passBytes.Length > 255)
            throw new ArgumentException("Password must be 255 UTF-8 bytes or fewer.", nameof(password));

        // [0x01][ulen][username][plen][password]
        var request = new byte[3 + userBytes.Length + passBytes.Length];
        int i = 0;
        request[i++] = 0x01;  // Subnegotiation version
        request[i++] = (byte)userBytes.Length;
        userBytes.CopyTo(request, i); i += userBytes.Length;
        request[i++] = (byte)passBytes.Length;
        passBytes.CopyTo(request, i);

        await stream.WriteAsync(request, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var response = await ReadExactlyAsync(stream, 2, ct).ConfigureAwait(false);
        if (response[0] != 0x01)
            throw new Socks5Exception(
                $"Unexpected auth subnegotiation version 0x{response[0]:X2}.");
        if (response[1] != 0x00)
            throw new Socks5Exception(
                $"Proxy rejected credentials (status 0x{response[1]:X2}).", response[1]);
    }

    // ---------------------------------------------------------------------------
    // Phase 3 — CONNECT request (internal for testing)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Send a SOCKS5 CONNECT request for <paramref name="host"/>:<paramref name="port"/>.
    /// Uses ATYP=0x03 (domain name) so the proxy resolves DNS — no local lookup is made.
    /// </summary>
    internal static async Task SendConnectAsync(
        Stream stream, string host, int port, CancellationToken ct)
    {
        byte[] hostBytes = Encoding.UTF8.GetBytes(host);
        if (hostBytes.Length > 255)
            throw new ArgumentException("Host name must be 255 UTF-8 bytes or fewer.", nameof(host));

        // [ver=0x05][cmd=0x01][rsv=0x00][atyp=0x03][hlen][host][port_hi][port_lo]
        var request = new byte[4 + 1 + hostBytes.Length + 2];
        int i = 0;
        request[i++] = 0x05;  // SOCKS5
        request[i++] = 0x01;  // CONNECT
        request[i++] = 0x00;  // Reserved
        request[i++] = 0x03;  // ATYP: domain name (remote DNS — no local lookup)
        request[i++] = (byte)hostBytes.Length;
        hostBytes.CopyTo(request, i); i += hostBytes.Length;
        request[i++] = (byte)(port >> 8);   // Port high byte
        request[i  ] = (byte)(port & 0xFF); // Port low byte

        await stream.WriteAsync(request, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------
    // Phase 4 — CONNECT response (internal for testing)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Read and validate the SOCKS5 CONNECT response. Discards the bound address.
    /// Throws <see cref="Socks5Exception"/> if the proxy reports failure.
    /// </summary>
    internal static async Task ReadConnectResponseAsync(Stream stream, CancellationToken ct)
    {
        // [ver][rep][rsv][atyp]
        var header = await ReadExactlyAsync(stream, 4, ct).ConfigureAwait(false);

        if (header[0] != 0x05)
            throw new Socks5Exception(
                $"Proxy returned unexpected SOCKS version 0x{header[0]:X2} in CONNECT response.");

        byte rep  = header[1];
        byte atyp = header[3];

        if (rep != 0x00)
            throw new Socks5Exception(
                $"Proxy CONNECT failed: {ReplyCodeText(rep)} (0x{rep:X2}).", rep);

        // Discard the bound address — length depends on the address type.
        int addrLen;
        if (atyp == 0x01)
        {
            addrLen = 4;   // IPv4
        }
        else if (atyp == 0x04)
        {
            addrLen = 16;  // IPv6
        }
        else if (atyp == 0x03)
        {
            // Domain name: 1-byte length prefix followed by that many bytes.
            var lenBuf = await ReadExactlyAsync(stream, 1, ct).ConfigureAwait(false);
            addrLen = lenBuf[0];
        }
        else
        {
            throw new Socks5Exception($"Proxy returned unknown bound address type 0x{atyp:X2}.");
        }

        // Discard address bytes + 2-byte port.
        await ReadExactlyAsync(stream, addrLen + 2, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------
    // Shared helpers
    // ---------------------------------------------------------------------------

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken ct)
    {
        byte[] buffer = new byte[count];
        // ReadExactlyAsync (Stream extension, .NET 7+) throws EndOfStreamException
        // when the stream ends before count bytes are available.
        await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        return buffer;
    }

    private static string ReplyCodeText(byte code) => code switch
    {
        0x00 => "succeeded",
        0x01 => "general SOCKS server failure",
        0x02 => "connection not allowed by ruleset",
        0x03 => "network unreachable",
        0x04 => "host unreachable",
        0x05 => "connection refused",
        0x06 => "TTL expired",
        0x07 => "command not supported",
        0x08 => "address type not supported",
        _    => $"unknown error 0x{code:X2}",
    };
}
