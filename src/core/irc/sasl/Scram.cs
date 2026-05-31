// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;

namespace DataJack.Core.Irc.Sasl;

/// <summary>
/// SCRAM (Salted Challenge Response Authentication Mechanism) base implementation.
/// Covers both SCRAM-SHA-256 (RFC 7677) and SCRAM-SHA-512, sharing all logic except
/// the hash primitive. Uses GS2 header "n,," (no channel binding) as required for
/// IRC SASL where TLS channel binding is not negotiated.
///
/// Exchange summary:
///   C: AUTHENTICATE &lt;mechanism&gt;
///   S: AUTHENTICATE +
///   C: AUTHENTICATE &lt;base64(n,,n=&lt;nick&gt;,r=&lt;client-nonce&gt;)&gt;
///   S: AUTHENTICATE &lt;base64(r=&lt;combined-nonce&gt;,s=&lt;salt&gt;,i=&lt;iters&gt;)&gt;
///   C: AUTHENTICATE &lt;base64(c=biws,r=&lt;combined-nonce&gt;,p=&lt;client-proof&gt;)&gt;
///   S: AUTHENTICATE &lt;base64(v=&lt;server-signature&gt;)&gt;
///   S: 903
/// </summary>
internal abstract class ScramMechanism : ISaslMechanism
{
    private enum Stage { Initial, WaitingForServerFirst, WaitingForServerFinal, Complete }

    private Stage _stage = Stage.Initial;
    private string? _nonce;
    private string? _clientFirstMessageBare;
    private string? _expectedServerSignature;

    private readonly string _authcid;
    private readonly string _password;
    private readonly Func<string> _nonceFactory;

    public string Name { get; }
    public bool IsComplete => _stage == Stage.Complete;

