// SPDX-License-Identifier: GPL-3.0-or-later
// DCC session management: incoming offer parsing, outbound offers, and session lifecycle.
// File transfer I/O is in Transfer.cs. DCC CHAT is Phase 4 (Chat.cs). NAT traversal
// and passive/reverse DCC are Phase 4 (Nat.cs). See ARCHITECTURE.md §11.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using DataJack.Core.Events;
using DataJack.Core.Irc;
using DataJack.Core.Storage.Config;
using DataJack.Net;

namespace DataJack.Core.Protocol.Dcc;

// ---------------------------------------------------------------------------
// Session snapshot type
// ---------------------------------------------------------------------------

/// <summary>
/// Immutable snapshot of one DCC file transfer session.
/// Use <c>with</c> expressions to derive updated copies.
/// </summary>
public sealed record DccSession(
    Guid            Id,
    string          Server,
    DccTransferType Type,
    string          PeerNick,
    string          PeerAddress,
    int             PeerPort,
    DccSessionStatus Status,
    string?         Filename,
    long?           FileSize,
    long            BytesTransferred,
    double          TransferRate,
    string?         ErrorMessage);

// ---------------------------------------------------------------------------
// Filename sanitizer (public — used by UI for pre-accept display)
// ---------------------------------------------------------------------------

/// <summary>
/// Sanitizes and validates filenames received in DCC SEND offers.
/// All methods are stateless and thread-safe.
/// </summary>
public static class DccFilenameSanitizer
{
    private const int MaxFilenameLength = 255;

    // Extensions whose presence triggers an additional executable-file warning.
    // Kept as a static field so the HashSet is allocated once.
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".com", ".msi", ".msp",
        ".sh", ".bash", ".zsh", ".fish", ".csh",
        ".ps1", ".psm1", ".psd1",
        ".py", ".pyw",
        ".rb",
        ".pl",
        ".lua",
        ".js", ".mjs", ".cjs",
        ".vbs", ".vbe", ".wsh", ".wsf",
        ".jar",
        ".app", ".dmg",
        ".deb", ".rpm",
        ".run",
        ".elf",
    };

    /// <summary>
    /// Sanitizes a DCC filename for safe use as a local file path.
    /// Returns the safe bare filename, or <see langword="null"/> when the name must be
    /// rejected entirely (null bytes, empty after stripping, or path-separator-only).
    ///
    /// Path traversal sequences (<c>../</c>, <c>..\</c>) and any directory components
    /// are neutralized by keeping only the last path element via <see cref="Path.GetFileName"/>.
    /// The result is capped at 255 characters.
    /// </summary>
    public static string? Sanitize(string? filename)
    {
        if (string.IsNullOrEmpty(filename))
            return null;

        // Null bytes cause path manipulation vulnerabilities on some systems.
        if (filename.IndexOf('\0') >= 0)
            return null;

        // Normalize Windows-style backslashes to forward slashes before calling
        // Path.GetFileName. On Linux, Path.GetFileName only recognizes '/' as a
        // separator, so without this step, Windows-path filenames (e.g. "..\..\..\etc\passwd")
        // would pass through unsanitized.
        string normalized = filename.Replace('\\', '/');
        string bare = Path.GetFileName(normalized);

        if (string.IsNullOrEmpty(bare) || bare == "." || bare == "..")
            return null;

        if (bare.Length > MaxFilenameLength)
            bare = bare[..MaxFilenameLength];

        return bare;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="filename"/> has a file extension
    /// commonly associated with executable code or scripts. Check is case-insensitive.
    /// A <see langword="true"/> result should trigger an additional confirmation prompt.
    /// </summary>
    public static bool IsExecutable(string? filename)
    {
        if (string.IsNullOrEmpty(filename))
            return false;

        string ext = Path.GetExtension(filename);
        return !string.IsNullOrEmpty(ext) && ExecutableExtensions.Contains(ext);
    }
}

// ---------------------------------------------------------------------------
// CTCP DCC message parser (internal; exposed via InternalsVisibleTo for tests)
// ---------------------------------------------------------------------------

/// <summary>Parsed parameter data from a DCC SEND CTCP offer.</summary>
internal readonly record struct DccOffer(
    string Filename,
    string PeerAddress,
    int    PeerPort,
    long   FileSize);

