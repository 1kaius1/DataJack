// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using DataJack.Core.Caps;
using DataJack.Core.Events;
using DataJack.Core.Irc;
using DataJack.Core.Irc.Sasl;
using DataJack.Net;
using Xunit;

namespace DataJack.Core.Tests;

// ---------------------------------------------------------------------------
// Mechanism unit tests
// ---------------------------------------------------------------------------

public sealed class SaslMechanismTests
{
    // -----------------------------------------------------------------------
    // SCRAM-SHA-256
    // Uses the nonce/salt/iterations from RFC 7677 §3 so the test is
    // reproducible, but verifies structure and self-consistent crypto rather
    // than hard-coding derived values that depend on exact PBKDF2 output.
    // -----------------------------------------------------------------------

    private const string ScramUser        = "user";
    private const string ScramPassword    = "pencil";
    private const string ScramClientNonce = "rOprNGfwEbeRWgbNEkqO";
    private const string ScramServerNonce = "%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0";
    private const string ScramSaltB64     = "W22ZaJ0SNY7soEsUEjb6gQ==";
    private const int    ScramIterations  = 4096;

    // Compute the server signature from the same inputs the mechanism would use,
    // letting the test verify the server-final step without duplicating hard-coded values.
    private static string ComputeServerSignature256(
        string password, byte[] salt, int iterations,
        string clientFirstBare, string serverFirstMsg, string clientFinalWithoutProof)
    {
        var authMessage = $"{clientFirstBare},{serverFirstMsg},{clientFinalWithoutProof}";
        var saltedPw = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations,
            HashAlgorithmName.SHA256, 32);
        using var hmacSK = new HMACSHA256(saltedPw);
        var serverKey = hmacSK.ComputeHash(Encoding.UTF8.GetBytes("Server Key"));
        using var hmacSS = new HMACSHA256(serverKey);
        return Convert.ToBase64String(
            hmacSS.ComputeHash(Encoding.UTF8.GetBytes(authMessage)));
    }

    [Fact]
    public void ScramSha256_ClientFirstMessage_HasCorrectFormat()
    {
        var m = new ScramSha256Mechanism(ScramUser, ScramPassword, () => ScramClientNonce);

        var b64 = m.Respond(null);

        Assert.NotNull(b64);
        Assert.Equal($"n,,n={ScramUser},r={ScramClientNonce}", Utf8FromB64(b64!));
        Assert.False(m.IsComplete);
    }

    [Fact]
    public void ScramSha256_ClientFinalMessage_HasCorrectStructure()
    {
        var combinedNonce = ScramClientNonce + ScramServerNonce;
        var m = new ScramSha256Mechanism(ScramUser, ScramPassword, () => ScramClientNonce);
        m.Respond(null);

        var serverFirst = $"r={combinedNonce},s={ScramSaltB64},i={ScramIterations}";
        var b64 = m.Respond(B64FromUtf8(serverFirst));

        Assert.NotNull(b64);
        var clientFinal = Utf8FromB64(b64!);
        Assert.StartsWith($"c=biws,r={combinedNonce},p=", clientFinal);
        // ClientProof for SHA-256 is 32 bytes → 44-char base64
        var proof = clientFinal[$"c=biws,r={combinedNonce},p=".Length..];
        Assert.Equal(32, Convert.FromBase64String(proof).Length);
    }

    [Fact]
    public void ScramSha256_CorrectServerSignature_CompletesExchange()
    {
        var combinedNonce = ScramClientNonce + ScramServerNonce;
        var salt = Convert.FromBase64String(ScramSaltB64);
        var gs2B64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("n,,"));
        var clientFinalWithoutProof = $"c={gs2B64},r={combinedNonce}";
        var serverFirst = $"r={combinedNonce},s={ScramSaltB64},i={ScramIterations}";

        var serverSig = ComputeServerSignature256(
            ScramPassword, salt, ScramIterations,
            $"n={ScramUser},r={ScramClientNonce}",
            serverFirst,
            clientFinalWithoutProof);

        var m = new ScramSha256Mechanism(ScramUser, ScramPassword, () => ScramClientNonce);
        m.Respond(null);
        m.Respond(B64FromUtf8(serverFirst));

        var result = m.Respond(B64FromUtf8($"v={serverSig}"));

        Assert.Null(result);
        Assert.True(m.IsComplete);
    }

    [Fact]
    public void ScramSha256_WrongServerSignature_ThrowsSaslException()
    {
        var combinedNonce = ScramClientNonce + ScramServerNonce;
        var m = new ScramSha256Mechanism(ScramUser, ScramPassword, () => ScramClientNonce);
        m.Respond(null);
        m.Respond(B64FromUtf8($"r={combinedNonce},s={ScramSaltB64},i={ScramIterations}"));

        Assert.Throws<SaslException>(
            () => m.Respond(B64FromUtf8("v=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")));
    }

    [Fact]
    public void ScramSha256_ServerNonceMismatch_ThrowsSaslException()
    {
        var m = new ScramSha256Mechanism(ScramUser, ScramPassword, () => ScramClientNonce);
        m.Respond(null);

        var tampered = $"r=WRONGNONCE,s={ScramSaltB64},i={ScramIterations}";
        Assert.Throws<SaslException>(() => m.Respond(B64FromUtf8(tampered)));
    }

    [Fact]
    public void ScramSha256_ServerFinalWithError_ThrowsSaslException()
    {
        var combinedNonce = ScramClientNonce + ScramServerNonce;
        var m = new ScramSha256Mechanism(ScramUser, ScramPassword, () => ScramClientNonce);
        m.Respond(null);
        m.Respond(B64FromUtf8($"r={combinedNonce},s={ScramSaltB64},i={ScramIterations}"));

        Assert.Throws<SaslException>(
            () => m.Respond(B64FromUtf8("e=server-does-not-support-channel-binding")));
    }

    [Fact]
    public void ScramSha512_ClientProof_Is64Bytes()
    {
        var combinedNonce = ScramClientNonce + ScramServerNonce;
        var m = new ScramSha512Mechanism(ScramUser, ScramPassword, () => ScramClientNonce);
        m.Respond(null);

        var b64 = m.Respond(B64FromUtf8(
            $"r={combinedNonce},s={ScramSaltB64},i={ScramIterations}"))!;

        var proof = Utf8FromB64(b64)[$"c=biws,r={combinedNonce},p=".Length..];
        Assert.Equal(64, Convert.FromBase64String(proof).Length);
    }

    [Fact]
    public void ScramSha512_HasCorrectMechanismName()
    {
        Assert.Equal("SCRAM-SHA-512", new ScramSha512Mechanism("u", "p").Name);
    }

    // -----------------------------------------------------------------------
    // PLAIN
    // -----------------------------------------------------------------------

    [Fact]
    public void Plain_Respond_ReturnsNullAuthcidPasswordBase64()
    {
        var m = new PlainMechanism("user", "s3cr3t");

        var response = m.Respond(null);

        Assert.NotNull(response);
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(response!));
        Assert.Equal("\0user\0s3cr3t", decoded);
        Assert.True(m.IsComplete);
    }

    [Fact]
    public void Plain_SecondRespond_ReturnsNull()
    {
        var m = new PlainMechanism("user", "pass");
        m.Respond(null);

        Assert.Null(m.Respond(null));
    }

    // -----------------------------------------------------------------------
    // EXTERNAL
    // -----------------------------------------------------------------------

    [Fact]
    public void External_Respond_ReturnsEmptyString()
    {
        var m = new ExternalMechanism();

        var response = m.Respond(null);

        Assert.Equal("", response); // empty → SaslAuthenticator sends AUTHENTICATE +
        Assert.True(m.IsComplete);
    }

    [Fact]
    public void External_SecondRespond_ReturnsNull()
    {
        var m = new ExternalMechanism();
        m.Respond(null);

        Assert.Null(m.Respond(null));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string B64FromUtf8(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static string Utf8FromB64(string b64) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(b64));
}

