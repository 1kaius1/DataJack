// SPDX-License-Identifier: GPL-3.0-or-later
// macOS spell check backend via NSSpellChecker (Foundation framework).
// Accessed through the Objective-C runtime to avoid a native wrapper dependency.
// The target API for Phase 4 is a full ObjC bridge; this Phase 3 implementation
// provides the working P/Invoke path. See ARCHITECTURE.md §6.5.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace DataJack.Platform.Spell;

/// <summary>
/// macOS spell check service using <c>NSSpellChecker</c> via the Objective-C runtime.
/// Provides word checking and suggestions through the system spell checker, which
/// respects the system language settings. Falls back to <see cref="NullSpellCheckService"/>
/// behaviour when the ObjC runtime is unavailable.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacosSpellCheckService : ISpellCheckService
{
    // -----------------------------------------------------------------------
    // ObjC runtime P/Invoke
    // -----------------------------------------------------------------------

    private const string ObjCLib     = "/usr/lib/libobjc.A.dylib";
    private const string FoundationLib = "/System/Library/Frameworks/Foundation.framework/Foundation";

    [DllImport(ObjCLib)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjCLib)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_ptr(IntPtr receiver, IntPtr selector);

    // NSString alloc + initWithBytes:length:encoding: (copies bytes; no pinning required)
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_nsstring(
        IntPtr receiver, IntPtr selector, IntPtr bytes, nint length, uint encoding);

    // checkSpelling:startingAt:language:wrap:inSpellDocumentWithTag:wordCount:
    // Returns NSRange {location, length}; length==0 means no misspelling found.
    // On arm64 (all modern Macs) objc_msgSend handles struct returns in registers.
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern NSRange objc_msgSend_nsrange(
        IntPtr receiver, IntPtr selector,
        IntPtr string_, nint startAt, IntPtr language,
        [MarshalAs(UnmanagedType.I1)] bool wrap, nint tag, IntPtr wordCount);

    // guessesForWordRange:inString:language:inSpellDocumentWithTag: -> NSArray*
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_guesses(
        IntPtr receiver, IntPtr selector,
        NSRange range, IntPtr string_, IntPtr language, nint tag);

    // NSArray count -> NSUInteger
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_count(IntPtr receiver, IntPtr selector);

    // NSArray objectAtIndex: -> id
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_objectAtIndex(IntPtr receiver, IntPtr selector, nint index);

    // NSString UTF8String -> const char*
    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_utf8string(IntPtr receiver, IntPtr selector);

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRange { public nint Location; public nint Length; }

    private const uint NSUTF8StringEncoding = 4;

    // -----------------------------------------------------------------------
    // State
    // -----------------------------------------------------------------------

    private readonly IntPtr _checker;
    private readonly IntPtr _selCheckSpelling;
    private readonly IntPtr _selGuesses;
    private readonly IntPtr _selCount;
    private readonly IntPtr _selObjectAtIndex;
    private readonly IntPtr _selUTF8String;
    private readonly IntPtr _selAlloc;
    private readonly IntPtr _selInitWithBytes;
    private bool _available;
    private bool _disposed;

    /// <inheritdoc/>
    public bool IsAvailable => _available;

    // -----------------------------------------------------------------------
    // Construction
    // -----------------------------------------------------------------------

    internal MacosSpellCheckService()
    {
        try
        {
            // Load Foundation to ensure NSSpellChecker is available.
            NativeLibrary.Load(FoundationLib);

            var cls    = objc_getClass("NSSpellChecker");
            var selShared = sel_registerName("sharedSpellChecker");
            _checker = objc_msgSend_ptr(cls, selShared);

            if (_checker == IntPtr.Zero)
                return;

            _selCheckSpelling  = sel_registerName("checkSpelling:startingAt:language:wrap:inSpellDocumentWithTag:wordCount:");
            _selGuesses        = sel_registerName("guessesForWordRange:inString:language:inSpellDocumentWithTag:");
            _selCount          = sel_registerName("count");
            _selObjectAtIndex  = sel_registerName("objectAtIndex:");
            _selUTF8String     = sel_registerName("UTF8String");
            _selAlloc          = sel_registerName("alloc");
            _selInitWithBytes  = sel_registerName("initWithBytes:length:encoding:");

            _available = true;
        }
        catch
        {
            // ObjC runtime not available or any native error — stay uninitialized.
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // NSSpellChecker is a singleton; no release needed.
    }

    // -----------------------------------------------------------------------
    // ISpellCheckService
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public bool Check(string word)
    {
        if (!_available || string.IsNullOrEmpty(word))
            return true;

        try
        {
            IntPtr nsWord = CreateNSString(word);
            if (nsWord == IntPtr.Zero) return true;

            NSRange range = objc_msgSend_nsrange(_checker, _selCheckSpelling,
                nsWord, 0, IntPtr.Zero, false, 0, IntPtr.Zero);

            return range.Length == 0; // length 0 = no misspelling found
        }
        catch { return true; }
    }

    /// <inheritdoc/>
    public string[] Suggest(string word, int maxSuggestions = 8)
    {
        if (!_available || string.IsNullOrEmpty(word))
            return Array.Empty<string>();

        try
        {
            IntPtr nsWord = CreateNSString(word);
            if (nsWord == IntPtr.Zero) return Array.Empty<string>();

            var wordRange = new NSRange { Location = 0, Length = word.Length };
            IntPtr array = objc_msgSend_guesses(_checker, _selGuesses,
                wordRange, nsWord, IntPtr.Zero, 0);

            if (array == IntPtr.Zero) return Array.Empty<string>();

            int count = (int)Math.Min(objc_msgSend_count(array, _selCount), maxSuggestions);
            var results = new string[count];
            for (int i = 0; i < count; i++)
            {
                IntPtr item    = objc_msgSend_objectAtIndex(array, _selObjectAtIndex, i);
                IntPtr utf8ptr = objc_msgSend_utf8string(item, _selUTF8String);
                results[i] = Marshal.PtrToStringUTF8(utf8ptr) ?? string.Empty;
            }

            return results;
        }
        catch { return Array.Empty<string>(); }
    }

    // Creates a temporary NSString from a managed string.
    // Uses initWithBytes:length:encoding: (copying variant) so we only need to pin
    // the byte array during the synchronous ObjC call, not for the lifetime of the object.
    private IntPtr CreateNSString(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        GCHandle pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            IntPtr cls   = objc_getClass("NSString");
            IntPtr alloc = objc_msgSend_ptr(cls, _selAlloc);
            return objc_msgSend_nsstring(
                alloc, _selInitWithBytes, pin.AddrOfPinnedObject(), bytes.Length, NSUTF8StringEncoding);
        }
        finally
        {
            pin.Free();
        }
    }
}