/// <summary>
/// Parsed parameter data from a DCC RESUME or DCC ACCEPT CTCP message.
/// Both messages share the same three-field structure: filename, port, offset.
/// </summary>
internal readonly record struct DccResumeOffer(
    string Filename,
    int    Port,
    long   Offset);

/// <summary>
/// Parses the parameter string of a DCC CTCP message.
/// Phase 3 handles SEND, RESUME, and ACCEPT. CHAT is Phase 4.
/// </summary>
internal static class DccCtcpParser
{
    /// <summary>
    /// Attempts to parse a DCC SEND offer from the CTCP params string.
    /// The params string is the content of <see cref="CtcpRequest.Params"/> when
    /// <see cref="CtcpRequest.Command"/> is <c>"DCC"</c>.
    ///
    /// Supported format:
    /// <code>SEND filename ip_uint32 port size [token]</code>
    /// <code>SEND "quoted filename" ip_uint32 port size [token]</code>
    ///
    /// The optional trailing <c>token</c> is used in passive DCC; it is parsed and
    /// discarded — passive DCC handshake is Phase 4.
    /// </summary>
    internal static bool TryParse(string? ctcpParams, out DccOffer offer)
    {
        offer = default;
        if (string.IsNullOrEmpty(ctcpParams))
            return false;

        var span = ctcpParams.AsSpan().TrimStart();

        // Read the DCC subcommand.
        int spaceIdx = span.IndexOf(' ');
        if (spaceIdx < 0)
            return false;

        var subcommand = span[..spaceIdx];
        span = span[(spaceIdx + 1)..].TrimStart();

        if (!subcommand.Equals("SEND", StringComparison.OrdinalIgnoreCase))
            return false;

        // Parse filename — quoted (may contain spaces) or bare.
        string filename;
        if (!span.IsEmpty && span[0] == '"')
        {
            int closeQuote = span[1..].IndexOf('"');
            if (closeQuote < 0)
                return false;
            filename = span[1..(closeQuote + 1)].ToString();
            span = span[(closeQuote + 2)..].TrimStart();
        }
        else
        {
            int nameEnd = span.IndexOf(' ');
            if (nameEnd < 0)
                return false;
            filename = span[..nameEnd].ToString();
            span = span[(nameEnd + 1)..].TrimStart();
        }

        // Parse IP address as a decimal uint32 in network byte order (big-endian, MSB first).
        int ipEnd = span.IndexOf(' ');
        if (ipEnd < 0)
            return false;

        if (!ulong.TryParse(span[..ipEnd], out ulong ipUlong) || ipUlong > uint.MaxValue)
            return false;
        span = span[(ipEnd + 1)..].TrimStart();

        var ipBytes = new byte[4]
        {
            (byte)(ipUlong >> 24 & 0xFF),
            (byte)(ipUlong >> 16 & 0xFF),
            (byte)(ipUlong >>  8 & 0xFF),
            (byte)(ipUlong       & 0xFF),
        };
        string peerAddress = new IPAddress(ipBytes).ToString();

        // Parse port.
        int portEnd = span.IndexOf(' ');
        if (portEnd < 0)
            return false;

        if (!int.TryParse(span[..portEnd], out int port) || port < 0 || port > 65535)
            return false;
        span = span[(portEnd + 1)..].TrimStart();

        // Parse file size. A trailing passive-DCC token after the size is ignored.
        var sizeSpan = span;
        int sizeEnd = span.IndexOf(' ');
        if (sizeEnd >= 0)
            sizeSpan = span[..sizeEnd];

        if (!long.TryParse(sizeSpan, out long fileSize) || fileSize < 0)
            return false;

        offer = new DccOffer(filename, peerAddress, port, fileSize);
        return true;
    }

