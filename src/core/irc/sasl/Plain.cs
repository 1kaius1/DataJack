// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace DataJack.Core.Irc.Sasl;

/// <summary>
/// PLAIN mechanism (RFC 4616). Sends credentials as a single base64-encoded
/// "\0authcid\0password" payload.
///
/// MUST only be used over a TLS connection. The SaslAuthenticator enforces this;
/// this class itself does not check.
/// </summary>
internal sealed class PlainMechanism : ISaslMechanism
{
    public string Name => "PLAIN";
    public bool IsComplete { get; private set; }

    private readonly string _authcid;
    private readonly string _password;

    public PlainMechanism(string authcid, string password)
    {
        _authcid = authcid;
        _password = password;
    }

    public string? Respond(string? serverMessage)
    {
        if (IsComplete) return null;
        IsComplete = true;
        // Format: \0 authcid \0 passwd  (authzid left empty)
        var payload = Encoding.UTF8.GetBytes($"\0{_authcid}\0{_password}");
        return Convert.ToBase64String(payload);
    }
}
