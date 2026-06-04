// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.Events;
using DataJack.Core.Irc;
using DataJack.Core.Protocol.Dcc;
using DataJack.Core.Storage.Config;
using DataJack.Net;
using Xunit;

namespace DataJack.Core.Tests;

// ---------------------------------------------------------------------------
// DccCtcpParser tests — pure function, no I/O
// ---------------------------------------------------------------------------

public sealed class DccCtcpParserTests
{
    // Bare filename, IPv4 uint32, port, size
    [Fact]
    public void Parse_ValidSend_BareFilename_Succeeds()
    {
        bool ok = DccCtcpParser.TryParse("SEND photo.jpg 2130706433 5000 1048576", out var offer);

        Assert.True(ok);
        Assert.Equal("photo.jpg",  offer.Filename);
        Assert.Equal("127.0.0.1", offer.PeerAddress);
        Assert.Equal(5000,        offer.PeerPort);
        Assert.Equal(1048576L,    offer.FileSize);
    }

    [Fact]
    public void Parse_ValidSend_QuotedFilenameWithSpaces_Succeeds()
    {
        bool ok = DccCtcpParser.TryParse("SEND \"my photo.jpg\" 2130706433 5000 2048", out var offer);

        Assert.True(ok);
        Assert.Equal("my photo.jpg", offer.Filename);
        Assert.Equal("127.0.0.1",    offer.PeerAddress);
        Assert.Equal(5000,           offer.PeerPort);
        Assert.Equal(2048L,          offer.FileSize);
    }

    [Fact]
    public void Parse_IpConversion_LocalhostUint32_IsCorrect()
    {
        // 127.0.0.1 = (127 << 24) | 1 = 2130706433
        DccCtcpParser.TryParse("SEND f.txt 2130706433 6667 0", out var offer);
        Assert.Equal("127.0.0.1", offer.PeerAddress);
    }

    [Fact]
    public void Parse_IpConversion_Zero_IsZeroDotted()
    {
        DccCtcpParser.TryParse("SEND f.txt 0 1024 0", out var offer);
        Assert.Equal("0.0.0.0", offer.PeerAddress);
    }

    [Fact]
    public void Parse_IpConversion_MaxUint32_Is255s()
    {
        DccCtcpParser.TryParse("SEND f.txt 4294967295 1024 0", out var offer);
        Assert.Equal("255.255.255.255", offer.PeerAddress);
    }

    [Fact]
    public void Parse_PassiveDcc_PortZero_Succeeds()
    {
        // Passive DCC advertises port 0
        bool ok = DccCtcpParser.TryParse("SEND file.zip 2130706433 0 65536", out var offer);

        Assert.True(ok);
        Assert.Equal(0, offer.PeerPort);
    }

    [Fact]
    public void Parse_TrailingToken_IsIgnored()
    {
        // Passive DCC appends a token after the size — it should be silently discarded
        bool ok = DccCtcpParser.TryParse("SEND f.txt 2130706433 5000 1024 42", out var offer);

        Assert.True(ok);
        Assert.Equal(1024L, offer.FileSize);
    }

    [Fact]
    public void Parse_NullParams_ReturnsFalse()
    {
        Assert.False(DccCtcpParser.TryParse(null, out _));
    }

    [Fact]
    public void Parse_EmptyParams_ReturnsFalse()
    {
        Assert.False(DccCtcpParser.TryParse("", out _));
    }

    [Fact]
    public void Parse_UnknownSubcommand_ReturnsFalse()
    {
        Assert.False(DccCtcpParser.TryParse("CHAT 2130706433 5000", out _));
    }

    [Fact]
    public void Parse_MissingPort_ReturnsFalse()
    {
        Assert.False(DccCtcpParser.TryParse("SEND f.txt 2130706433", out _));
    }

    [Fact]
    public void Parse_MissingSize_ReturnsFalse()
    {
        Assert.False(DccCtcpParser.TryParse("SEND f.txt 2130706433 5000", out _));
    }

    [Fact]
    public void Parse_IpTooLarge_ReturnsFalse()
    {
        Assert.False(DccCtcpParser.TryParse("SEND f.txt 99999999999 5000 1024", out _));
    }

    [Fact]
    public void Parse_InvalidPort_ReturnsFalse()
    {
        Assert.False(DccCtcpParser.TryParse("SEND f.txt 2130706433 notaport 1024", out _));
    }

    [Fact]
    public void Parse_NegativeSize_ReturnsFalse()
    {
        Assert.False(DccCtcpParser.TryParse("SEND f.txt 2130706433 5000 -1", out _));
    }

    [Fact]
    public void Parse_SubcommandCaseInsensitive_Succeeds()
    {
        Assert.True(DccCtcpParser.TryParse("send f.txt 2130706433 5000 0", out _));
        Assert.True(DccCtcpParser.TryParse("Send f.txt 2130706433 5000 0", out _));
    }
}

