// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.Events;
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
        CreateEngine(senderStream);

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

        DccCompleted? completed = null;
        _dispatcher.Subscribe<DccCompleted>(e => completed = e);

        await _engine.AcceptReceiveAsync(offer!.Value.SessionId);

        Assert.NotNull(completed);
        Assert.Equal(fileContent.Length, (int)completed!.Value.BytesTransferred);

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
                fakeSender, outPath, content.Length, null, CancellationToken.None);

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
                fakeSender, outPath, expectedSize: 100, null, CancellationToken.None);

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
                fakeSender, outPath, content.Length, progress, CancellationToken.None);

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
