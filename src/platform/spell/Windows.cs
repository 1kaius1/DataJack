// SPDX-License-Identifier: GPL-3.0-or-later
// Windows spell check backend via ISpellChecker COM interface (Windows 8+).
// The target for Phase 4 is the WinRT C# projection on the net10.0-windows TFM;
// this Phase 3 implementation uses COM P/Invoke which is available on all .NET
// platforms when run on Windows. See ARCHITECTURE.md §6.5.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DataJack.Platform.Spell;

/// <summary>
/// Windows spell check service using the <c>ISpellChecker</c> COM interface
/// (available since Windows 8). Checks words and provides suggestions through
/// the Windows spell check API, which uses the system language dictionaries.
/// Falls back to <see cref="NullSpellCheckService"/> behaviour when the COM
/// server is unavailable.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsSpellCheckService : ISpellCheckService
{
    // -----------------------------------------------------------------------
    // COM GUIDs and interface definitions
    // -----------------------------------------------------------------------

    // SpellCheckerFactory CLSID: {7AB36653-1796-484B-BDFA-E74F1DB7C1DC}
    private static readonly Guid CLSID_SpellCheckerFactory =
        new("7AB36653-1796-484B-BDFA-E74F1DB7C1DC");

    // ISpellCheckerFactory IID: {8E018A9D-2415-4677-BF08-794EA61F94BB}
    private static readonly Guid IID_SpellCheckerFactory =
        new("8E018A9D-2415-4677-BF08-794EA61F94BB");

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        out IntPtr ppv);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint dwCoInit);

    private const uint CLSCTX_INPROC_SERVER = 1;
    private const uint COINIT_APARTMENTTHREADED = 2;

    // -----------------------------------------------------------------------
    // State
    // -----------------------------------------------------------------------

    // We delegate to a helper that wraps ISpellCheckerFactory and ISpellChecker.
    private readonly WindowsCheckerHelper? _helper;
    private bool _disposed;

    /// <inheritdoc/>
    public bool IsAvailable => _helper is { IsAvailable: true };

    // -----------------------------------------------------------------------
    // Construction
    // -----------------------------------------------------------------------

    internal WindowsSpellCheckService()
    {
        try
        {
            CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
            _helper = new WindowsCheckerHelper();
        }
        catch { /* COM not available or not on Windows 8+ */ }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _helper?.Dispose();
    }

    // -----------------------------------------------------------------------
    // ISpellCheckService
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public bool Check(string word)
    {
        if (!IsAvailable || string.IsNullOrEmpty(word))
            return true;

        try { return _helper!.Check(word); }
        catch { return true; }
    }

    /// <inheritdoc/>
    public string[] Suggest(string word, int maxSuggestions = 8)
    {
        if (!IsAvailable || string.IsNullOrEmpty(word))
            return Array.Empty<string>();

        try { return _helper!.Suggest(word, maxSuggestions); }
        catch { return Array.Empty<string>(); }
    }

    // -----------------------------------------------------------------------
    // Inner helper — wraps the COM ISpellCheckerFactory/ISpellChecker calls
    // -----------------------------------------------------------------------

    // Spell checking via ISpellChecker COM requires interacting with a v-table
    // layout that is not directly expressible as a managed interface without
    // unsafe code. The helper uses Marshal.GetDelegateForFunctionPointer to
    // call the methods at known vtable offsets.
    //
    // ISpellCheckerFactory vtable (inherits IUnknown at slots 0-2):
    //   slot 3: CreateSpellChecker(LPCWSTR languageTag, ISpellChecker** value) -> HRESULT
    //
    // ISpellChecker vtable (inherits IUnknown at slots 0-2):
    //   slot 3: get_LanguageTag(LPWSTR* value) -> HRESULT
    //   slot 4: Check(LPCWSTR text, IEnumSpellingError** value) -> HRESULT
    //   slot 5: Suggest(LPCWSTR word, IEnumString** value) -> HRESULT
    //
    // This approach avoids importing TLB references or adding packages.

    private sealed class WindowsCheckerHelper : IDisposable
    {
        private IntPtr _factory;
        private IntPtr _checker;

        public bool IsAvailable => _checker != IntPtr.Zero;

        public WindowsCheckerHelper()
        {
            var clsid = CLSID_SpellCheckerFactory;
            var iid   = IID_SpellCheckerFactory;

            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out _factory);
            if (hr != 0 || _factory == IntPtr.Zero) return;

            // Get the language tag from the current UI culture.
            string langTag = System.Globalization.CultureInfo.CurrentUICulture.IetfLanguageTag;

            // vtable slot 3: CreateSpellChecker(LPCWSTR, ISpellChecker**)
            IntPtr createFn = Marshal.ReadIntPtr(Marshal.ReadIntPtr(_factory), 3 * IntPtr.Size);
            var create = Marshal.GetDelegateForFunctionPointer<CreateSpellCheckerDelegate>(createFn);
            create(_factory, langTag, out _checker);
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateSpellCheckerDelegate(
            IntPtr pThis, [MarshalAs(UnmanagedType.LPWStr)] string languageTag, out IntPtr checker);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CheckDelegate(
            IntPtr pThis, [MarshalAs(UnmanagedType.LPWStr)] string text, out IntPtr enumError);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SuggestDelegate(
            IntPtr pThis, [MarshalAs(UnmanagedType.LPWStr)] string word, out IntPtr enumString);

        // IEnumSpellingError: slot 3 = Next(ISpellingError**)
        // ISpellingError:     slot 3 = get_StartIndex, slot 4 = get_Length, slot 5 = get_CorrectiveAction
        // Returns 0 (S_OK) from Next means there are errors.
        public bool Check(string word)
        {
            if (_checker == IntPtr.Zero) return true;

            IntPtr checkFn = Marshal.ReadIntPtr(Marshal.ReadIntPtr(_checker), 4 * IntPtr.Size);
            var checkDel   = Marshal.GetDelegateForFunctionPointer<CheckDelegate>(checkFn);

            int hr = checkDel(_checker, word, out IntPtr enumError);
            if (hr != 0 || enumError == IntPtr.Zero) return true;

            // IEnumSpellingError::Next(ISpellingError**) -> 0=S_OK (error found), 1=S_FALSE (end)
            IntPtr nextFn = Marshal.ReadIntPtr(Marshal.ReadIntPtr(enumError), 3 * IntPtr.Size);
            var nextDel   = Marshal.GetDelegateForFunctionPointer<NextErrorDelegate>(nextFn);

            int nextHr = nextDel(enumError, out IntPtr error);

            Marshal.Release(enumError);
            if (error != IntPtr.Zero) Marshal.Release(error);

            return nextHr != 0; // S_FALSE (1) means no errors = word correct
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int NextErrorDelegate(IntPtr pThis, out IntPtr error);

        public string[] Suggest(string word, int maxSuggestions)
        {
            if (_checker == IntPtr.Zero) return Array.Empty<string>();

            IntPtr suggestFn = Marshal.ReadIntPtr(Marshal.ReadIntPtr(_checker), 5 * IntPtr.Size);
            var suggestDel   = Marshal.GetDelegateForFunctionPointer<SuggestDelegate>(suggestFn);

            int hr = suggestDel(_checker, word, out IntPtr enumString);
            if (hr != 0 || enumString == IntPtr.Zero) return Array.Empty<string>();

            // IEnumString::Next(celt, rgelt, pceltFetched) -> HRESULT
            IntPtr nextFn = Marshal.ReadIntPtr(Marshal.ReadIntPtr(enumString), 3 * IntPtr.Size);
            var nextDel   = Marshal.GetDelegateForFunctionPointer<IEnumStringNextDelegate>(nextFn);

            var results = new List<string>();
            while (results.Count < maxSuggestions)
            {
                var buf = new IntPtr[1];
                int fetched = 0;
                hr = nextDel(enumString, 1, buf, ref fetched);
                if (hr != 0 || fetched == 0 || buf[0] == IntPtr.Zero) break;

                string? s = Marshal.PtrToStringUni(buf[0]);
                Marshal.FreeCoTaskMem(buf[0]);
                if (s is not null) results.Add(s);
            }

            Marshal.Release(enumString);
            return results.ToArray();
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int IEnumStringNextDelegate(
            IntPtr pThis, uint celt, [Out] IntPtr[] rgelt, ref int pceltFetched);

        public void Dispose()
        {
            if (_checker != IntPtr.Zero) { Marshal.Release(_checker); _checker = IntPtr.Zero; }
            if (_factory != IntPtr.Zero) { Marshal.Release(_factory); _factory = IntPtr.Zero; }
        }
    }
}