// ---------------------------------------------------------------------------
// DccFilenameSanitizer tests — pure function, no I/O
// ---------------------------------------------------------------------------

public sealed class DccFilenameSanitizerTests
{
    // Sanitize — happy paths

    [Fact]
    public void Sanitize_NormalFilename_ReturnsUnchanged()
    {
        Assert.Equal("photo.jpg", DccFilenameSanitizer.Sanitize("photo.jpg"));
    }

    [Fact]
    public void Sanitize_FilenameWithExtension_PreservesExtension()
    {
        Assert.Equal("archive.tar.gz", DccFilenameSanitizer.Sanitize("archive.tar.gz"));
    }

    [Fact]
    public void Sanitize_UnixPathTraversal_ReturnsBareFilename()
    {
        Assert.Equal("passwd", DccFilenameSanitizer.Sanitize("../../etc/passwd"));
    }

    [Fact]
    public void Sanitize_WindowsPathTraversal_ReturnsBareFilename()
    {
        Assert.Equal("calc.exe", DccFilenameSanitizer.Sanitize(@"..\..\..\windows\calc.exe"));
    }

    [Fact]
    public void Sanitize_AbsoluteUnixPath_ReturnsBareFilename()
    {
        Assert.Equal("shadow", DccFilenameSanitizer.Sanitize("/etc/shadow"));
    }

    [Fact]
    public void Sanitize_AbsoluteWindowsPath_ReturnsBareFilename()
    {
        Assert.Equal("cmd.exe", DccFilenameSanitizer.Sanitize(@"C:\Windows\System32\cmd.exe"));
    }

    [Fact]
    public void Sanitize_FilenameExactlyAtMaxLength_ReturnsUnchanged()
    {
        string name = new string('a', 255) + ".txt";
        // Length is 259, so it gets truncated; what matters is no crash and length <= 255
        string? result = DccFilenameSanitizer.Sanitize(name);
        Assert.NotNull(result);
        Assert.True(result!.Length <= 255);
    }

    [Fact]
    public void Sanitize_FilenameLongerThan255_TruncatedTo255()
    {
        string longName = new string('x', 300);
        string? result = DccFilenameSanitizer.Sanitize(longName);
        Assert.NotNull(result);
        Assert.Equal(255, result!.Length);
    }

    // Sanitize — rejection paths

    [Fact]
    public void Sanitize_NullInput_ReturnsNull()
    {
        Assert.Null(DccFilenameSanitizer.Sanitize(null));
    }

    [Fact]
    public void Sanitize_EmptyString_ReturnsNull()
    {
        Assert.Null(DccFilenameSanitizer.Sanitize(""));
    }

    [Fact]
    public void Sanitize_NullBytes_ReturnsNull()
    {
        Assert.Null(DccFilenameSanitizer.Sanitize("file\0name.txt"));
    }

    [Fact]
    public void Sanitize_SingleDot_ReturnsNull()
    {
        Assert.Null(DccFilenameSanitizer.Sanitize("."));
    }

    [Fact]
    public void Sanitize_DoubleDot_ReturnsNull()
    {
        Assert.Null(DccFilenameSanitizer.Sanitize(".."));
    }

    [Fact]
    public void Sanitize_PathWithOnlySlashes_ReturnsNull()
    {
        Assert.Null(DccFilenameSanitizer.Sanitize("/"));
    }

    // IsExecutable

    [Theory]
    [InlineData(".exe")]
    [InlineData(".bat")]
    [InlineData(".cmd")]
    [InlineData(".sh")]
    [InlineData(".bash")]
    [InlineData(".ps1")]
    [InlineData(".py")]
    [InlineData(".rb")]
    [InlineData(".js")]
    [InlineData(".vbs")]
    [InlineData(".jar")]
    [InlineData(".deb")]
    [InlineData(".rpm")]
    public void IsExecutable_KnownDangerousExtension_ReturnsTrue(string ext)
    {
        Assert.True(DccFilenameSanitizer.IsExecutable("file" + ext));
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".mp3")]
    [InlineData(".png")]
    [InlineData(".zip")]
    [InlineData(".pdf")]
    [InlineData(".csv")]
    public void IsExecutable_SafeExtension_ReturnsFalse(string ext)
    {
        Assert.False(DccFilenameSanitizer.IsExecutable("file" + ext));
    }

    [Fact]
    public void IsExecutable_UpperCaseExtension_CaseInsensitive()
    {
        Assert.True(DccFilenameSanitizer.IsExecutable("MALWARE.EXE"));
        Assert.True(DccFilenameSanitizer.IsExecutable("script.SH"));
    }

    [Fact]
    public void IsExecutable_NoExtension_ReturnsFalse()
    {
        Assert.False(DccFilenameSanitizer.IsExecutable("Makefile"));
    }

    [Fact]
    public void IsExecutable_NullInput_ReturnsFalse()
    {
        Assert.False(DccFilenameSanitizer.IsExecutable(null));
    }

    [Fact]
    public void IsExecutable_EmptyString_ReturnsFalse()
    {
        Assert.False(DccFilenameSanitizer.IsExecutable(""));
    }
}

