// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DataJack.Net;

/// <summary>
/// TLS transport. Establishes a TCP connection with happy-eyeballs, then performs a
/// TLS 1.2/1.3 handshake with SNI. See ARCHITECTURE.md §10.2 for the certificate policy.
/// </summary>
public sealed class TlsTransport : INetworkProvider
{
    /// <inheritdoc/>
    public async Task<Stream> ConnectAsync(NetworkEndpoint endpoint, CancellationToken ct = default)
    {
        var socket = await HappyEyeballs.ConnectAsync(
            endpoint.Host,
            endpoint.Port,
            endpoint.AddressFamily,
            ct).ConfigureAwait(false);

        var networkStream = new NetworkStream(socket, ownsSocket: true);

        // Capture validation failure details in the callback so we can surface them
        // in TlsCertificateException after AuthenticateAsClientAsync throws.
        X509Certificate2? failedCert = null;
        SslPolicyErrors failedErrors = SslPolicyErrors.None;

        var sslOptions = new SslClientAuthenticationOptions
        {
            TargetHost = endpoint.Host,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ClientCertificates = endpoint.ClientCertificate is not null
                ? new X509CertificateCollection { endpoint.ClientCertificate }
                : null,
            RemoteCertificateValidationCallback = (_, cert, _, errors) =>
            {
                bool valid = ValidateCertificate(cert, endpoint.TlsFingerprintPin, errors);
                if (!valid)
                {
                    failedErrors = errors;
                    failedCert = cert is not null ? new X509Certificate2(cert) : null;
                }
                return valid;
            },
        };

        var sslStream = new SslStream(networkStream, leaveInnerStreamOpen: false);
        try
        {
            await sslStream.AuthenticateAsClientAsync(sslOptions, ct).ConfigureAwait(false);
            return sslStream;
        }
        catch (AuthenticationException ex)
        {
            await sslStream.DisposeAsync().ConfigureAwait(false);
            throw new TlsCertificateException(
                endpoint.Host,
                $"TLS authentication failed for {endpoint.Host}: {ex.Message}",
                failedCert,
                failedErrors);
        }
        catch
        {
            await sslStream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Returns true if the certificate should be accepted.
    ///
    /// When a fingerprint pin is configured, it overrides chain validation entirely:
    /// the certificate is accepted if and only if its SHA-256 hash matches the pin.
    /// This is how user-accepted self-signed certificates are handled after the first
    /// connection prompt.
    ///
    /// When no pin is configured, standard chain validation must pass with no errors.
    /// </summary>
    internal static bool ValidateCertificate(
        X509Certificate? cert,
        string? fingerprintPin,
        SslPolicyErrors policyErrors)
    {
        if (cert is null)
            return false;

        if (fingerprintPin is not null)
        {
            using var cert2 = new X509Certificate2(cert);
            var thumbprint = cert2.GetCertHashString(HashAlgorithmName.SHA256);
            return string.Equals(thumbprint, fingerprintPin, StringComparison.OrdinalIgnoreCase);
        }

        return policyErrors == SslPolicyErrors.None;
    }
}
