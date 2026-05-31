// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace DataJack.Net;

/// <summary>
/// Address-family selection for outbound connections. See ARCHITECTURE.md §10.3.
/// </summary>
public enum AddressFamilyPreference
{
    /// <summary>Try IPv6 first; fall back to IPv4 after 250 ms. Default.</summary>
    PreferIpv6,

    /// <summary>Try IPv4 first; fall back to IPv6 after 250 ms.</summary>
    PreferIpv4,

    /// <summary>Only attempt IPv6. Fail if no AAAA records exist.</summary>
    Ipv6Only,

    /// <summary>Only attempt IPv4. Fail if no A records exist.</summary>
    Ipv4Only,
}

/// <summary>
/// Parameters for a single outbound connection. Passed to <see cref="INetworkProvider.ConnectAsync"/>.
/// </summary>
public sealed record NetworkEndpoint(
    string Host,
    int Port,
    bool UseTls,
    AddressFamilyPreference AddressFamily = AddressFamilyPreference.PreferIpv6,
    X509Certificate2? ClientCertificate = null,
    /// <summary>
    /// SHA-256 fingerprint (hex, case-insensitive) of an accepted self-signed certificate.
    /// When set, fingerprint matching overrides chain validation entirely.
    /// Null means standard chain validation is used.
    /// </summary>
    string? TlsFingerprintPin = null);

/// <summary>
/// Thrown by <see cref="TlsTransport"/> when TLS certificate validation fails.
/// <see cref="IRCConnection"/> converts this to a <c>TLSCertificateError</c> event
/// and decides whether to prompt the user or abort the connection.
/// </summary>
public sealed class TlsCertificateException : Exception
{
    /// <summary>The certificate that failed validation, if one was presented.</summary>
    public X509Certificate2? Certificate { get; }

    /// <summary>The SSL policy errors that caused the failure.</summary>
    public SslPolicyErrors PolicyErrors { get; }

    /// <summary>The server hostname being connected to.</summary>
    public string Host { get; }

    public TlsCertificateException(
        string host,
        string message,
        X509Certificate2? certificate,
        SslPolicyErrors policyErrors)
        : base(message)
    {
        Host = host;
        Certificate = certificate;
        PolicyErrors = policyErrors;
    }
}

/// <summary>
/// Transport abstraction used by <see cref="IRCConnection"/>. Returns a connected
/// <see cref="Stream"/> ready for bidirectional byte I/O. All IRC logic operates on
/// this stream; it never references sockets, TLS, or proxies directly.
/// </summary>
public interface INetworkProvider
{
    /// <summary>
    /// Open a connection as described by <paramref name="endpoint"/> and return the
    /// connected stream. The caller owns the stream and must dispose it to close the connection.
    /// </summary>
    /// <exception cref="System.Net.Sockets.SocketException">TCP-level failure.</exception>
    /// <exception cref="TlsCertificateException">Certificate validation failure (TLS only).</exception>
    /// <exception cref="System.Security.Authentication.AuthenticationException">TLS handshake failure.</exception>
    /// <exception cref="OperationCanceledException">Cancelled via <paramref name="ct"/>.</exception>
    Task<Stream> ConnectAsync(NetworkEndpoint endpoint, CancellationToken ct = default);
}