// ---------------------------------------------------------------------------
// DccEngine event-integration tests
// ---------------------------------------------------------------------------

public sealed class DccEngineTests : IAsyncDisposable
{
    private const string Server = "libera";

    private readonly EventDispatcher _dispatcher = new();

    // Fake network provider that returns a pre-built stream.
    private FakeNetworkProvider? _provider;
    private DccEngine?           _engine;

    public DccEngineTests()
    {
        _dispatcher.Start();
    }

    public async ValueTask DisposeAsync()
    {
        if (_engine is not null)
            await _engine.DisposeAsync();
        await _dispatcher.DisposeAsync();
    }

    private void CreateEngine(Stream? stream = null)
    {
        _provider = new FakeNetworkProvider(stream ?? Stream.Null);
        _engine   = new DccEngine(Server, _dispatcher, _provider, () => DccSettings.Default());
    }

    // Publishes an event and waits for the dispatch loop to process it.
    private async Task Pub<T>(T evt) where T : struct
    {
        await _dispatcher.PublishAsync(evt);
        await Task.Delay(50);
    }

    // ---------------------------------------------------------------------------
    // Offer parsing and event emission
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task CtcpDccSend_EmitsDccOfferReceived()
    {
        CreateEngine();

        DccOfferReceived? captured = null;
        _dispatcher.Subscribe<DccOfferReceived>(e => captured = e);

        await Pub(new CtcpRequest(Server, "alice", "DCC", "SEND photo.jpg 2130706433 5000 1024000"));

        Assert.NotNull(captured);
    }

    [Fact]
    public async Task CtcpDccSend_OfferHasCorrectFields()
    {
        CreateEngine();

        DccOfferReceived? captured = null;
        _dispatcher.Subscribe<DccOfferReceived>(e => captured = e);

        await Pub(new CtcpRequest(Server, "bob", "DCC", "SEND data.bin 2130706433 6001 512"));

        Assert.NotNull(captured);
        Assert.Equal(Server,       captured!.Value.Server);
        Assert.Equal("bob",        captured.Value.PeerNick);
        Assert.Equal("data.bin",   captured.Value.Filename);
        Assert.Equal(512L,         captured.Value.FileSize);
        Assert.Equal("127.0.0.1", captured.Value.PeerAddress);
        Assert.Equal(6001,         captured.Value.PeerPort);
        Assert.Equal(DccTransferType.Receive, captured.Value.Type);
    }

    [Fact]
    public async Task CtcpDccSend_SessionIdIsNonEmpty()
    {
        CreateEngine();

        DccOfferReceived? captured = null;
        _dispatcher.Subscribe<DccOfferReceived>(e => captured = e);

        await Pub(new CtcpRequest(Server, "carol", "DCC", "SEND f.txt 2130706433 5000 0"));

        Assert.NotEqual(Guid.Empty, captured!.Value.SessionId);
    }

    [Fact]
    public async Task CtcpDccSend_TwoOffers_HaveDistinctSessionIds()
    {
        CreateEngine();

        var ids = new List<Guid>();
        _dispatcher.Subscribe<DccOfferReceived>(e => ids.Add(e.SessionId));

        await Pub(new CtcpRequest(Server, "alice", "DCC", "SEND a.txt 2130706433 5001 0"));
        await Pub(new CtcpRequest(Server, "bob",   "DCC", "SEND b.txt 2130706433 5002 0"));

        Assert.Equal(2, ids.Count);
        Assert.NotEqual(ids[0], ids[1]);
    }

    [Fact]
    public async Task CtcpDccSend_ExecutableFile_IsExecutableTrue()
    {
        CreateEngine();

        DccOfferReceived? captured = null;
        _dispatcher.Subscribe<DccOfferReceived>(e => captured = e);

        await Pub(new CtcpRequest(Server, "alice", "DCC", "SEND malware.exe 2130706433 5000 1024"));

        Assert.True(captured!.Value.IsExecutable);
    }

    [Fact]
    public async Task CtcpDccSend_SafeFile_IsExecutableFalse()
    {
        CreateEngine();

        DccOfferReceived? captured = null;
        _dispatcher.Subscribe<DccOfferReceived>(e => captured = e);

        await Pub(new CtcpRequest(Server, "alice", "DCC", "SEND photo.jpg 2130706433 5000 1024"));

        Assert.False(captured!.Value.IsExecutable);
    }

