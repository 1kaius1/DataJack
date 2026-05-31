// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Sockets;

namespace DataJack.Net;

/// <summary>
/// Plaintext TCP transport. Resolves DNS with happy-eyeballs dual-stack logic and
/// returns a <see cref="NetworkStream"/> that owns its underlying socket.
/// </summary>
public sealed class TcpTransport : INetworkProvider
{
    /// <inheritdoc/>
    public async Task<Stream> ConnectAsync(NetworkEndpoint endpoint, CancellationToken ct = default)
    {
        var socket = await HappyEyeballs.ConnectAsync(
            endpoint.Host,
            endpoint.Port,
            endpoint.AddressFamily,
            ct).ConfigureAwait(false);

        // ownsSocket: true — disposing the NetworkStream will close and dispose the socket.
        return new NetworkStream(socket, ownsSocket: true);
    }
}
