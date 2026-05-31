// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Sockets;

namespace DataJack.Net;

/// <summary>
/// RFC 8305 happy-eyeballs connection helper. Resolves both A and AAAA records,
/// sorts by the configured address-family preference, then starts connection attempts
/// 250 ms apart. Returns the first socket that connects; cancels and disposes the rest.
/// </summary>
internal static class HappyEyeballs
{
    /// <summary>Delay before starting each successive connection attempt.</summary>
    private static readonly TimeSpan FallbackDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Resolve <paramref name="host"/> and connect to <paramref name="port"/> using
    /// happy-eyeballs parallel attempt logic.
    /// </summary>
    /// <returns>A connected, non-blocking <see cref="Socket"/> with Nagle disabled.</returns>
    internal static async Task<Socket> ConnectAsync(
        string host,
        int port,
        AddressFamilyPreference preference,
        CancellationToken ct)
    {
        var addresses = await ResolveAsync(host, preference, ct).ConfigureAwait(false);

        if (addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        if (addresses.Length == 1)
            return await ConnectToAddressAsync(addresses[0], port, ct).ConfigureAwait(false);

        return await ConnectWithFallbackAsync(addresses, port, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolve DNS and return addresses sorted by <paramref name="preference"/>.
    /// Only addresses matching the preference filter are returned for *Only modes.
    /// </summary>
    internal static async Task<IPAddress[]> ResolveAsync(
        string host,
        AddressFamilyPreference preference,
        CancellationToken ct)
    {
        var all = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        return SortAddresses(all, preference);
    }

    /// <summary>
    /// Sort (and optionally filter) <paramref name="addresses"/> by <paramref name="preference"/>.
    /// Exposed as internal for unit testing.
    /// </summary>
    internal static IPAddress[] SortAddresses(IPAddress[] addresses, AddressFamilyPreference preference)
    {
        return preference switch
        {
            AddressFamilyPreference.Ipv6Only =>
                addresses
                    .Where(a => a.AddressFamily == AddressFamily.InterNetworkV6)
                    .ToArray(),

            AddressFamilyPreference.Ipv4Only =>
                addresses
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .ToArray(),

            AddressFamilyPreference.PreferIpv4 =>
                addresses
                    .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                    .ToArray(),

            _ => // PreferIpv6 (default)
                addresses
                    .OrderBy(a => a.AddressFamily == AddressFamily.InterNetworkV6 ? 0 : 1)
                    .ToArray(),
        };
    }

    /// <summary>
    /// Start connection attempts 250 ms apart. The first to succeed wins;
    /// the rest are cancelled and their sockets disposed.
    /// </summary>
    private static async Task<Socket> ConnectWithFallbackAsync(
        IPAddress[] addresses,
        int port,
        CancellationToken ct)
    {
        // winnerCts lets us cancel all losing attempts once a winner is found.
        using var winnerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var attempts = new List<Task<Socket>>(addresses.Length);

        // First address starts immediately; each subsequent address is delayed by
        // an additional 250 ms (absolute from t=0, not relative to the previous attempt).
        attempts.Add(ConnectToAddressAsync(addresses[0], port, winnerCts.Token));
        for (int i = 1; i < addresses.Length; i++)
        {
            var addr = addresses[i];
            attempts.Add(DelayThenConnectAsync(FallbackDelay * i, addr, port, winnerCts.Token));
        }

        var exceptions = new List<Exception>();
        var pending = new List<Task<Socket>>(attempts);

        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(finished);

            if (finished.IsCompletedSuccessfully)
            {
                // Cancel all remaining attempts and dispose any sockets that arrive late.
                winnerCts.Cancel();
                _ = DisposeLosingSocketsAsync(pending);
                return finished.Result;
            }

            if (finished.IsFaulted && finished.Exception is not null)
                exceptions.AddRange(finished.Exception.InnerExceptions);
        }

        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);

        throw new AggregateException("All connection attempts failed.", exceptions);
    }

    private static async Task<Socket> DelayThenConnectAsync(
        TimeSpan delay,
        IPAddress address,
        int port,
        CancellationToken ct)
    {
        await Task.Delay(delay, ct).ConfigureAwait(false);
        return await ConnectToAddressAsync(address, port, ct).ConfigureAwait(false);
    }

    private static async Task<Socket> ConnectToAddressAsync(
        IPAddress address,
        int port,
        CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            // Disable Nagle algorithm: IRC sends many small lines; buffering adds latency.
            NoDelay = true,
        };

        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), ct).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Fire-and-forget cleanup: awaits each losing task and disposes the socket if it
    /// completed successfully after we had already found a winner.
    /// </summary>
    private static async Task DisposeLosingSocketsAsync(IEnumerable<Task<Socket>> tasks)
    {
        foreach (var task in tasks)
        {
            try
            {
                var socket = await task.ConfigureAwait(false);
                socket.Dispose();
            }
            catch
            {
                // Task faulted or was cancelled — nothing to dispose.
            }
        }
    }
}