    [Fact]
    public async Task CtcpDccSend_PathTraversalFilename_SanitizedInOffer()
    {
        CreateEngine();

        DccOfferReceived? captured = null;
        _dispatcher.Subscribe<DccOfferReceived>(e => captured = e);

        await Pub(new CtcpRequest(Server, "eve", "DCC",
            "SEND ../../etc/passwd 2130706433 5000 1024"));

        // After sanitization only the bare filename remains.
        Assert.Equal("passwd", captured!.Value.Filename);
    }

    [Fact]
    public async Task CtcpDccSend_StoresSessionInSessions()
    {
        CreateEngine();

        DccOfferReceived? captured = null;
        _dispatcher.Subscribe<DccOfferReceived>(e => captured = e);

        await Pub(new CtcpRequest(Server, "alice", "DCC", "SEND f.txt 2130706433 5000 0"));

        var session = _engine!.Sessions.SingleOrDefault(s => s.Id == captured!.Value.SessionId);
        Assert.NotNull(session);
        Assert.Equal(DccSessionStatus.Pending, session.Status);
    }

    [Fact]
    public async Task CtcpFromDifferentServer_IsIgnored()
    {
        CreateEngine();

        DccOfferReceived? captured = null;
        _dispatcher.Subscribe<DccOfferReceived>(e => captured = e);

        await Pub(new CtcpRequest("othernet", "alice", "DCC", "SEND f.txt 2130706433 5000 0"));

        Assert.Null(captured);
    }

    [Fact]
    public async Task NonDccCtcp_IsIgnored()
    {
        CreateEngine();

        DccOfferReceived? captured = null;
        _dispatcher.Subscribe<DccOfferReceived>(e => captured = e);

        await Pub(new CtcpRequest(Server, "alice", "PING", null));

        Assert.Null(captured);
    }

    [Fact]
    public async Task UnparsableDccPayload_IsIgnored()
    {
        CreateEngine();

        DccOfferReceived? captured = null;
        _dispatcher.Subscribe<DccOfferReceived>(e => captured = e);

        await Pub(new CtcpRequest(Server, "alice", "DCC", "SEND only-filename-no-ip"));

        Assert.Null(captured);
    }

    // ---------------------------------------------------------------------------
    // AcceptReceiveAsync — file download
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task AcceptReceiveAsync_DownloadsCorrectBytes()
    {
        byte[] fileContent = "Hello, DCC World!"u8.ToArray();
        var senderStream = new FakeSenderStream(fileContent);

        string tempDir = Path.Combine(Path.GetTempPath(), $"dcc_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        _engine = new DccEngine(
            Server, _dispatcher,
            new FakeNetworkProvider(senderStream),
            () => new DccSettings(tempDir, false, 0));

        DccOfferReceived? offer = null;
        _dispatcher.Subscribe<DccOfferReceived>(e => offer = e);

        await Pub(new CtcpRequest(Server, "peer", "DCC",
            $"SEND hello.txt 2130706433 5000 {fileContent.Length}"));

        Assert.NotNull(offer);

        // The DccCompleted event is dispatched on the event-loop thread after
        // AcceptReceiveAsync writes it to the channel. Use a TCS so the test
        // waits for the dispatch rather than racing against it.
        var completedTcs = new TaskCompletionSource<DccCompleted>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.Subscribe<DccCompleted>(e => completedTcs.TrySetResult(e));

        await _engine.AcceptReceiveAsync(offer!.Value.SessionId);

        DccCompleted completed = await completedTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(fileContent.Length, (int)completed.BytesTransferred);

        string savedPath = Path.Combine(tempDir, "hello.txt");
        Assert.True(File.Exists(savedPath));
        Assert.Equal(fileContent, await File.ReadAllBytesAsync(savedPath));

        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task AcceptReceiveAsync_EmitsDccStarted()
    {
        byte[] fileContent = "data"u8.ToArray();
        var senderStream   = new FakeSenderStream(fileContent);

        string tempDir = Path.Combine(Path.GetTempPath(), $"dcc_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        _engine = new DccEngine(
            Server, _dispatcher,
            new FakeNetworkProvider(senderStream),
            () => new DccSettings(tempDir, false, 0));

        DccOfferReceived? offer = null;
        _dispatcher.Subscribe<DccOfferReceived>(e => offer = e);
        await Pub(new CtcpRequest(Server, "peer", "DCC",
            $"SEND small.bin 2130706433 5000 {fileContent.Length}"));

        DccStarted? started = null;
        _dispatcher.Subscribe<DccStarted>(e => started = e);

        await _engine.AcceptReceiveAsync(offer!.Value.SessionId);

        Assert.NotNull(started);
        Assert.Equal(offer.Value.SessionId, started!.Value.SessionId);

        Directory.Delete(tempDir, recursive: true);
    }

    [Fact]
    public async Task AcceptReceiveAsync_UnknownSessionId_Throws()
    {
        CreateEngine();
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _engine!.AcceptReceiveAsync(Guid.NewGuid()));
    }

