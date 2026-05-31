// SPDX-License-Identifier: GPL-3.0-or-later

namespace DataJack.Net;

/// <summary>
/// SOCKS5 proxy transport. Not yet implemented — planned for Phase 3.
/// See ARCHITECTURE.md §10.4 for the design (per-server proxy config, remote DNS,
/// optional TLS over the proxied connection).
/// </summary>
public sealed class Socks5Transport : INetworkProvider
{
    /// <inheritdoc/>
    /// <exception cref="NotImplementedException">Always. Implemented in Phase 3.</exception>
    public Task<Stream> ConnectAsync(NetworkEndpoint endpoint, CancellationToken ct = default) =>
        throw new NotImplementedException("SOCKS5 transport is not yet implemented (Phase 3).");
}