    /// <summary>
    /// Attempts to parse a DCC RESUME or DCC ACCEPT CTCP message.
    /// Both share the same structure: <c>RESUME|ACCEPT filename port offset</c>
    /// (quoted filenames are also supported). The caller is expected to have already
    /// determined whether this is RESUME or ACCEPT by inspecting the first token.
    /// Returns <see langword="false"/> for null/empty params, invalid values, or any
    /// subcommand other than RESUME/ACCEPT.
    /// </summary>
    internal static bool TryParseResumeOrAccept(string? ctcpParams, out DccResumeOffer offer)
    {
        offer = default;
        if (string.IsNullOrEmpty(ctcpParams))
            return false;

        var span = ctcpParams.AsSpan().TrimStart();

        // Read and validate the subcommand.
        int spaceIdx = span.IndexOf(' ');
        if (spaceIdx < 0)
            return false;

        var subcommand = span[..spaceIdx];
        if (!subcommand.Equals("RESUME", StringComparison.OrdinalIgnoreCase) &&
            !subcommand.Equals("ACCEPT", StringComparison.OrdinalIgnoreCase))
            return false;

        span = span[(spaceIdx + 1)..].TrimStart();

        // Parse filename — quoted or bare.
        string filename;
        if (!span.IsEmpty && span[0] == '"')
        {
            int closeQuote = span[1..].IndexOf('"');
            if (closeQuote < 0)
                return false;
            filename = span[1..(closeQuote + 1)].ToString();
            span = span[(closeQuote + 2)..].TrimStart();
        }
        else
        {
            int nameEnd = span.IndexOf(' ');
            if (nameEnd < 0)
                return false;
            filename = span[..nameEnd].ToString();
            span = span[(nameEnd + 1)..].TrimStart();
        }

        // Parse port.
        int portEnd = span.IndexOf(' ');
        if (portEnd < 0)
            return false;

        if (!int.TryParse(span[..portEnd], out int port) || port < 0 || port > 65535)
            return false;
        span = span[(portEnd + 1)..].TrimStart();

        // Parse offset (remaining span; no trailing fields in the RESUME/ACCEPT format).
        if (!long.TryParse(span, out long offset) || offset < 0)
            return false;

        offer = new DccResumeOffer(filename, port, offset);
        return true;
    }
}

// ---------------------------------------------------------------------------
// DCC engine
// ---------------------------------------------------------------------------

/// <summary>
/// Manages DCC file transfer sessions for one IRC server.
///
/// Subscribes to <see cref="CtcpRequest"/> events to detect incoming DCC SEND offers,
/// sanitizes filenames, and emits <see cref="DccOfferReceived"/> for the UI to present
/// to the user. The UI then calls <see cref="AcceptReceiveAsync"/> to begin downloading,
/// or <see cref="InitiateSendAsync"/> to offer a file to a peer.
///
/// One instance per server connection. See ARCHITECTURE.md §11.
/// </summary>
public sealed class DccEngine : IAsyncDisposable
{
    private readonly string            _serverId;
    private readonly EventDispatcher   _dispatcher;
    private readonly INetworkProvider  _networkProvider;
    private readonly Func<DccSettings> _settingsGetter;
    // Used to send DCC RESUME (receiver role) and DCC ACCEPT (sender role) over IRC.
    private readonly IRCConnection?    _ircConnection;
    // Overridable in tests to avoid binding real OS ports.
    private readonly Func<int, TcpListener>? _listenerFactory;

    private readonly ConcurrentDictionary<Guid, DccSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeCts = new();
    // RESUME correlation (receiver role): (filename, port) → TCS completed with confirmed offset.
    private readonly ConcurrentDictionary<(string, int), TaskCompletionSource<long>> _pendingResumes = new();
    // Confirmed resume offsets (sender role): sessionId → offset the peer requested.
    // Consumed once by the background send task when the peer's TCP connection arrives.
    private readonly ConcurrentDictionary<Guid, long> _confirmedResumeOffsets = new();
    private bool _disposed;

    // The synchronous event handler reference is kept so it can be passed to Unsubscribe.
    private readonly Action<CtcpRequest> _ctcpHandler;

    /// <summary>Point-in-time snapshot of all tracked DCC sessions.</summary>
    public DccSession[] Sessions => _sessions.Values.ToArray();