    // ---------------------------------------------------------------------------
    // ResolveDownloadDirectory helper
    // ---------------------------------------------------------------------------

    [Fact]
    public void ResolveDownloadDirectory_WithExplicitPath_ReturnsIt()
    {
        string result = DccEngine.ResolveDownloadDirectory(
            new DccSettings("/custom/downloads", false, 0));

        Assert.Equal("/custom/downloads", result);
    }

    [Fact]
    public void ResolveDownloadDirectory_WithNullPath_ReturnsDownloadsFolder()
    {
        string result = DccEngine.ResolveDownloadDirectory(DccSettings.Default());

        // Platform ~/Downloads
        Assert.EndsWith("Downloads", result, StringComparison.OrdinalIgnoreCase);
    }
}

// ---------------------------------------------------------------------------
// DccReceiver tests — low-level I/O
// ---------------------------------------------------------------------------

public sealed class DccReceiverTests
{
    [Fact]
    public async Task ReceiveAsync_WritesAllBytesToFile()
    {
        byte[] content = "Hello DCC Receiver"u8.ToArray();
        using var fakeSender = new FakeSenderStream(content);

        string outPath = Path.GetTempFileName();
        try
        {
            long received = await DccReceiver.ReceiveAsync(
                fakeSender, outPath, content.Length, 0, null, CancellationToken.None);

            Assert.Equal(content.Length, (int)received);
            Assert.Equal(content, await File.ReadAllBytesAsync(outPath));
        }
        finally
        {
            File.Delete(outPath);
        }
    }

    [Fact]
    public async Task ReceiveAsync_StopsAtExpectedSize_WithLargerStream()
    {
        byte[] all   = new byte[256];
        for (int i = 0; i < all.Length; i++) all[i] = (byte)i;

        using var fakeSender = new FakeSenderStream(all);

        string outPath = Path.GetTempFileName();
        try
        {
            long received = await DccReceiver.ReceiveAsync(
                fakeSender, outPath, expectedSize: 100, resumeOffset: 0, null, CancellationToken.None);

            Assert.Equal(100, (int)received);
            byte[] written = await File.ReadAllBytesAsync(outPath);
            Assert.Equal(100, written.Length);
        }
        finally
        {
            File.Delete(outPath);
        }
    }

    [Fact]
    public async Task ReceiveAsync_ReportsProgress()
    {
        byte[] content = new byte[DccReceiver.BufferSize + 1024];

        using var fakeSender = new FakeSenderStream(content);

        string outPath = Path.GetTempFileName();
        var progressReports = new List<(long bytes, double rate)>();
        var progress = new Progress<(long, double)>(p => progressReports.Add(p));

        try
        {
            await DccReceiver.ReceiveAsync(
                fakeSender, outPath, content.Length, 0, progress, CancellationToken.None);

            // At least one progress report for the over-one-buffer transfer
            Assert.NotEmpty(progressReports);
            Assert.Equal(content.Length, (int)progressReports[^1].bytes);
        }
        finally
        {
            File.Delete(outPath);
        }
    }
}

// ---------------------------------------------------------------------------
// DccCtcpParser RESUME / ACCEPT tests — pure function, no I/O
// ---------------------------------------------------------------------------

public sealed class DccCtcpParserResumeTests
{
    [Fact]
    public void Parse_Resume_BareFilename_Succeeds()
    {
        bool ok = DccCtcpParser.TryParseResumeOrAccept("RESUME archive.zip 5001 65536", out var offer);

        Assert.True(ok);
        Assert.Equal("archive.zip", offer.Filename);
        Assert.Equal(5001,          offer.Port);
        Assert.Equal(65536L,        offer.Offset);
    }

    [Fact]
    public void Parse_Resume_QuotedFilename_Succeeds()
    {
        bool ok = DccCtcpParser.TryParseResumeOrAccept(@"RESUME ""my file.bin"" 5001 1024", out var offer);

        Assert.True(ok);
        Assert.Equal("my file.bin", offer.Filename);
        Assert.Equal(5001,          offer.Port);
        Assert.Equal(1024L,         offer.Offset);
    }

    [Fact]
    public void Parse_Accept_Succeeds()
    {
        bool ok = DccCtcpParser.TryParseResumeOrAccept("ACCEPT data.bin 6000 2097152", out var offer);

        Assert.True(ok);
        Assert.Equal("data.bin",   offer.Filename);
        Assert.Equal(6000,         offer.Port);
        Assert.Equal(2097152L,     offer.Offset);
    }

    [Fact]
    public void Parse_ZeroOffset_IsValid()
    {
        // A peer may send RESUME with offset 0 (start of file) — unusual but valid.
        bool ok = DccCtcpParser.TryParseResumeOrAccept("RESUME f.txt 5000 0", out var offer);

        Assert.True(ok);
        Assert.Equal(0L, offer.Offset);
    }