    protected ScramMechanism(string name, string authcid, string password, Func<string>? nonceFactory)
    {
        Name = name;
        // Comma and equals are forbidden unescaped in SCRAM name fields (RFC 5802 §5.1)
        _authcid = authcid.Replace("=", "=3D").Replace(",", "=2C");
        _password = password;
        _nonceFactory = nonceFactory
            ?? (() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)));
    }

    /// <summary>Output length of the hash in bytes (32 for SHA-256, 64 for SHA-512).</summary>
    protected abstract int KeyLength { get; }

    /// <summary>Hash algorithm name for PBKDF2.</summary>
    protected abstract HashAlgorithmName HashAlgorithmName { get; }

    /// <summary>Create an HMAC instance keyed with <paramref name="key"/>.</summary>
    protected abstract HMAC CreateHmac(byte[] key);

    /// <summary>Compute a one-shot hash of <paramref name="input"/>.</summary>
    protected abstract byte[] HashBytes(byte[] input);

    public string? Respond(string? serverMessage) =>
        _stage switch
        {
            Stage.Initial                => Initial(),
            Stage.WaitingForServerFirst  => ServerFirst(serverMessage!),
            Stage.WaitingForServerFinal  => ServerFinal(serverMessage!),
            Stage.Complete               => null,
            _ => throw new InvalidOperationException("SCRAM: unexpected state"),
        };

    // ---------------------------------------------------------------------------
    // Stage handlers
    // ---------------------------------------------------------------------------

    private string Initial()
    {
        _nonce = _nonceFactory();
        _clientFirstMessageBare = $"n={_authcid},r={_nonce}";
        _stage = Stage.WaitingForServerFirst;
        return ToBase64($"n,,{_clientFirstMessageBare}");
    }

    private string ServerFirst(string serverBase64)
    {
        var serverFirst = FromBase64(serverBase64);
        var attrs = ParseAttributes(serverFirst);

        if (!attrs.TryGetValue('r', out var serverNonce) ||
            !attrs.TryGetValue('s', out var saltB64) ||
            !attrs.TryGetValue('i', out var iterStr))
            throw new SaslException("SCRAM: malformed server-first-message");

        if (!serverNonce.StartsWith(_nonce!, StringComparison.Ordinal))
            throw new SaslException("SCRAM: server nonce does not start with client nonce");

        if (!int.TryParse(iterStr, out int iterations) || iterations < 1)
            throw new SaslException("SCRAM: invalid iteration count");

        var salt = Convert.FromBase64String(saltB64);

        // SaltedPassword = PBKDF2(Normalize(password), salt, iterations, dkLen)
        var saltedPassword = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(_password),
            salt,
            iterations,
            HashAlgorithmName,
            KeyLength);

        // GS2 header "n,," → base64 → "biws" (constant for this binding type)
        var gs2HeaderB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("n,,"));
        var clientFinalWithoutProof = $"c={gs2HeaderB64},r={serverNonce}";

        var authMessage = $"{_clientFirstMessageBare},{serverFirst},{clientFinalWithoutProof}";
        var authMessageBytes = Encoding.UTF8.GetBytes(authMessage);

        using var hmacClientKey = CreateHmac(saltedPassword);
        var clientKey = hmacClientKey.ComputeHash(Encoding.UTF8.GetBytes("Client Key"));
        var storedKey = HashBytes(clientKey);

        using var hmacClientSig = CreateHmac(storedKey);
        var clientSignature = hmacClientSig.ComputeHash(authMessageBytes);

        var clientProof = XorBytes(clientKey, clientSignature);

        using var hmacServerKey = CreateHmac(saltedPassword);
        var serverKey = hmacServerKey.ComputeHash(Encoding.UTF8.GetBytes("Server Key"));

        using var hmacServerSig = CreateHmac(serverKey);
        _expectedServerSignature = Convert.ToBase64String(
            hmacServerSig.ComputeHash(authMessageBytes));

        var clientFinal = $"{clientFinalWithoutProof},p={Convert.ToBase64String(clientProof)}";
        _stage = Stage.WaitingForServerFinal;
        return ToBase64(clientFinal);
    }

    private string? ServerFinal(string serverBase64)
    {
        var serverFinal = FromBase64(serverBase64);
        var attrs = ParseAttributes(serverFinal);

        if (attrs.TryGetValue('e', out var error))
            throw new SaslException($"SCRAM: server error: {error}");

        if (!attrs.TryGetValue('v', out var serverSig))
            throw new SaslException("SCRAM: server-final-message missing v= attribute");

        if (serverSig != _expectedServerSignature)
            throw new SaslException("SCRAM: server signature mismatch — possible MITM");

        _stage = Stage.Complete;
        return null; // no response; wait for 903
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static Dictionary<char, string> ParseAttributes(string message)
    {
        var result = new Dictionary<char, string>();
        foreach (var part in message.Split(','))
        {
            if (part.Length >= 2 && part[1] == '=')
                result[part[0]] = part[2..];
        }
        return result;
    }

    private static byte[] XorBytes(byte[] a, byte[] b)
    {
        var result = new byte[a.Length];
        for (int i = 0; i < a.Length; i++)
            result[i] = (byte)(a[i] ^ b[i]);
        return result;
    }

    private static string ToBase64(string utf8Text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(utf8Text));

    private static string FromBase64(string b64) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(b64));
}

/// <summary>SCRAM-SHA-256 (RFC 7677). Preferred over SCRAM-SHA-512 by most servers.</summary>
internal sealed class ScramSha256Mechanism : ScramMechanism
{
    public ScramSha256Mechanism(string authcid, string password, Func<string>? nonceFactory = null)
        : base("SCRAM-SHA-256", authcid, password, nonceFactory) { }

    protected override int KeyLength => 32;
    protected override HashAlgorithmName HashAlgorithmName => HashAlgorithmName.SHA256;
    protected override HMAC CreateHmac(byte[] key) => new HMACSHA256(key);
    protected override byte[] HashBytes(byte[] input) => SHA256.HashData(input);
}

/// <summary>SCRAM-SHA-512. Higher security margin than SHA-256; preferred if supported.</summary>
internal sealed class ScramSha512Mechanism : ScramMechanism
{
    public ScramSha512Mechanism(string authcid, string password, Func<string>? nonceFactory = null)
        : base("SCRAM-SHA-512", authcid, password, nonceFactory) { }

    protected override int KeyLength => 64;
    protected override HashAlgorithmName HashAlgorithmName => HashAlgorithmName.SHA512;
    protected override HMAC CreateHmac(byte[] key) => new HMACSHA512(key);
    protected override byte[] HashBytes(byte[] input) => SHA512.HashData(input);
}