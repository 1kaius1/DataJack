// SPDX-License-Identifier: GPL-3.0-or-later

namespace DataJack.Core.Irc.Sasl;

/// <summary>
/// Contract for a single SASL authentication mechanism.
/// Implementations are stateful and handle exactly one authentication attempt.
/// </summary>
internal interface ISaslMechanism
{
    /// <summary>Wire name sent in AUTHENTICATE &lt;name&gt;, e.g. "SCRAM-SHA-256".</summary>
    string Name { get; }

    /// <summary>True after the mechanism has consumed its final server message.</summary>
    bool IsComplete { get; }

    /// <summary>
    /// Produce the client's next AUTHENTICATE payload.
    /// </summary>
    /// <param name="serverMessage">
    /// null when the server sent AUTHENTICATE + (empty challenge);
    /// otherwise the raw base64 string from the server's AUTHENTICATE line.
    /// </param>
    /// <returns>
    /// null  — no response to send; wait for the next server message.<br/>
    /// ""    — send AUTHENTICATE + (empty payload).<br/>
    /// other — send AUTHENTICATE &lt;value&gt; (already base64-encoded).
    /// </returns>
    /// <exception cref="SaslException">Thrown when the server's message is invalid or fails verification.</exception>
    string? Respond(string? serverMessage);
}

/// <summary>Thrown by a mechanism when an unrecoverable SASL error is detected.</summary>
internal sealed class SaslException(string message) : Exception(message);