// ---------------------------------------------------------------------------
// SaslAuthenticator integration tests
// ---------------------------------------------------------------------------

public sealed class SaslAuthenticatorTests : IAsyncDisposable
{
    private readonly EventDispatcher _dispatcher = new();
    private readonly DuplexPipeStream _stream = new();
    private readonly IRCConnection _connection;
    private static readonly NetworkEndpoint TlsEndpoint =
        new("irc.libera.chat", 6697, UseTls: true);
    private static readonly NetworkEndpoint PlainEndpoint =
        new("irc.libera.chat", 6667, UseTls: false);

    public SaslAuthenticatorTests()
    {
        _dispatcher.Start();
        _connection = new IRCConnection("libera", new FakeNetworkProvider(_stream), _dispatcher);
    }

    // Publish CapabilityNegotiated with sasl granted to kick off authentication.
    private ValueTask TriggerSaslAsync() =>
        _dispatcher.PublishAsync(
            new CapabilityNegotiated("libera", ["sasl"], []),
            EventPriority.Normal);

    // -----------------------------------------------------------------------
    // EXTERNAL full flow
    // -----------------------------------------------------------------------

    [Fact]
    public async Task External_FullFlow_PublishesSASLSucceeded()
    {
        var succeeded = new TaskCompletionSource<SASLSucceeded>();
        _dispatcher.Subscribe<SASLSucceeded>(e => succeeded.TrySetResult(e));

        var creds = new SaslCredentials("", "", TryExternal: true);
        _ = new SaslAuthenticator("libera", _connection, _dispatcher, creds);

        await _connection.ConnectAsync(PlainEndpoint);
        await TriggerSaslAsync();

        var mechLine = await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("AUTHENTICATE EXTERNAL", mechLine);

        await _stream.SendServerDataAsync("AUTHENTICATE +\r\n");

        var responseLine = await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("AUTHENTICATE +", responseLine);

        await _stream.SendServerDataAsync(":server 903 libera :SASL authentication successful\r\n");

        var evt = await succeeded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("libera", evt.Server);
    }

