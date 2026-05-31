// SPDX-License-Identifier: GPL-3.0-or-later

namespace DataJack.Core.Irc.Sasl;

/// <summary>
/// EXTERNAL mechanism (RFC 4422). Authentication identity is derived from the
/// TLS client certificate already presented during the handshake. Sends an
/// empty payload (AUTHENTICATE +) so the server uses the cert's CN/SAN as the
/// account name.
/// </summary>
internal sealed class ExternalMechanism : ISaslMechanism
{
    public string Name => "EXTERNAL";
    public bool IsComplete { get; private set; }

    public string? Respond(string? serverMessage)
    {
        if (IsComplete) return null;
        IsComplete = true;
        return ""; // empty payload → SaslAuthenticator sends AUTHENTICATE +
    }
}
