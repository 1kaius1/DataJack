// SPDX-License-Identifier: GPL-3.0-or-later
// Spell check service abstraction, null implementation, and platform factory.
// Platform backends are in Linux.cs, Macos.cs, and Windows.cs.
// See ARCHITECTURE.md §6.5.

using System.Runtime.InteropServices;

namespace DataJack.Platform.Spell;

// ---------------------------------------------------------------------------
// Interface
// ---------------------------------------------------------------------------

/// <summary>
/// Checks spelling and provides replacement suggestions for the message input box.
/// Callers must skip checking when the input text starts with '/' (command lines).
/// Implementations must be safe to call from the UI thread and must swallow all
/// native errors internally, falling back to "everything is correct" behaviour.
/// </summary>
public interface ISpellCheckService : IDisposable
{
    /// <summary>
    /// <see langword="true"/> when the backend is functional on this platform.
    /// When <see langword="false"/> all methods return harmless no-op values and
    /// the UI should not display any spell-check affordances.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="word"/> is spelled correctly,
    /// or when <see cref="IsAvailable"/> is <see langword="false"/>.
    /// </summary>
    bool Check(string word);

    /// <summary>
    /// Returns up to <paramref name="maxSuggestions"/> replacement suggestions for a
    /// misspelled word. Returns an empty array when <see cref="IsAvailable"/> is
    /// <see langword="false"/> or when the backend has no suggestions.
    /// </summary>
    string[] Suggest(string word, int maxSuggestions = 8);
}

// ---------------------------------------------------------------------------
// Null (no-op) implementation
// ---------------------------------------------------------------------------

/// <summary>
/// No-op spell check service used when no platform backend is available or when
/// the required native library is not installed.
/// </summary>
public sealed class NullSpellCheckService : ISpellCheckService
{
    /// <inheritdoc/>
    public bool IsAvailable => false;

    /// <inheritdoc/>
    public bool Check(string word) => true;

    /// <inheritdoc/>
    public string[] Suggest(string word, int maxSuggestions = 8) => Array.Empty<string>();

    /// <inheritdoc/>
    public void Dispose() { }
}

// ---------------------------------------------------------------------------
// Factory
// ---------------------------------------------------------------------------

/// <summary>
/// Selects the appropriate <see cref="ISpellCheckService"/> for the current OS.
/// Returns a <see cref="NullSpellCheckService"/> on unrecognised platforms or when
/// the required native library is not found.
/// </summary>
public static class SpellCheckServiceFactory
{
    /// <summary>
    /// Creates and returns the best available <see cref="ISpellCheckService"/> for
    /// the current operating system. The returned instance must be disposed when
    /// the application exits.
    /// </summary>
    public static ISpellCheckService Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return TryCreate(() => new LinuxSpellCheckService());

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return TryCreate(() => new MacosSpellCheckService());

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return TryCreate(() => new WindowsSpellCheckService());

        return new NullSpellCheckService();
    }

    // Wraps backend construction in a try/catch so a missing library never
    // crashes the application — we just silently fall back to no-op.
    private static ISpellCheckService TryCreate(Func<ISpellCheckService> factory)
    {
        try
        {
            return factory();
        }
        catch
        {
            return new NullSpellCheckService();
        }
    }
}
