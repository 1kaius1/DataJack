// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.Events;
using DataJack.Core.Irc;
using DataJack.Core.Irc.Sasl;

namespace DataJack.Core.Caps;

/// <summary>
/// Credentials supplied to <see cref="SaslAuthenticator"/>.
/// </summary>
/// <param name="AccountName">SASL username (usually the IRC account / NickServ account name).</param>
/// <param name="Password">Password for SCRAM and PLAIN mechanisms. Leave empty to disable both.</param>
/// <param name="TryExternal">
/// When true, EXTERNAL is added to the mechanism queue after SCRAM. Requires a TLS client
/// certificate to have been configured in the <see cref="IRCConnection"/>'s TLS settings.
/// </param>
public sealed record SaslCredentials(
    string AccountName,
    string Password,
    bool TryExternal = false);

/// <summary>
/// Drives the SASL authentication exchange after <see cref="CapabilityNegotiator"/> grants
/// the "sasl" capability.
///
/// Mechanism preference order (ARCHITECTURE.md §4.3):
///   1. SCRAM-SHA-512   — mutual auth; preferred if supported
///   2. SCRAM-SHA-256   — fallback SCRAM variant
///   3. EXTERNAL        — cert-based; only when TryExternal is true
///   4. PLAIN           — password in clear; only permitted over TLS
///
/// On any unrecoverable failure, SASLFailed is emitted. Phase 1 does not
/// implement continue-on-failure; the caller is expected to disconnect.
/// </summary>
public sealed class SaslAuthenticator
{
    private readonly string _serverId;
    private readonly IRCConnection _connection;
    private readonly EventDispatcher _dispatcher;
    private readonly SaslCredentials _credentials;

    // Serialises concurrent Task.Run handlers (same pattern as CapabilityNegotiator).
    private readonly SemaphoreSlim _lock = new(1, 1);

    private List<ISaslMechanism> _mechanisms = [];
    private ISaslMechanism? _active;
    private int _mechanismIndex;

    public SaslAuthenticator(
        string serverId,
        IRCConnection connection,
        EventDispatcher dispatcher,
        SaslCredentials credentials)
    {
        _serverId = serverId;
        _connection = connection;
        _dispatcher = dispatcher;
        _credentials = credentials;

        dispatcher.Subscribe<CapabilityNegotiated>(OnCapabilityNegotiated);
        dispatcher.Subscribe<RawLineReceived>(OnRawLineReceived);
    }

    // ---------------------------------------------------------------------------
    // Event handlers
    // ---------------------------------------------------------------------------

    private void OnCapabilityNegotiated(CapabilityNegotiated evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;
        if (!evt.Granted.Contains("sasl", StringComparer.OrdinalIgnoreCase)) return;

        _ = Task.Run(StartSaslAsync);
    }

    private void OnRawLineReceived(RawLineReceived evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;

        _ = Task.Run(() => ProcessLineAsync(evt.Line));
    }

    // ---------------------------------------------------------------------------
    // SASL startup
    // ---------------------------------------------------------------------------

    private async Task StartSaslAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            _mechanisms = BuildMechanisms(_credentials, _connection.IsTls);

            if (_mechanisms.Count == 0)
            {
                await _dispatcher.PublishAsync(
                    new SASLFailed(_serverId, "No applicable SASL mechanism configured"),
                    EventPriority.Normal).ConfigureAwait(false);
                return;
            }

            _mechanismIndex = 0;
            _active = _mechanisms[0];
            await SendAuthenticateAsync(_active.Name).ConfigureAwait(false);
            await _dispatcher.PublishAsync(
                new SASLStarted(_serverId, _active.Name),
                EventPriority.Normal).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static List<ISaslMechanism> BuildMechanisms(SaslCredentials creds, bool isTls)
    {
        var list = new List<ISaslMechanism>();

        if (!string.IsNullOrEmpty(creds.Password))
        {
            list.Add(new ScramSha512Mechanism(creds.AccountName, creds.Password));
            list.Add(new ScramSha256Mechanism(creds.AccountName, creds.Password));
        }

        if (creds.TryExternal)
            list.Add(new ExternalMechanism());

        // PLAIN only over TLS — sending credentials over plaintext is not acceptable
        if (!string.IsNullOrEmpty(creds.Password) && isTls)
            list.Add(new PlainMechanism(creds.AccountName, creds.Password));

        return list;
    }

