// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DataJack.Net;
using Xunit;

namespace DataJack.Core.Tests;

public sealed class NetworkProviderTests
{
    // ---------------------------------------------------------------------------
    // NetworkEndpoint defaults
    // ---------------------------------------------------------------------------

    [Fact]
    public void NetworkEndpoint_DefaultAddressFamily_IsPreferIpv6()
    {
        var ep = new NetworkEndpoint("irc.libera.chat", 6697, UseTls: true);
        Assert.Equal(AddressFamilyPreference.PreferIpv6, ep.AddressFamily);
    }

    [Fact]
    public void NetworkEndpoint_DefaultCertificateAndPin_AreNull()
    {
        var ep = new NetworkEndpoint("irc.libera.chat", 6697, UseTls: true);
        Assert.Null(ep.ClientCertificate);
        Assert.Null(ep.TlsFingerprintPin);
    }

    // ---------------------------------------------------------------------------
    // TlsCertificateException
    // ---------------------------------------------------------------------------

    [Fact]
    public void TlsCertificateException_PropertiesArePreserved()
    {
        var ex = new TlsCertificateException(
            "irc.libera.chat",
            "validation failed",
            certificate: null,
            SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.Equal("irc.libera.chat", ex.Host);
        Assert.Equal("validation failed", ex.Message);
        Assert.Null(ex.Certificate);
        Assert.Equal(SslPolicyErrors.RemoteCertificateChainErrors, ex.PolicyErrors);
    }

    // ---------------------------------------------------------------------------
    // HappyEyeballs.SortAddresses
    // ---------------------------------------------------------------------------

    [Fact]
    public void SortAddresses_PreferIpv6_PlacesIpv6First()
    {
        var ipv4 = IPAddress.Parse("1.2.3.4");
        var ipv6 = IPAddress.Parse("2001:db8::1");
        var sorted = HappyEyeballs.SortAddresses(
            [ipv4, ipv6], AddressFamilyPreference.PreferIpv6);

        Assert.Equal(ipv6, sorted[0]);
        Assert.Equal(ipv4, sorted[1]);
    }

    [Fact]
    public void SortAddresses_PreferIpv4_PlacesIpv4First()
    {
        var ipv4 = IPAddress.Parse("1.2.3.4");
        var ipv6 = IPAddress.Parse("2001:db8::1");
        var sorted = HappyEyeballs.SortAddresses(
            [ipv4, ipv6], AddressFamilyPreference.PreferIpv4);

        Assert.Equal(ipv4, sorted[0]);
        Assert.Equal(ipv6, sorted[1]);
    }

    [Fact]
    public void SortAddresses_Ipv6Only_FiltersOutIpv4()
    {
        var ipv4 = IPAddress.Parse("1.2.3.4");
        var ipv6 = IPAddress.Parse("2001:db8::1");
        var sorted = HappyEyeballs.SortAddresses(
            [ipv4, ipv6], AddressFamilyPreference.Ipv6Only);

        Assert.Single(sorted);
        Assert.Equal(ipv6, sorted[0]);
    }

    [Fact]
    public void SortAddresses_Ipv4Only_FiltersOutIpv6()
    {
        var ipv4 = IPAddress.Parse("1.2.3.4");
        var ipv6 = IPAddress.Parse("2001:db8::1");
        var sorted = HappyEyeballs.SortAddresses(
            [ipv4, ipv6], AddressFamilyPreference.Ipv4Only);

        Assert.Single(sorted);
        Assert.Equal(ipv4, sorted[0]);
    }

    [Fact]
    public void SortAddresses_EmptyInput_ReturnsEmpty()
    {
        var sorted = HappyEyeballs.SortAddresses([], AddressFamilyPreference.PreferIpv6);
        Assert.Empty(sorted);
    }

    // ---------------------------------------------------------------------------
    // TlsTransport.ValidateCertificate
    // ---------------------------------------------------------------------------

    [Fact]
    public void ValidateCertificate_NoErrors_NoPin_ReturnsTrue()
    {
        using var cert = MakeSelfSignedCert();
        bool result = TlsTransport.ValidateCertificate(cert, null, SslPolicyErrors.None);
        Assert.True(result);
    }

    [Fact]
    public void ValidateCertificate_ChainError_NoPin_ReturnsFalse()
    {
        using var cert = MakeSelfSignedCert();
        bool result = TlsTransport.ValidateCertificate(
            cert, null, SslPolicyErrors.RemoteCertificateChainErrors);
        Assert.False(result);
    }

    [Fact]
    public void ValidateCertificate_CorrectPin_ChainError_ReturnsTrue()
    {
        using var cert = MakeSelfSignedCert();
        var pin = cert.GetCertHashString(HashAlgorithmName.SHA256);

        bool result = TlsTransport.ValidateCertificate(
            cert, pin, SslPolicyErrors.RemoteCertificateChainErrors);
        Assert.True(result);
    }

    [Fact]
    public void ValidateCertificate_WrongPin_NoErrors_ReturnsFalse()
    {
        using var cert = MakeSelfSignedCert();
        const string wrongPin = "0000000000000000000000000000000000000000000000000000000000000000";

        bool result = TlsTransport.ValidateCertificate(cert, wrongPin, SslPolicyErrors.None);
        Assert.False(result);
    }

    [Fact]
    public void ValidateCertificate_NullCert_ReturnsFalse()
    {
        bool result = TlsTransport.ValidateCertificate(null, null, SslPolicyErrors.None);
        Assert.False(result);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static X509Certificate2 MakeSelfSignedCert()
    {
        using var key = ECDsa.Create();
        var req = new CertificateRequest("cn=test", key, HashAlgorithmName.SHA256);
        return req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));
    }
}