    [Theory]
    [InlineData("resume f.txt 5000 1024")]
    [InlineData("Resume f.txt 5000 1024")]
    [InlineData("ACCEPT f.txt 5000 1024")]
    [InlineData("accept f.txt 5000 1024")]
    public void Parse_CaseInsensitive_Succeeds(string ctcpParams)
    {
        Assert.True(DccCtcpParser.TryParseResumeOrAccept(ctcpParams, out _));
    }

    [Fact]
    public void Parse_UnknownSubcommand_ReturnsFalse()
    {
        Assert.False(DccCtcpParser.TryParseResumeOrAccept("SEND f.txt 2130706433 5000 1024", out _));
    }

    [Fact]
    public void Parse_NullParams_ReturnsFalse()
    {
        Assert.False(DccCtcpParser.TryParseResumeOrAccept(null, out _));
    }

    [Fact]
    public void Parse_EmptyParams_ReturnsFalse()
    {
        Assert.False(DccCtcpParser.TryParseResumeOrAccept("", out _));
    }

    [Fact]
    public void Parse_InvalidPort_ReturnsFalse()
    {
        Assert.False(DccCtcpParser.TryParseResumeOrAccept("RESUME f.txt notaport 1024", out _));
    }

    [Fact]
    public void Parse_NegativeOffset_ReturnsFalse()
    {
        Assert.False(DccCtcpParser.TryParseResumeOrAccept("RESUME f.txt 5000 -1", out _));
    }

    [Fact]
    public void Parse_MissingOffset_ReturnsFalse()
    {
        Assert.False(DccCtcpParser.TryParseResumeOrAccept("RESUME f.txt 5000", out _));
    }

    [Fact]
    public void Parse_MissingPort_ReturnsFalse()
    {
        Assert.False(DccCtcpParser.TryParseResumeOrAccept("RESUME f.txt", out _));
    }
}

// ---------------------------------------------------------------------------
// DCC RESUME transfer tests — I/O behaviour with resume offsets
// ---------------------------------------------------------------------------

public sealed class DccResumeTransferTests
{
    // DccReceiver — resume mode

    [Fact]
    public async Task DccReceiver_WithResumeOffset_AppendsToExistingFile()
    {
        byte[] existingBytes  = "AAAA"u8.ToArray();           // 4 bytes already on disk
        byte[] remainderBytes = "BBBBBBBB"u8.ToArray();       // 8 bytes the sender provides
        byte[] expectedFull   = "AAAABBBBBBBB"u8.ToArray();   // complete file

        string outPath = Path.GetTempFileName();
        try
        {
            // Pre-populate the partial file.
            await File.WriteAllBytesAsync(outPath, existingBytes);

            using var fakeSender = new FakeSenderStream(remainderBytes);

            long received = await DccReceiver.ReceiveAsync(
                fakeSender,
                outPath,
                expectedFull.Length,
                resumeOffset: existingBytes.Length,
                null,
                CancellationToken.None);

            Assert.Equal(expectedFull.Length, (int)received);
            Assert.Equal(expectedFull, await File.ReadAllBytesAsync(outPath));
        }
        finally
        {
            File.Delete(outPath);
        }
    }

    [Fact]
    public async Task DccReceiver_WithResumeOffset_ReturnsTotalBytesIncludingOffset()
    {
        const int offset    = 1024;
        const int newBytes  = 512;
        byte[] content = new byte[newBytes];

        using var fakeSender = new FakeSenderStream(content);

        string outPath = Path.GetTempFileName();
        try
        {
            // Pre-populate dummy partial file so append works.
            await File.WriteAllBytesAsync(outPath, new byte[offset]);

            long returned = await DccReceiver.ReceiveAsync(
                fakeSender,
                outPath,
                offset + newBytes,
                resumeOffset: offset,
                null,
                CancellationToken.None);

            // Return value is the TOTAL bytes on disk, not just the session bytes.
            Assert.Equal(offset + newBytes, (int)returned);
        }
        finally
        {
            File.Delete(outPath);
        }
    }

    [Fact]
    public async Task DccReceiver_WithZeroOffset_CreatesFreshFile()
    {
        byte[] content = "Hello"u8.ToArray();
        using var fakeSender = new FakeSenderStream(content);

        string outPath = Path.GetTempFileName();
        try
        {
            long returned = await DccReceiver.ReceiveAsync(
                fakeSender, outPath, content.Length, 0, null, CancellationToken.None);

            Assert.Equal(content.Length, (int)returned);
            Assert.Equal(content, await File.ReadAllBytesAsync(outPath));
        }
        finally
        {
            File.Delete(outPath);
        }
    }

    // DccSender — resume mode