    // ---------------------------------------------------------------------------
    // Line processing
    // ---------------------------------------------------------------------------

    private async Task ProcessLineAsync(string line)
    {
        var msg = IRCParser.ParseMessage(line);
        if (msg is null) return;

        switch (msg.Value.Command)
        {
            case "AUTHENTICATE":
                await WithLockAsync(() => HandleAuthenticateAsync(msg.Value))
                    .ConfigureAwait(false);
                break;

            case "903":
                await WithLockAsync(HandleSuccessAsync).ConfigureAwait(false);
                break;

            case "904" or "905" or "906":
                await WithLockAsync(
                    () => HandleFailureAsync(msg.Value.Command, msg.Value.Param(1)))
                    .ConfigureAwait(false);
                break;

            case "908":
                // Server lists available mechanisms; find one we support
                await WithLockAsync(
                    () => HandleMechanismUnavailableAsync(msg.Value.Param(1)))
                    .ConfigureAwait(false);
                break;
        }
    }

    // ---------------------------------------------------------------------------
    // Protocol handlers (called under _lock)
    // ---------------------------------------------------------------------------

    private async Task HandleAuthenticateAsync(IrcMessage msg)
    {
        if (_active is null) return;

        // "+" means empty challenge; anything else is a base64 challenge
        var challenge = msg.Param(0) == "+" ? null : msg.Param(0);

        string? response;
        try
        {
            response = _active.Respond(challenge);
        }
        catch (SaslException ex)
        {
            await AbortAsync(ex.Message).ConfigureAwait(false);
            return;
        }

        if (response is null) return; // mechanism says "wait for more server data"

        var payload = response.Length == 0 ? "+" : response;
        await SendAuthenticateAsync(payload).ConfigureAwait(false);
    }

    private async Task HandleSuccessAsync()
    {
        if (_active is null) return;
        _active = null;

        await _dispatcher.PublishAsync(
            new SASLSucceeded(_serverId),
            EventPriority.Normal).ConfigureAwait(false);
    }

    private async Task HandleFailureAsync(string code, string detail)
    {
        if (_active is not null)
        {
            // Try the next mechanism in the queue before giving up
            _mechanismIndex++;
            if (_mechanismIndex < _mechanisms.Count)
            {
                _active = _mechanisms[_mechanismIndex];
                await SendAuthenticateAsync(_active.Name).ConfigureAwait(false);
                await _dispatcher.PublishAsync(
                    new SASLStarted(_serverId, _active.Name),
                    EventPriority.Normal).ConfigureAwait(false);
                return;
            }
        }

        _active = null;
        await _dispatcher.PublishAsync(
            new SASLFailed(_serverId, $"{code}: {detail}"),
            EventPriority.Normal).ConfigureAwait(false);
    }

    private async Task HandleMechanismUnavailableAsync(string availableCsv)
    {
        // availableCsv is a comma-separated list from the 908 numeric
        var available = new HashSet<string>(
            availableCsv.Split(',', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);

        _mechanismIndex++;
        while (_mechanismIndex < _mechanisms.Count)
        {
            var candidate = _mechanisms[_mechanismIndex];
            if (available.Contains(candidate.Name))
            {
                _active = candidate;
                await SendAuthenticateAsync(_active.Name).ConfigureAwait(false);
                await _dispatcher.PublishAsync(
                    new SASLStarted(_serverId, _active.Name),
                    EventPriority.Normal).ConfigureAwait(false);
                return;
            }
            _mechanismIndex++;
        }

        _active = null;
        await _dispatcher.PublishAsync(
            new SASLFailed(_serverId, "908: No mutually supported SASL mechanism"),
            EventPriority.Normal).ConfigureAwait(false);
    }

    private async Task AbortAsync(string reason)
    {
        _active = null;
        await SendAuthenticateAsync("*").ConfigureAwait(false);
        await _dispatcher.PublishAsync(
            new SASLFailed(_serverId, reason),
            EventPriority.Normal).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private Task SendAuthenticateAsync(string payload) =>
        _connection.SendLineAsync($"AUTHENTICATE {payload}");

    private async Task WithLockAsync(Func<Task> work)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try { await work().ConfigureAwait(false); }
        finally { _lock.Release(); }
    }
}