    // -----------------------------------------------------------------------
    // Failure fallback
    // -----------------------------------------------------------------------

    [Fact]
    public async Task FourOhFour_FallsBackToNextMechanismInQueue()
    {
        var creds = new SaslCredentials("user", "pass", TryExternal: true);
        _ = new SaslAuthenticator("libera", _connection, _dispatcher, creds);

        await _connection.ConnectAsync(PlainEndpoint); // non-TLS: SCRAM-512, SCRAM-256, EXTERNAL
        await TriggerSaslAsync();

        var first = await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("AUTHENTICATE SCRAM-SHA-512", first);

        await _stream.SendServerDataAsync(":server 904 libera :Authentication failed\r\n");

        var second = await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("AUTHENTICATE SCRAM-SHA-256", second);
    }

    [Fact]
    public async Task AllMechanismsFail_PublishesSASLFailed()
    {
        var failed = new TaskCompletionSource<SASLFailed>();
        _dispatcher.Subscribe<SASLFailed>(e => failed.TrySetResult(e));

        var creds = new SaslCredentials("", "", TryExternal: true); // only EXTERNAL
        _ = new SaslAuthenticator("libera", _connection, _dispatcher, creds);

        await _connection.ConnectAsync(PlainEndpoint);
        await TriggerSaslAsync();

        await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2)); // AUTHENTICATE EXTERNAL
        await _stream.SendServerDataAsync("AUTHENTICATE +\r\n");
        await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2)); // AUTHENTICATE +
        await _stream.SendServerDataAsync(":server 904 libera :Authentication failed\r\n");

        var evt = await failed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("libera", evt.Server);
        Assert.Contains("904", evt.Reason);
    }

    // -----------------------------------------------------------------------
    // PLAIN on TLS vs non-TLS
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Plain_IncludedOnTls_TriedAfterScramFails()
    {
        var creds = new SaslCredentials("user", "pass");
        _ = new SaslAuthenticator("libera", _connection, _dispatcher, creds);

        await _connection.ConnectAsync(TlsEndpoint); // TLS: SCRAM-512, SCRAM-256, PLAIN
        await TriggerSaslAsync();

        var line1 = await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("AUTHENTICATE SCRAM-SHA-512", line1);
        await _stream.SendServerDataAsync(":server 904 libera :fail\r\n");

        var line2 = await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("AUTHENTICATE SCRAM-SHA-256", line2);
        await _stream.SendServerDataAsync(":server 904 libera :fail\r\n");

        var line3 = await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("AUTHENTICATE PLAIN", line3);
    }

    [Fact]
    public async Task Plain_ExcludedOnNonTls_SaslFailsAfterScram()
    {
        var failed = new TaskCompletionSource<SASLFailed>();
        _dispatcher.Subscribe<SASLFailed>(e => failed.TrySetResult(e));

        var creds = new SaslCredentials("user", "pass");
        _ = new SaslAuthenticator("libera", _connection, _dispatcher, creds);

        await _connection.ConnectAsync(PlainEndpoint); // non-TLS: only SCRAM-512, SCRAM-256
        await TriggerSaslAsync();

        await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await _stream.SendServerDataAsync(":server 904 libera :fail\r\n");

        await _stream.ReadClientLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await _stream.SendServerDataAsync(":server 904 libera :fail\r\n");

        var evt = await failed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("libera", evt.Server);
    }

    // -----------------------------------------------------------------------
    // SASLStarted event
    // -----------------------------------------------------------------------

    [Fact]
    public async Task OnCapabilityNegotiatedWithSasl_PublishesSASLStarted()
    {
        var started = new TaskCompletionSource<SASLStarted>();
        _dispatcher.Subscribe<SASLStarted>(e => started.TrySetResult(e));

        var creds = new SaslCredentials("", "", TryExternal: true);
        _ = new SaslAuthenticator("libera", _connection, _dispatcher, creds);

        await _connection.ConnectAsync(PlainEndpoint);
        await TriggerSaslAsync();

        var evt = await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("libera", evt.Server);
        Assert.Equal("EXTERNAL", evt.Mechanism);
    }

    [Fact]
    public async Task OnCapabilityNegotiatedWithoutSasl_DoesNothing()
    {
        var started = new TaskCompletionSource<SASLStarted>();
        _dispatcher.Subscribe<SASLStarted>(e => started.TrySetResult(e));

        var creds = new SaslCredentials("", "", TryExternal: true);
        _ = new SaslAuthenticator("libera", _connection, _dispatcher, creds);

        await _connection.ConnectAsync(PlainEndpoint);
        await _dispatcher.PublishAsync(
            new CapabilityNegotiated("libera", ["message-tags"], []),
            EventPriority.Normal);

        await Task.Delay(100);
        Assert.False(started.Task.IsCompleted);
    }

    // -----------------------------------------------------------------------
    // Multi-server isolation
    // -----------------------------------------------------------------------

    [Fact]
    public async Task MultiServerIsolation_OtherServerSaslDoesNotTriggerLibera()
    {
        var liberaStarted = new TaskCompletionSource<SASLStarted>();
        _dispatcher.Subscribe<SASLStarted>(e =>
        {
            if (e.Server == "libera") liberaStarted.TrySetResult(e);
        });

        var creds = new SaslCredentials("", "", TryExternal: true);
        _ = new SaslAuthenticator("libera",   _connection, _dispatcher, creds);
        _ = new SaslAuthenticator("freenode", _connection, _dispatcher, creds);

        await _connection.ConnectAsync(PlainEndpoint);

        // Only trigger freenode's SASL — libera must not start
        await _dispatcher.PublishAsync(
            new CapabilityNegotiated("freenode", ["sasl"], []),
            EventPriority.Normal);

        await Task.Delay(100);
        Assert.False(liberaStarted.Task.IsCompleted);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        await _dispatcher.DisposeAsync();
        _stream.Dispose();
    }
}