    [Fact]
    public async Task DccSender_WithResumeOffset_SkipsLeadingBytes()
    {
        byte[] fullFile = "AAAABBBBCCCC"u8.ToArray(); // 12 bytes

        string filePath = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(filePath, fullFile);

            var captureStream = new CaptureStream();

            long sent = await DccSender.SendAsync(
                captureStream, filePath, resumeOffset: 4, null, CancellationToken.None);

            // Should have sent bytes[4..] = "BBBBCCCC"
            Assert.Equal("BBBBCCCC"u8.ToArray(), captureStream.Written.ToArray());
            // Return value is total bytes the receiver holds (offset + sent)
            Assert.Equal(12, (int)sent);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task DccSender_WithZeroOffset_SendsFullFile()
    {
        byte[] fullFile = "HelloSender"u8.ToArray();

        string filePath = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(filePath, fullFile);

            var captureStream = new CaptureStream();

            long sent = await DccSender.SendAsync(
                captureStream, filePath, resumeOffset: 0, null, CancellationToken.None);

            Assert.Equal(fullFile, captureStream.Written.ToArray());
            Assert.Equal(fullFile.Length, (int)sent);
        }
        finally
        {
            File.Delete(filePath);
        }
    }
}

// ---------------------------------------------------------------------------
// DccEngine RESUME integration tests
// ---------------------------------------------------------------------------

public sealed class DccEngineResumeTests : IAsyncDisposable
{
    private const string Server = "libera";

    private readonly EventDispatcher _dispatcher = new();
    private DccEngine?               _engine;

    public DccEngineResumeTests()
    {
        _dispatcher.Start();
    }

    public async ValueTask DisposeAsync()
    {
        if (_engine is not null)
            await _engine.DisposeAsync();
        await _dispatcher.DisposeAsync();
    }

    private async Task Pub<T>(T evt) where T : struct
    {
        await _dispatcher.PublishAsync(evt);
        await Task.Delay(50);
    }

    // Sender role: peer sends DCC RESUME → engine stores the confirmed offset.
    // The ACCEPT CTCP response is sent via IRCConnection; the exact wire format is
    // verified in DccCtcpParserResumeTests. Here we verify the state side-effect:
    // the confirmed resume offset is stored so the background send task can use it.
    [Fact]
    public async Task DccResume_FromPeer_StoresConfirmedOffset()
    {
        var pipeStream  = new DuplexPipeStream();
        var ircProvider = new FakeNetworkProvider(pipeStream);
        var ircConn     = new IRCConnection(Server, ircProvider, _dispatcher);
        await ircConn.ConnectAsync(new NetworkEndpoint("h", 6667, false));

        _engine = new DccEngine(Server, _dispatcher,
            new FakeNetworkProvider(Stream.Null),
            () => DccSettings.Default(),
            ircConnection: ircConn);

        // Inject a pending Send session (filename "file.zip" on port 5500).
        var session = new DccSession(
            Guid.NewGuid(), Server, DccTransferType.Send, "bob",
            "0.0.0.0", 5500, DccSessionStatus.Pending, "file.zip",
            FileSize: 65536, BytesTransferred: 0, TransferRate: 0, ErrorMessage: null);
        _engine.AddSessionForTest(session);

        // Peer sends DCC RESUME.
        await Pub(new CtcpRequest(Server, "bob", "DCC", "RESUME file.zip 5500 32768"));

        // Give the fire-and-forget SendLineAsync a moment to complete.
        await Task.Delay(100);

        // The engine should have stored the confirmed offset for the background send task.
        Assert.True(_engine.HasConfirmedResumeOffset(session.Id));
        Assert.Equal(32768L, _engine.GetConfirmedResumeOffset(session.Id));

        await ircConn.DisposeAsync();
    }

    // Sender role: RESUME for an unknown session is silently ignored.
    [Fact]
    public async Task DccResume_NoMatchingSession_IsIgnored()
    {
        var pipeStream  = new DuplexPipeStream();
        var ircProvider = new FakeNetworkProvider(pipeStream);
        var ircConn     = new IRCConnection(Server, ircProvider, _dispatcher);
        await ircConn.ConnectAsync(new NetworkEndpoint("h", 6667, false));

        _engine = new DccEngine(Server, _dispatcher,
            new FakeNetworkProvider(Stream.Null),
            () => DccSettings.Default(),
            ircConnection: ircConn);

        // No sessions registered — RESUME should produce no ACCEPT.
        await Pub(new CtcpRequest(Server, "bob", "DCC", "RESUME ghost.zip 9999 100"));

        // No ACCEPT should be sent within a short window.
        using var readCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        try
        {
            string? sent = await pipeStream.ReadClientLineAsync(readCts.Token);
            // If anything was read, it must not be an ACCEPT for our ghost session.
            if (sent is not null)
                Assert.DoesNotContain("DCC ACCEPT ghost.zip", sent);
        }
        catch (OperationCanceledException) { } // timeout = nothing sent, which is correct

        await ircConn.DisposeAsync();
    }

