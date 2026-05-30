// SPDX-License-Identifier: GPL-3.0-or-later

namespace DataJack.Core.State;

/// <summary>
/// Single-writer IRC world state model. See ARCHITECTURE.md §9.
///
/// The event dispatch thread is the only permitted caller of <see cref="Apply"/>.
/// All other threads read through <see cref="CreateQuery"/>, which returns a view
/// bound to the snapshot at the moment of the call. Snapshot replacement is atomic
/// via a volatile field write, so reads on any thread are always non-blocking and
/// consistent (at most one event cycle stale).
/// </summary>
public sealed class IRCStateModel
{
    // volatile guarantees that every read of _snapshot hits memory and is never
    // served from a CPU register or reordered past a subsequent read. Combined with
    // the single-writer contract, this is sufficient for lock-free snapshot access.
    private volatile IrcWorldSnapshot _snapshot = IrcWorldSnapshot.Empty;

    /// <summary>
    /// Apply a pure mutation to the current snapshot and atomically publish the result.
    /// Must only be called from the event dispatch thread.
    /// </summary>
    /// <param name="mutate">
    /// A function that receives the current snapshot and returns the next snapshot.
    /// Must be pure: no side effects, no I/O, no blocking calls.
    /// </param>
    /// <returns>The new snapshot that was published.</returns>
    public IrcWorldSnapshot Apply(Func<IrcWorldSnapshot, IrcWorldSnapshot> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        var next = mutate(_snapshot);
        _snapshot = next;
        return next;
    }

    /// <summary>
    /// Return a query object bound to the current snapshot.
    /// The query object is a stable, point-in-time view: subsequent calls to
    /// <see cref="Apply"/> do not affect an already-created query object.
    /// </summary>
    public IRCStateQuery CreateQuery() => new StateQuery(_snapshot);
}