    /// <param name="serverId">Server identifier; events for other servers are ignored.</param>
    /// <param name="dispatcher">Application event bus.</param>
    /// <param name="networkProvider">Used to open outbound TCP connections (receive path).</param>
    /// <param name="settingsGetter">
    /// Delegate invoked on each transfer to retrieve current DCC configuration, allowing
    /// config changes to take effect without restarting the engine.
    /// </param>
    /// <param name="ircConnection">
    /// Optional IRC connection used to send DCC RESUME (when we are the receiver) and
    /// DCC ACCEPT (when we are the sender). When <see langword="null"/> the resume
    /// handshake is skipped and transfers always restart from the beginning.
    /// </param>
    /// <param name="listenerFactory">
    /// Optional: returns a <see cref="TcpListener"/> bound to the requested local port.
    /// Pass <see langword="null"/> (default) to use <see cref="TcpListener"/> directly.
    /// Inject a fake in unit tests to avoid real OS port binding.
    /// </param>
    public DccEngine(
        string             serverId,
        EventDispatcher    dispatcher,
        INetworkProvider   networkProvider,
        Func<DccSettings>  settingsGetter,
        IRCConnection?     ircConnection   = null,
        Func<int, TcpListener>? listenerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(serverId);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(networkProvider);
        ArgumentNullException.ThrowIfNull(settingsGetter);

        _serverId        = serverId;
        _dispatcher      = dispatcher;
        _networkProvider = networkProvider;
        _settingsGetter  = settingsGetter;
        _ircConnection   = ircConnection;
        _listenerFactory = listenerFactory;
        _ctcpHandler     = OnCtcpRequest;

        dispatcher.Subscribe<CtcpRequest>(_ctcpHandler);
    }

    // ---------------------------------------------------------------------------
    // Test helper — allows tests to inject pre-built sessions without a real handshake.
    // ---------------------------------------------------------------------------

    /// <summary>Adds a session directly into the session table. For unit tests only.</summary>
    internal void AddSessionForTest(DccSession session) => _sessions[session.Id] = session;

    /// <summary>Returns true when a confirmed resume offset exists for the given session. For unit tests only.</summary>
    internal bool HasConfirmedResumeOffset(Guid sessionId) => _confirmedResumeOffsets.ContainsKey(sessionId);

    /// <summary>Returns the confirmed resume offset for the given session, or 0 if absent. For unit tests only.</summary>
    internal long GetConfirmedResumeOffset(Guid sessionId) =>
        _confirmedResumeOffsets.TryGetValue(sessionId, out long v) ? v : 0;

    // ---------------------------------------------------------------------------
    // Incoming CTCP dispatch — runs on the event dispatch thread (synchronous)
    // ---------------------------------------------------------------------------

    private void OnCtcpRequest(CtcpRequest req)
    {
        if (req.Server != _serverId)
            return;

        if (!req.Command.Equals("DCC", StringComparison.OrdinalIgnoreCase))
            return;

        // Peek at the DCC subcommand to route to the right handler.
        var paramSpan = (req.Params ?? string.Empty).AsSpan().TrimStart();
        int firstSpace = paramSpan.IndexOf(' ');
        if (firstSpace < 0)
            return;

        var sub = paramSpan[..firstSpace];

        if (sub.Equals("SEND", StringComparison.OrdinalIgnoreCase))
        {
            if (DccCtcpParser.TryParse(req.Params, out DccOffer offer))
                HandleSendOffer(req, offer);
        }
        else if (sub.Equals("RESUME", StringComparison.OrdinalIgnoreCase))
        {
            // Peer wants to resume a file we are sending (we are in the sender role).
            if (DccCtcpParser.TryParseResumeOrAccept(req.Params, out DccResumeOffer resumeOffer))
                HandleResumeFromPeer(req.FromNick, resumeOffer);
        }
        else if (sub.Equals("ACCEPT", StringComparison.OrdinalIgnoreCase))
        {
            // Sender confirmed our RESUME request (we are in the receiver role).
            if (DccCtcpParser.TryParseResumeOrAccept(req.Params, out DccResumeOffer acceptOffer))
                HandleAcceptFromPeer(acceptOffer);
        }
    }

