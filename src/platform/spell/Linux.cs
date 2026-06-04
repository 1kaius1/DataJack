// SPDX-License-Identifier: GPL-3.0-or-later
// Linux spell check backend via Enchant-2 (libenchant-2.so.2).
// Enchant acts as a broker over installed backends: Hunspell, Aspell, Nuspell, etc.
// If the library or a dictionary for the current locale is not installed, the service
// reports IsAvailable = false and all checks return true (words are "correct").
// See ARCHITECTURE.md §6.5.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace DataJack.Platform.Spell;

/// <summary>
/// Linux spell check service using Enchant-2 (libenchant-2.so.2).
/// Supports Hunspell, Aspell, and Nuspell backends through the Enchant broker.
/// Falls back to <see cref="NullSpellCheckService"/> behaviour when the library
/// or a suitable dictionary is not installed.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxSpellCheckService : ISpellCheckService
{
    private const string Lib = "libenchant-2.so.2";

    // -----------------------------------------------------------------------
    // Enchant-2 P/Invoke declarations
    // -----------------------------------------------------------------------

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr enchant_broker_init();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void enchant_broker_free(IntPtr broker);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr enchant_broker_request_dict(IntPtr broker, byte[] tag);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void enchant_broker_free_dict(IntPtr broker, IntPtr dict);

    // Returns 0 if the word is correct, non-zero if misspelled.
    // len = -1 means use strlen.
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int enchant_dict_check(IntPtr dict, byte[] word, IntPtr len);

    // Returns a char** of suggestions; sets nSuggs to the count.
    // Returns NULL when no suggestions are available.
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr enchant_dict_suggest(IntPtr dict, byte[] word, IntPtr len, out UIntPtr nSuggs);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void enchant_dict_free_string_list(IntPtr dict, IntPtr strList);

    // -----------------------------------------------------------------------
    // State
    // -----------------------------------------------------------------------

    private readonly IntPtr _broker;
    private readonly IntPtr _dict;
    private bool _disposed;

    /// <inheritdoc/>
    public bool IsAvailable => _dict != IntPtr.Zero;

    // -----------------------------------------------------------------------
    // Construction / disposal
    // -----------------------------------------------------------------------

    internal LinuxSpellCheckService()
    {
        try
        {
            _broker = enchant_broker_init();
            if (_broker == IntPtr.Zero)
                return;

            // Try the full locale tag first (e.g. "en_US"), then fall back to
            // the language code alone (e.g. "en").
            string locale = System.Globalization.CultureInfo.CurrentUICulture.Name
                .Replace('-', '_');

            _dict = RequestDict(locale);

            if (_dict == IntPtr.Zero && locale.Contains('_'))
                _dict = RequestDict(locale[..locale.IndexOf('_')]);
        }
        catch
        {
            // libenchant-2 not installed or any other native error — stay uninitialized.
        }
    }

    private IntPtr RequestDict(string lang) =>
        enchant_broker_request_dict(_broker, Encoding.UTF8.GetBytes(lang + '\0'));

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            if (_dict != IntPtr.Zero && _broker != IntPtr.Zero)
                enchant_broker_free_dict(_broker, _dict);
            if (_broker != IntPtr.Zero)
                enchant_broker_free(_broker);
        }
        catch { /* swallow errors during shutdown */ }
    }

    // -----------------------------------------------------------------------
    // ISpellCheckService
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public bool Check(string word)
    {
        if (!IsAvailable || string.IsNullOrEmpty(word))
            return true;

        try
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(word + '\0');
            return enchant_dict_check(_dict, utf8, new IntPtr(-1)) == 0;
        }
        catch
        {
            return true;
        }
    }

    /// <inheritdoc/>
    public string[] Suggest(string word, int maxSuggestions = 8)
    {
        if (!IsAvailable || string.IsNullOrEmpty(word))
            return Array.Empty<string>();

        try
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(word + '\0');
            IntPtr listPtr = enchant_dict_suggest(_dict, utf8, new IntPtr(-1), out UIntPtr count);
            if (listPtr == IntPtr.Zero || count == UIntPtr.Zero)
                return Array.Empty<string>();

            int n = (int)Math.Min((uint)count, (uint)maxSuggestions);
            var results = new string[n];
            for (int i = 0; i < n; i++)
            {
                IntPtr strPtr = Marshal.ReadIntPtr(listPtr, i * IntPtr.Size);
                results[i] = Marshal.PtrToStringUTF8(strPtr) ?? string.Empty;
            }

            enchant_dict_free_string_list(_dict, listPtr);
            return results;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