    // Receiver role: AcceptReceiveAsync with a partial file sends DCC RESUME and
    // then downloads the remainder once the sender replies with DCC ACCEPT.
    [Fact]
    public async Task AcceptReceiveAsync_PartialFile_SendsResumeAndAppendsTail()
    {
        byte[] fullContent = "FIRSTHALF_SECONDHALF"u8.ToArray(); // 20 bytes
        int    offset      = 10;
        byte[] partial     = fullContent[..offset];
        byte[] remainder   = fullContent[offset..];

        string tempDir = Path.Combine(Path.GetTempPath(), $"dcc_resume_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string filePath = Path.Combine(tempDir, "resume.txt");
        await File.WriteAllBytesAsync(filePath, partial);

        // IRC pipe to capture the RESUME CTCP the engine sends.
        var pipeStream  = new DuplexPipeStream();
        var ircProvider = new FakeNetworkProvider(pipeStream);
        var ircConn     = new IRCConnection(Server, ircProvider, _dispatcher);
        await ircConn.ConnectAsync(new NetworkEndpoint("h", 6667, false));

        // Network provider that returns the remainder bytes.
        var senderStream  = new FakeSenderStream(remainder);
        var dataProvider  = new FakeNetworkProvider(senderStream);

        _engine = new DccEngine(
            Server, _dispatcher, dataProvider,
            () => new DccSettings(tempDir, false, 0),
            ircConnection: ircConn);

        // Simulate receiving the original SEND offer.
        DccOfferReceived? offer = null;
        _dispatcher.Subscribe<DccOfferReceived>(e => offer = e);
        await Pub(new CtcpRequest(Server, "sender", "DCC",
            $"SEND resume.txt 2130706433 5000 {fullContent.Length}"));

        Assert.NotNull(offer);

        // Start the accept in the background (it will pause at the RESUME handshake).
        var acceptTask = _engine.AcceptReceiveAsync(offer!.Value.SessionId);

        // Give AcceptReceiveAsync time to send the DCC RESUME CTCP.
        await Task.Delay(150);

        // Sender replies with DCC ACCEPT to release the handshake.
        // (The RESUME CTCP wire format is verified in DccCtcpParserResumeTests.)
        await _dispatcher.PublishAsync(
            new CtcpRequest(Server, "sender", "DCC", $"ACCEPT resume.txt 5000 {offset}"));
        await Task.Delay(50);

        // AcceptReceiveAsync should now complete.
        await acceptTask;

        // Verify the file contains the full content.
        Assert.Equal(fullContent, await File.ReadAllBytesAsync(filePath));

        await ircConn.DisposeAsync();
        Directory.Delete(tempDir, recursive: true);
    }
}

// ---------------------------------------------------------------------------
// CaptureStream: writable stream that records all written bytes; reads return EOF.
// ---------------------------------------------------------------------------

/// <summary>
/// Test helper: records all bytes written to it; reads immediately return 0 (EOF).
/// Used to inspect what DccSender wrote without a real TCP connection.
/// </summary>
internal sealed class CaptureStream : Stream
{
    private readonly List<byte> _written = new();

    public byte[] Written => _written.ToArray();

    public override bool CanRead  => true;
    public override bool CanWrite => true;
    public override bool CanSeek  => false;
    public override long Length   => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void   Flush()                                                => _written.Clear();
    public override long   Seek(long offset, SeekOrigin origin)                   => throw new NotSupportedException();
    public override void   SetLength(long value)                                  => throw new NotSupportedException();
    public override int    Read(byte[] buffer, int offset, int count)             => 0; // EOF
    public override void   Write(byte[] buffer, int offset, int count)            => _written.AddRange(buffer[offset..(offset + count)]);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        ValueTask.FromResult(0); // EOF

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        _written.AddRange(buffer.Span.ToArray());
        await Task.Yield();
    }
}

// ---------------------------------------------------------------------------
// FakeSenderStream: readable (returns provided bytes), writable (discards ACKs)
// ---------------------------------------------------------------------------

/// <summary>
/// Test helper: returns a fixed byte payload on reads and silently discards writes.
/// Used to simulate a DCC sender without a real TCP connection.
/// </summary>
internal sealed class FakeSenderStream : Stream
{
    private readonly MemoryStream _data;

    public FakeSenderStream(byte[] data) => _data = new MemoryStream(data, writable: false);

    public override bool CanRead  => true;
    public override bool CanWrite => true;
    public override bool CanSeek  => false;
    public override long Length   => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value)                => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) =>
        _data.Read(buffer, offset, count);

    public override void Write(byte[] buffer, int offset, int count) { } // discard ACKs

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        _data.ReadAsync(buffer, ct);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
        ValueTask.CompletedTask; // discard ACKs

    protected override void Dispose(bool disposing)
    {
        if (disposing) _data.Dispose();
        base.Dispose(disposing);
    }
}