    private void HandleSendOffer(CtcpRequest req, DccOffer offer)
    {
        string? safeFilename = DccFilenameSanitizer.Sanitize(offer.Filename);
        bool isExec = safeFilename is not null && DccFilenameSanitizer.IsExecutable(safeFilename);

        var session = new DccSession(
            Id:               Guid.NewGuid(),
            Server:           _serverId,
            Type:             DccTransferType.Receive,
            PeerNick:         req.FromNick,
            PeerAddress:      offer.PeerAddress,
            PeerPort:         offer.PeerPort,
            Status:           DccSessionStatus.Pending,
            Filename:         safeFilename,
            FileSize:         offer.FileSize,
            BytesTransferred: 0,
            TransferRate:     0,
            ErrorMessage:     null);

        _sessions[session.Id] = session;

        _ = _dispatcher.PublishAsync(new DccOfferReceived(
            Server:       _serverId,
            SessionId:    session.Id,
            PeerNick:     req.FromNick,
            Type:         DccTransferType.Receive,
            Filename:     safeFilename,
            FileSize:     offer.FileSize,
            PeerAddress:  offer.PeerAddress,
            PeerPort:     offer.PeerPort,
            IsExecutable: isExec)).AsTask()
            .ContinueWith(
                static t => { /* publishing errors are silently swallowed */ },
                TaskContinuationOptions.OnlyOnFaulted);
    }

    // Sender role: peer sent DCC RESUME → reply with DCC ACCEPT and store the offset.
    private void HandleResumeFromPeer(string fromNick, DccResumeOffer offer)
    {
        if (_ircConnection is null)
            return;

        // Find a Pending Send session that matches the filename and port.
        DccSession? match = null;
        foreach (var s in _sessions.Values)
        {
            if (s.Type == DccTransferType.Send &&
                s.Status == DccSessionStatus.Pending &&
                string.Equals(s.Filename, offer.Filename, StringComparison.OrdinalIgnoreCase) &&
                s.PeerPort == offer.Port)
            {
                match = s;
                break;
            }
        }

        if (match is null)
            return;

        // Store the confirmed offset; the background send task will consume it.
        _confirmedResumeOffsets[match.Id] = offer.Offset;

        // Send DCC ACCEPT to the receiver.
        string acceptCtcp = $"\x01DCC ACCEPT {offer.Filename} {offer.Port} {offer.Offset}\x01";
        _ = _ircConnection.SendLineAsync($"PRIVMSG {fromNick} :{acceptCtcp}")
            .ContinueWith(
                static t => { /* send errors swallowed; IRC connection handles reconnect */ },
                TaskContinuationOptions.OnlyOnFaulted);
    }

    // Receiver role: sender replied with DCC ACCEPT → complete the pending TCS.
    private void HandleAcceptFromPeer(DccResumeOffer offer)
    {
        var key = (offer.Filename, offer.Port);
        if (_pendingResumes.TryRemove(key, out var tcs))
            tcs.TrySetResult(offer.Offset);
    }

    // ---------------------------------------------------------------------------
    // Accept an incoming file offer (UI calls this after the user confirms)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Accepts a pending incoming DCC SEND offer and begins downloading the file to the
    /// configured download directory. The file is saved as <c>{DownloadDirectory}/{Filename}</c>.
    ///
    /// Emits <see cref="DccStarted"/>, periodic <see cref="DccProgress"/>, and
    /// <see cref="DccCompleted"/> on success or <see cref="DccFailed"/> on error.
    /// </summary>
    /// <param name="sessionId">Session ID from the <see cref="DccOfferReceived"/> event.</param>
    /// <param name="ct">Cancels the transfer; emits <see cref="DccFailed"/> with reason "cancelled".</param>
    /// <exception cref="KeyNotFoundException">No session with <paramref name="sessionId"/> exists.</exception>
    /// <exception cref="InvalidOperationException">Session is not Pending or is not a Receive session.</exception>
    public async Task AcceptReceiveAsync(Guid sessionId, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new KeyNotFoundException($"DCC session {sessionId} not found.");

        if (session.Type != DccTransferType.Receive)
            throw new InvalidOperationException($"Session {sessionId} is not a Receive session.");

        if (session.Status != DccSessionStatus.Pending)
            throw new InvalidOperationException($"Session {sessionId} is not in Pending status.");

        if (string.IsNullOrEmpty(session.Filename))
        {
            await FailSessionAsync(sessionId, "Filename was rejected during sanitization.").ConfigureAwait(false);
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _activeCts[sessionId] = cts;

        _sessions[sessionId] = session with { Status = DccSessionStatus.Active };
        await _dispatcher.PublishAsync(new DccStarted(_serverId, sessionId)).ConfigureAwait(false);

        try
        {
            var settings = _settingsGetter();
            string downloadDir = ResolveDownloadDirectory(settings);
            Directory.CreateDirectory(downloadDir);
            string outputPath = Path.Combine(downloadDir, session.Filename);

            // --- DCC RESUME handshake ---
            // If a partial file already exists and is smaller than the total, attempt
            // to resume from where it left off rather than restarting from the beginning.
            long resumeOffset = 0;
            if (_ircConnection is not null && File.Exists(outputPath))
            {
                var existing = new FileInfo(outputPath);
                long partialSize = existing.Length;
                long totalSize   = session.FileSize ?? 0;
                if (partialSize > 0 && partialSize < totalSize)
                {
                    // Register the correlation entry so HandleAcceptFromPeer can complete it.
                    var tcs = new TaskCompletionSource<long>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _pendingResumes[(session.Filename, session.PeerPort)] = tcs;

                    string resumeCtcp = $"\x01DCC RESUME {session.Filename} {session.PeerPort} {partialSize}\x01";
                    await _ircConnection
                        .SendLineAsync($"PRIVMSG {session.PeerNick} :{resumeCtcp}", cts.Token)
                        .ConfigureAwait(false);

                    // Wait up to 30 s for the sender's DCC ACCEPT reply.
                    try
                    {
                        using var resumeCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                        resumeCts.CancelAfter(TimeSpan.FromSeconds(30));
                        resumeOffset = await tcs.Task.WaitAsync(resumeCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _pendingResumes.TryRemove((session.Filename, session.PeerPort), out _);
                        resumeOffset = 0; // fall back to fresh download on timeout
                    }
                }
            }
            // --- end RESUME handshake ---

            var endpoint = new NetworkEndpoint(session.PeerAddress, session.PeerPort, UseTls: false);
            await using var peerStream = await _networkProvider
                .ConnectAsync(endpoint, cts.Token).ConfigureAwait(false);

            var progress = new Progress<(long bytes, double rate)>(update =>
            {
                _sessions[sessionId] = _sessions[sessionId] with
                {
                    BytesTransferred = update.bytes,
                    TransferRate     = update.rate,
                };
                _ = _dispatcher.PublishAsync(
                    new DccProgress(_serverId, sessionId, update.bytes, update.rate));
            });

            long received = await DccReceiver.ReceiveAsync(
                peerStream,
                outputPath,
                session.FileSize ?? long.MaxValue,
                resumeOffset,
                progress,
                cts.Token).ConfigureAwait(false);

            _sessions[sessionId] = _sessions[sessionId] with
            {
                Status           = DccSessionStatus.Completed,
                BytesTransferred = received,
            };
            await _dispatcher.PublishAsync(new DccCompleted(_serverId, sessionId, received))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await FailSessionAsync(sessionId, "Transfer cancelled.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await FailSessionAsync(sessionId, ex.Message).ConfigureAwait(false);
        }
        finally
        {
            _activeCts.TryRemove(sessionId, out _);
        }
    }

    // ---------------------------------------------------------------------------
    // Initiate an outbound DCC SEND offer
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Offers a file to <paramref name="nick"/> via DCC SEND. Starts a TCP listener,
    /// sends the <c>CTCP DCC SEND</c> message, and waits for the peer to connect in a
    /// background task.
    ///
    /// The local advertised IP defaults to <c>127.0.0.1</c> (loopback). In production the
    /// UI layer should configure an external IP or use the server-detected external address;
    /// per-server external IP configuration is a Phase 4 addition.
    ///
    /// Emits <see cref="DccOfferSent"/>, <see cref="DccStarted"/>, periodic
    /// <see cref="DccProgress"/>, and <see cref="DccCompleted"/> or <see cref="DccFailed"/>.
    /// </summary>
    /// <param name="connection">IRC connection on which the CTCP message is sent.</param>
    /// <param name="nick">Target IRC nick to send the file to.</param>
    /// <param name="filePath">Absolute path of the file to send.</param>
    /// <param name="ct">Cancels the offer and background transfer task.</param>
    /// <exception cref="FileNotFoundException">File at <paramref name="filePath"/> does not exist.</exception>
    public async Task InitiateSendAsync(
        IRCConnection   connection,
        string          nick,
        string          filePath,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("File not found for DCC SEND.", filePath);

        var fileInfo = new FileInfo(filePath);
        string filename = fileInfo.Name;
        long fileSize   = fileInfo.Length;

        var listener  = CreateListener(0);
        listener.Start(backlog: 1);
        int localPort = ((IPEndPoint)listener.LocalEndpoint).Port;

        // Advertise loopback; UI/config should supply the real external IP in Phase 4.
        uint advertisedIp = IpToUint32(IPAddress.Loopback);
        string ctcpPayload = $"\x01DCC SEND {filename} {advertisedIp} {localPort} {fileSize}\x01";

        var session = new DccSession(
            Id:               Guid.NewGuid(),
            Server:           _serverId,
            Type:             DccTransferType.Send,
            PeerNick:         nick,
            PeerAddress:      "0.0.0.0",
            PeerPort:         localPort,
            Status:           DccSessionStatus.Pending,
            Filename:         filename,
            FileSize:         fileSize,
            BytesTransferred: 0,
            TransferRate:     0,
            ErrorMessage:     null);

        _sessions[session.Id] = session;

        await _dispatcher.PublishAsync(
            new DccOfferSent(_serverId, session.Id, nick, DccTransferType.Send, filename),
            ct: ct).ConfigureAwait(false);

        await connection.SendLineAsync($"PRIVMSG {nick} :{ctcpPayload}", ct).ConfigureAwait(false);

        // Wait for peer connection in a background task; this method returns once the CTCP is sent.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _activeCts[session.Id] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                using var tcpClient = await listener.AcceptTcpClientAsync(cts.Token)
                    .ConfigureAwait(false);
                listener.Stop();

                _sessions[session.Id] = _sessions[session.Id] with { Status = DccSessionStatus.Active };
                await _dispatcher.PublishAsync(new DccStarted(_serverId, session.Id)).ConfigureAwait(false);

                // Consume the resume offset stored by HandleResumeFromPeer (0 if no resume).
                _confirmedResumeOffsets.TryRemove(session.Id, out long resumeOffset);

                var progress = new Progress<(long bytes, double rate)>(update =>
                {
                    _sessions[session.Id] = _sessions[session.Id] with
                    {
                        BytesTransferred = update.bytes,
                        TransferRate     = update.rate,
                    };
                    _ = _dispatcher.PublishAsync(
                        new DccProgress(_serverId, session.Id, update.bytes, update.rate));
                });

                await using var peerStream = tcpClient.GetStream();
                long sent = await DccSender.SendAsync(peerStream, filePath, resumeOffset, progress, cts.Token)
                    .ConfigureAwait(false);

                _sessions[session.Id] = _sessions[session.Id] with
                {
                    Status           = DccSessionStatus.Completed,
                    BytesTransferred = sent,
                };
                await _dispatcher.PublishAsync(new DccCompleted(_serverId, session.Id, sent))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                listener.Stop();
                await FailSessionAsync(session.Id, "Transfer cancelled.").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                listener.Stop();
                await FailSessionAsync(session.Id, ex.Message).ConfigureAwait(false);
            }
            finally
            {
                _activeCts.TryRemove(session.Id, out _);
                cts.Dispose();
            }
        }, CancellationToken.None);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private TcpListener CreateListener(int port)
    {
        if (_listenerFactory is not null)
            return _listenerFactory(port);

        return new TcpListener(IPAddress.Any, port);
    }

    private async Task FailSessionAsync(Guid sessionId, string reason)
    {
        if (_sessions.TryGetValue(sessionId, out var s))
            _sessions[sessionId] = s with { Status = DccSessionStatus.Failed, ErrorMessage = reason };

        await _dispatcher.PublishAsync(new DccFailed(_serverId, sessionId, reason))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the effective download directory. Returns <see cref="DccSettings.DownloadDirectory"/>
    /// when set, otherwise falls back to <c>~/Downloads</c> on all platforms.
    /// </summary>
    internal static string ResolveDownloadDirectory(DccSettings settings)
    {
        if (!string.IsNullOrEmpty(settings.DownloadDirectory))
            return settings.DownloadDirectory;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
    }

    // Converts an IPv4 address to the big-endian uint32 used in DCC CTCP advertisements.
    private static uint IpToUint32(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        _dispatcher.Unsubscribe<CtcpRequest>(_ctcpHandler);

        foreach (var cts in _activeCts.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _activeCts.Clear();

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
