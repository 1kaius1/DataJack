# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- async void command/message handlers crash on network errors (DataJack/MainWindow.cs):

  `OnCommandIssued` and `OnMessageIssued` previously caught only
  `ArgumentException`; any `IOException`, `SocketException`, or other exception
  thrown by the underlying router calls would escape the `async void` boundary
  and become an unhandled exception, crashing the process. Both methods now
  catch `OperationCanceledException` silently (the UI already reflects the
  disconnected state via events) and catch all other `Exception` types, routing
  the message to the active buffer as an error line.

- Config migration corrupts startup when parent object is absent (storage/config/Loader.cs):

  `MigrateToV6`, `MigrateToV8`, `MigrateToV9`, and `MigrateToV10` each used a
  chained `&&` pattern that required the parent `"appearance"` or `"advanced"`
  JsonObject to already exist. When the parent key was missing (corrupted or
  hand-edited config), the guard failed silently, the required child fields were
  never inserted, but `schema_version` was still bumped. The subsequent
  `Deserialize<AppConfig>()` then saw a missing non-nullable sub-object and
  produced a null-field record, causing `NullReferenceException` on first access.
  Migrations now create the parent object when absent before inserting child keys.

### Added

- Reconnect-enabled config flag (storage/config/Schema.cs, Loader.cs):

  `AdvancedSettings.ReconnectEnabled` (bool, default `false`). When false, no
  automatic reconnection occurs after any disconnect. When true, involuntary
  disconnects trigger the existing exponential-backoff reconnect loop; voluntary
  `/quit` still never reconnects because `ServerSession.DisposeAsync` is called
  immediately after the QUIT is sent, cancelling the `ReconnectController` CTS
  before `ConnectionClosed` fires. Schema bumped 8 -> 9; `MigrateToV9` in
  Loader.cs adds `reconnect_enabled: false` to existing configs.

- BufferManager event coverage (ui/buffers/Manager.cs):

  Subscriptions added for 22 previously unhandled event types so that server
  output is visible rather than silently discarded:

  Connection/reconnect: `ConnectionFailed`, `ReconnectScheduled`,
  `ReconnectSucceeded`, `ReconnectFailed` -- shown in the server status buffer
  so the user sees connection feedback.

  Registration: `SASLStarted`, `SASLSucceeded`, `SASLFailed` -- shown in the
  server status buffer during SASL negotiation.

  Channel housekeeping: `NamesListReceived` silently populates
  `ChannelBuffer.Members` (the nicklist) without adding a chat line.
  `ChannelModeChanged` and `UserModeChanged` shown as `MessageKind.Mode` lines.
  `InviteReceived` shown as a notice in server status.

  Queries: `WhoIsReply`/`WhoIsEnd` and `WhoReplyEntry`/`WhoEnd` formatted as
  info lines in the server status buffer so `/whois` and `/who` produce visible
  output.

  Channel list: `ChannelListEntry` (compact `channel (count) topic` per line)
  and `ChannelListEnd` shown in server status so `/list` produces output.

  Ban list: `BanListEntry` and `BanListEnd` shown in server status.

  Messaging: `WallopsReceived` shown as a notice; `CtcpRequest` and `CtcpReply`
  shown as notices in server status.

  Errors: `NickInUse` and `PrivilegeError` shown as error lines in server status.

  Nicklist maintenance: `OnJoinedChannel` now adds the joining nick to
  `ChannelBuffer.Members`; `OnPartedChannel` and `OnKickReceived` now remove
  the departing nick. `OnNickChanged` falls back to the server status buffer
  when the user is not in any channel (e.g. `/nick` before joining).

### Changed

- `ReconnectController` is now opt-in (DataJack/ServerSession.cs):

  `_reconnect` is nullable; `ServerSession.ConnectAsync` only instantiates
  `ReconnectController` when `config.Advanced.ReconnectEnabled` is true.
  `DisposeAsync` null-checks before awaiting the controller.

- `/quit` cleans up the session (DataJack/MainWindow.cs):

  After `QuitAsync` returns, the session is removed from `_sessions` and
  `DisposeAsync` is called immediately. This guarantees no reconnect attempt
  occurs even when `ReconnectEnabled` is true, and frees server resources
  promptly.

### Added
- Away/idle management (core/irc/IdleMonitor.cs, storage/config/Schema.cs):

  `AwaySettings` (storage/config/Schema.cs): sealed record (AwayMessage string,
  AutoAwayEnabled bool, AutoAwayDelaySec int); defaults: message "Away", auto-away off,
  600-second idle delay. Added as `AppConfig.Away`. Schema version bumped 6 -> 7.
  `MigrateToV7` in Loader.cs adds the `away` object to existing v6 configs.

  `IdleMonitor` (core/irc/IdleMonitor.cs): tracks user input activity for auto-away.
  Constructor: `IdleMonitor(int delaySeconds, Func<int, CancellationToken, Task>?
  delayFactory = null)`. `delayFactory` is injectable for deterministic unit tests; null
  uses `Task.Delay`. Starts the countdown on construction. `NotifyActivity()`: atomically
  swaps the current `CancellationTokenSource` (cancelling the in-flight countdown task),
  fires `ActivityResumed` on the thread pool if the monitor was in the idle state, and
  starts a new countdown. Thread safety: `Interlocked.Exchange` on both `_cts` (reference
  type, swapped atomically) and `_idle` (int flag, 0=active/1=idle); `NotifyActivity` is
  intended to be called from the Avalonia UI thread so concurrent calls are not expected,
  but Dispose racing with the timer task is handled safely. `IdleTripped` event: fires
  once per idle cycle on a thread-pool thread when the delay elapses without activity.
  `ActivityResumed` event: fires on a thread-pool thread the first time `NotifyActivity`
  is called after an idle cycle. `Dispose()`: cancels the active CTS so the countdown
  task receives `OperationCanceledException` and exits cleanly; idempotent.

  `InputBox.ActivityOccurred` (ui/layout/InputBox.cs): new event raised at the start of
  every `OnKeyDown` handler, before key-specific processing. Used by `IdleMonitor` to
  reset the idle countdown on each keystroke.

  `LayoutManager.InputActivity` (ui/layout/LayoutManager.cs): new event forwarded from
  `InputBox.ActivityOccurred` via a lambda subscription in the constructor. Provides the
  idle-monitoring hook point to `MainWindow` without exposing `InputBox` directly.

  `MainWindow` wiring: `BootstrapAsync` creates an `IdleMonitor` when
  `config.Away.AutoAwayEnabled` is true and `AutoAwayDelaySec > 0`; subscribes
  `_layout.InputActivity` to `_idleMonitor.NotifyActivity`, and `IdleTripped` /
  `ActivityResumed` to `OnIdleTripped` / `OnActivityResumed` stubs (AWAY send deferred
  to when connection management is wired up in a later task). `OnClosed` disposes the
  monitor. The monitor is `null` when auto-away is disabled, so there is no overhead on
  the common path.

  13 new tests (tests/DataJack.Core.Tests/IdleMonitorTests.cs): Constructor_ZeroOrNegativeDelay_Throws
  (Theory, 3 values); Constructor_PositiveDelay_DoesNotThrow; IdleTripped_FiredWhenDelayElapses;
  IdleTripped_FiredExactlyOnce_PerIdleCycle; IdleTripped_NotFired_WhenActivityBeforeDelay;
  ActivityResumed_FiredWhenUserTypesAfterIdle; ActivityResumed_NotFired_WhenNoIdleCycleOccurred;
  ActivityResumed_FiredExactlyOnce_OnFirstKeystrokeAfterIdle; IdleAndResume_TwoCycles_BothFire;
  Dispose_StopsCountdown_NoIdleTripped; Dispose_CalledTwice_DoesNotThrow;
  NotifyActivity_AfterDispose_DoesNotThrow.

  4 new config tests (ConfigTests.cs): Default_Config_SchemaVersionIsSeven;
  Default_Away_HasExpectedDefaults; Loader_MigratesV6ToV7_AddsAwaySettings;
  Loader_RoundTrip_PreservesAwaySettings. Loader_MigratesV5ToV6_AddsLayoutMode updated to
  assert AppConfig.CurrentVersion (version-agnostic) instead of the hardcoded value 6.

- Spell checking (platform/spell/): `ISpellCheckService` interface with `Check(word)`
  and `Suggest(word, maxSuggestions)`. `NullSpellCheckService` no-op fallback used on
  unrecognized platforms or when the required native library is absent.
  `SpellCheckServiceFactory.Create()` selects the backend by OS at runtime.

  Linux: `LinuxSpellCheckService` (platform/spell/Linux.cs) via Enchant-2 P/Invoke
  (libenchant-2.so.2). `enchant_broker_init` + `enchant_broker_request_dict` with the
  current UI locale tag (e.g. "en_US"); falls back to the language code alone ("en") when
  the full tag has no dictionary. `enchant_dict_check` returns 0 for correct words.
  `enchant_dict_suggest` returns a char** which is iterated via `Marshal.ReadIntPtr` and
  freed with `enchant_dict_free_string_list`. `IsAvailable` = false when libenchant-2 is
  not installed or no matching dictionary exists for the locale.

  macOS: `MacosSpellCheckService` (platform/spell/Macos.cs) via the Objective-C runtime
  (`/usr/lib/libobjc.A.dylib`) + Foundation framework P/Invoke. Obtains the
  `NSSpellChecker` singleton via `sharedSpellChecker`. Spell check uses
  `checkSpelling:startingAt:language:wrap:inSpellDocumentWithTag:wordCount:` (NSRange
  return via `objc_msgSend` — arm64-safe, no `_stret` needed). Suggestions use
  `guessesForWordRange:inString:language:inSpellDocumentWithTag:` (NSArray* return). UTF-8
  NSStrings are created via `initWithBytes:length:encoding:` (copying variant) with the
  byte array pinned via `GCHandle` for the duration of the synchronous ObjC call, avoiding
  both `unsafe` blocks and use-after-free. `IsAvailable` = false when Foundation cannot be
  loaded or the ObjC runtime is unavailable.

  Windows: `WindowsSpellCheckService` (platform/spell/Windows.cs) via the `ISpellChecker`
  COM interface (Windows 8+). `CoCreateInstance` on `SpellCheckerFactory` CLSID, then
  vtable slot 3 (`CreateSpellChecker`) invoked via `Marshal.GetDelegateForFunctionPointer`
  with the current UI culture language tag (IETF, e.g. "en-US"). `Check`: vtable slot 4
  on `ISpellChecker`; reads `IEnumSpellingError::Next` (slot 3) — S_FALSE (1) = correct.
  `Suggest`: vtable slot 5 on `ISpellChecker`; drains `IEnumString::Next` (slot 3) up to
  `maxSuggestions` strings. `IsAvailable` = false when COM server is unavailable or
  `CreateSpellChecker` fails.

  UI integration: `InputBox.SetSpellCheckService(ISpellCheckService)` subscribes to
  `ContextRequested`. On right-click, the caret word is located via `FindWordAt` (letter,
  apostrophe, hyphen boundaries); if misspelled, a `MenuFlyout` populated with up to 8
  suggestions is set as `_textBox.ContextFlyout` and shown automatically. Each menu item
  replaces the word in place and repositions the caret. Command lines (text starting with
  '/') are never spell-checked. `LayoutManager.SetSpellCheckService()` delegates to
  `InputBox`. `MainWindow` constructs the service at startup via `SpellCheckServiceFactory`
  and calls `_layout.SetSpellCheckService` after config loads so the UI thread owns the
  COM apartment (required on Windows for apartment-threaded COM).

- LayoutManager tree view (ui/layout/LayoutManager.cs): mIRC-style vertical
  server/channel tree sidebar as an alternative to the HexChat-style tab bar.
  Two layout modes are now supported: "tabs" (Phase 2 default — horizontal TabControl
  at the top) and "tree" (new — fixed-width 200 px TreeView sidebar on the left).
  In tree mode the tab bar is hidden; buffers are grouped under collapsible server
  parent nodes (expanded by default). Global buffers (NetworkStatus, Highlights) sit
  at the root level outside any server group. Leaf nodes carry a kind prefix: "#" for
  channels, "~" for queries, "D" for DCC chat, "!" for notices, "%" for the raw log.
  Unread messages are signalled by changing the node foreground to the theme's
  TabUnreadForeground color (same heuristic as the tab bar). Selecting a node activates
  its buffer identically to selecting a tab. Switching modes preserves the active buffer
  and restores its selection in the target view.

  SetLayoutMode(mode): switches to "tabs" or "tree" instantly; ignores illegal values.
  ToggleLayoutMode(): flips between the two modes.
  CurrentLayoutMode property: returns the active mode string.

  /layout command (MainWindow.cs): /layout tabs|tree|toggle switches the mode and
  persists the preference to config. /layout toggle flips between the two modes.

  LayoutMode (storage/config/Schema.cs): added to AppearanceSettings as
  "layout_mode" (string, default "tabs"). Schema version bumped 5 -> 6.
  MigrateToV6 in Loader.cs adds "layout_mode": "tabs" to the "appearance" object
  of existing v5 configs. BootstrapAsync in MainWindow.cs calls
  _layout.SetLayoutMode(_configLoader.Config.Appearance.LayoutMode) after loading
  config so the persisted preference is applied on startup.

  3 new config tests (ConfigTests.cs): Default_Config_SchemaVersionIsSix,
  Default_Appearance_LayoutModeIsTabs, Loader_MigratesV5ToV6_AddsLayoutMode,
  Loader_RoundTrip_PreservesLayoutMode. Existing v3-to-v4 and v4-to-v5 migration
  tests updated to assert AppConfig.CurrentVersion (version-agnostic) and check
  that the layout_mode field is present after the full migration chain.

- DCC RESUME (core/protocol/dcc/Engine.cs, Transfer.cs): transfer restart at byte offset.
  DccCtcpParser.TryParseResumeOrAccept parses the shared RESUME / ACCEPT CTCP format
  (RESUME|ACCEPT filename port offset, quoted filenames supported). DccEngine gains three
  new roles: receiver role — AcceptReceiveAsync detects a partial on-disk file and (when
  an IRCConnection is configured) sends DCC RESUME, registers a pending TaskCompletionSource
  keyed by (filename, port), awaits DCC ACCEPT (30 s timeout, falls back to fresh download),
  then passes the confirmed offset to DccReceiver; sender role — OnCtcpRequest handles
  incoming DCC RESUME by matching a Pending Send session, storing the offset in
  _confirmedResumeOffsets, and sending DCC ACCEPT back via IRCConnection; sender role —
  the background send task in InitiateSendAsync consumes _confirmedResumeOffsets[sessionId]
  (default 0) and passes it to DccSender. DccReceiver.ReceiveAsync and DccSender.SendAsync
  each gain a long resumeOffset = 0 parameter: receiver opens the file in append mode when
  resumeOffset > 0 so existing bytes are preserved; ACK = resumeOffset + sessionBytes
  so the sender can track overall progress; returns total bytes on disk (resumeOffset +
  sessionBytes). Sender seeks the file to resumeOffset before streaming, progress reports
  total bytes (resumeOffset + sessionSent). DccEngine.AddSessionForTest, HasConfirmedResumeOffset,
  and GetConfirmedResumeOffset are internal test helpers. DccEngine constructor gains an
  optional IRCConnection? ircConnection = null parameter; all existing call sites are
  unaffected.

  23 new tests (DccCtcpParserResumeTests, DccResumeTransferTests, DccEngineResumeTests):
  DccCtcpParserResumeTests (12): RESUME bare filename; RESUME quoted filename; ACCEPT;
  offset 0 is valid; case-insensitive RESUME/ACCEPT/resume/accept (Theory 4 cases);
  unknown subcommand (SEND) returns false; null/empty params; invalid port; negative offset;
  missing offset; missing port. DccResumeTransferTests (5): DccReceiver appends to partial
  file; DccReceiver returns total including offset; DccReceiver offset 0 creates fresh file;
  DccSender with offset skips leading bytes; DccSender with offset 0 sends full file.
  DccEngineResumeTests (3): sender role stores confirmed offset after peer RESUME;
  RESUME for unknown session is ignored; AcceptReceiveAsync with partial file sends RESUME
  and downloads remainder when ACCEPT arrives.

- DCC SEND and DCC RECV (core/protocol/dcc/Engine.cs, Transfer.cs): full DCC file transfer
  engine. DccEngine (one instance per server) subscribes to CtcpRequest events; when a
  DCC SEND CTCP arrives it parses the offer, sanitizes the filename, and emits
  DccOfferReceived for the UI to present to the user. AcceptReceiveAsync(sessionId)
  connects to the peer, streams the file to the download directory, and emits DccStarted,
  periodic DccProgress, and DccCompleted or DccFailed. InitiateSendAsync(connection, nick,
  filePath) listens on a random port, sends the CTCP DCC SEND message, and waits for the
  peer to connect in a background task before streaming the file and emitting the same
  lifecycle events. Each session is tracked in DccEngine.Sessions as an immutable DccSession
  snapshot (Id, Type, PeerNick, PeerAddress, PeerPort, Status, Filename, FileSize,
  BytesTransferred, TransferRate, ErrorMessage).

- DccFilenameSanitizer (core/protocol/dcc/Engine.cs): public static class, thread-safe.
  Sanitize(filename): strips all directory components (both / and \, so Windows-style
  traversal sequences like "..\..\" work correctly on all platforms) via Path.GetFileName
  after normalizing backslashes to forward slashes; rejects filenames containing null
  bytes; rejects bare "." and ".."; caps result at 255 characters; returns null when the
  filename must be rejected entirely.
  IsExecutable(filename): returns true when the file extension matches a set of 30+
  extensions associated with executable code or scripts (.exe, .bat, .cmd, .sh, .bash,
  .zsh, .fish, .ps1, .py, .rb, .pl, .lua, .js, .vbs, .jar, .app, .dmg, .deb, .rpm,
  .run, .elf, and more); case-insensitive; returns false for null or empty input.
  A true result should trigger an additional confirmation prompt in the UI.

- DccCtcpParser (core/protocol/dcc/Engine.cs): internal static class; TryParse parses the
  params string of a CtcpRequest whose Command is "DCC". Handles both bare filenames
  (SEND file.txt ...) and quoted filenames with embedded spaces (SEND "my file.txt" ...).
  IP address is parsed as a decimal uint32 in network byte order (big-endian) and converted
  to dotted-decimal. A trailing passive-DCC token after the file size is silently ignored.
  Returns false for null/empty params, unknown subcommands (CHAT, RESUME, ACCEPT), invalid
  IP/port/size values, or missing required fields.

- DCC file transfer I/O (core/protocol/dcc/Transfer.cs): DccReceiver.ReceiveAsync reads
  data from the peer stream in 32 KB chunks, writes to the output file, and sends a 4-byte
  big-endian ACK after each chunk carrying the running total bytes received (clamped to
  uint.MaxValue for files >4 GB, consistent with all major DCC clients). DccSender.SendAsync
  reads the file and streams it to the peer in 32 KB chunks while a background DrainAcksAsync
  task drains 4-byte ACKs from the receiver to prevent TCP receive buffer overflow.

- DCC event types (core/events/Types.cs): DccTransferType enum (Send, Receive, Chat);
  DccSessionStatus enum (Pending, Active, Paused, Completed, Failed); DccOfferReceived
  (carries Server, SessionId, PeerNick, Type, Filename, FileSize, PeerAddress, PeerPort,
  IsExecutable); DccOfferSent; DccStarted; DccProgress (BytesTransferred, TransferRate);
  DccCompleted (BytesTransferred); DccFailed (Reason); DccChatMessageReceived and
  DccChatMessageSent (Phase 4 placeholders).

- DccSettings (storage/config/Schema.cs): sealed record (DownloadDirectory string?,
  AutoAccept bool, MaxFileSizeMb int); defaults: null directory (resolves to ~/Downloads),
  auto-accept off, no size limit. Added as AppConfig.Dcc. Schema version bumped 4 -> 5;
  MigrateToV5 in Loader.cs adds a default dcc object to existing v4 configs.

- 67 new tests (tests/DataJack.Core.Tests/DccEngineTests.cs):
  DccCtcpParserTests (15): valid SEND with bare filename; valid SEND with quoted filename
  containing spaces; IP conversion for 127.0.0.1, 0.0.0.0, and 255.255.255.255; port 0
  (passive DCC); trailing token ignored; false for null/empty/unknown subcommand/missing
  port/missing size/oversized IP/invalid port/negative size; case-insensitive subcommand.
  DccFilenameSanitizerTests (27): Sanitize preserves normal filenames and extensions;
  strips Unix path traversal; strips Windows path traversal; strips absolute Unix path;
  strips absolute Windows path; handles names at and over 255 chars; rejects null, empty,
  null bytes, lone dot, double dot, and slash-only; IsExecutable returns true for 13
  dangerous extensions (Theory), false for 6 safe extensions (Theory), case-insensitive
  match, no extension returns false, null/empty returns false.
  DccEngineTests (19): CtcpRequest -> DccOfferReceived emitted; offer fields (server, nick,
  filename, size, address, port, type) are correct; session ID is non-empty Guid; two offers
  have distinct session IDs; .exe file sets IsExecutable true; .jpg sets IsExecutable false;
  path traversal filename is sanitized in offer; session stored with Pending status; events
  from other server ignored; non-DCC CTCP ignored; unparseable DCC payload ignored;
  AcceptReceiveAsync downloads correct bytes; AcceptReceiveAsync emits DccStarted;
  AcceptReceiveAsync with unknown session ID throws; ResolveDownloadDirectory with explicit
  path returns it; ResolveDownloadDirectory with null falls back to ~/Downloads.
  DccReceiverTests (3): ReceiveAsync writes all bytes; ReceiveAsync stops at expectedSize
  with a larger stream; ReceiveAsync reports progress.

- 4 new config tests (ConfigTests.cs): Default_Config_SchemaVersionIsFive,
  Default_Dcc_HasExpectedDefaults, Loader_MigratesV4ToV5_AddsDccSettings,
  Loader_RoundTrip_PreservesDccSettings. Loader_MigratesV3ToV4_AddsArchiveSettings
  updated to assert AppConfig.CurrentVersion (version-agnostic) rather than 4, and
  now also asserts Dcc settings are present after the full migration chain.

- Socks5Transport (net/Socks5.cs): implements INetworkProvider; tunnels connections through a
  SOCKS5 proxy. Constructor: Socks5Transport(proxyHost, proxyPort, username?, password?) and a
  ProxySettings-based overload. ConnectAsync(NetworkEndpoint, ct): connects to the proxy via
  HappyEyeballs, performs the four-phase handshake, then optionally layers TLS with
  TlsTransport.WrapAsync. All four handshake phases are internal static methods for unit testing:
  NegotiateMethodAsync (sends greeting — NO AUTH always, USERNAME/PASSWORD when credentials
  present; validates proxy's SOCKS5 version byte; returns selected method), AuthenticateAsync
  (RFC 1929 subnegotiation; encodes username+password as UTF-8 length-prefixed bytes; throws
  Socks5Exception on rejection), SendConnectAsync (CONNECT request with ATYP=0x03 so the proxy
  performs DNS resolution — the target hostname is never resolved locally, preventing DNS leaks;
  port in big-endian two bytes), ReadConnectResponseAsync (reads four-byte header; maps non-zero
  reply codes to Socks5Exception with ReplyCode; discards bound address respecting ATYP 0x01/
  0x03/0x04 lengths; uses Stream.ReadExactlyAsync for framing correctness).

- Socks5Exception (net/Socks5.cs): IOException subclass with optional byte? ReplyCode.
  Reply codes 0x01–0x08 are mapped to descriptive strings in the exception message.

- TlsTransport (net/Tls.cs): extracted TLS wrapping logic into internal static
  WrapAsync(Stream inner, NetworkEndpoint endpoint, CancellationToken ct) so Socks5Transport
  can apply TLS over a proxied TCP connection with the same certificate validation and
  fingerprint-pin logic. TlsTransport.ConnectAsync refactored to call WrapAsync.

- ProxySettings (storage/config/Schema.cs): sealed record (Host, Port, Username?, Password?).
  Added as ServerEntry.Proxy? with default value null (no schema version bump — System.Text.Json
  deserializes absent JSON keys as null for nullable reference types). ServerEntry.New() updated.

- 17 new tests: Socks5TransportTests (15): NegotiateMethod sends correct greeting for no-auth
  and credential cases; server selects 0x00 or 0x02 correctly; wrong SOCKS version throws;
  Authenticate sends username+password bytes, accepts success, throws on rejection; SendConnect
  uses ATYP=0x03 (remote DNS), encodes host correctly, encodes port big-endian; ReadConnect
  succeeds for IPv4, throws with code on host-unreachable, handles IPv6 bound address; full
  no-auth and full with-auth happy-path assertions. ConfigTests (2): ServerEntry.Proxy round-
  trips through ConfigLoader, absent proxy field deserializes as null.

- LogArchiver (storage/logs/Archive.cs): static class with ArchiveOldLogsAsync(logDirectory,
  maxAgeDays=90) that recursively enumerates *.log files and compresses those whose last-write
  time predates the cutoff to <name>.log.gz via GZipStream (CompressionLevel.Optimal), then
  deletes the original. *.log.gz files are excluded by the glob pattern and never recompressed.
  Non-existent directories return immediately. CompressToAsync uses three chained await using
  var declarations whose reverse-order disposal ensures the GZip footer is written before the
  output FileStream is closed. zstd planned for a future phase.

- ExportManager (storage/logs/Export.cs): static class with ExportAsync(entries, stream,
  format, ct) and ExportToStringAsync convenience wrapper. ExportFormat enum: PlainText, Html.
  PlainText: one line per LogEntry, format varies by LogEntryKind — Message: "[ts] <nick> text",
  Action: "[ts] * nick text", Notice: "[ts] -nick- text", ServerMessage: "[ts] *** text".
  Timestamps are UTC, formatted as "yyyy-MM-dd HH:mm:ss". Html: self-contained document with
  embedded dark-theme CSS (1e1e1e background), per-kind colour classes (message/action/notice/
  server), line-level div elements with ts/nick/text spans. System.Net.WebUtility.HtmlEncode
  applied to from_nick and text fields prevents XSS in exported HTML. StreamWriter uses
  new UTF8Encoding(false) (no BOM marker) so an empty-input export returns an empty string.

- ArchiveSettings (storage/config/Schema.cs): sealed record (Enabled bool, MaxAgeDays int);
  default Enabled=true, MaxAgeDays=90. Added as AppConfig.Archive; CurrentVersion bumped 3→4;
  MigrateToV4 adds {"enabled": true, "max_age_days": 90} to configs that lack the "archive"
  key. Loader_MigratesV2ToV3 test updated to use AppConfig.CurrentVersion (version-agnostic
  assertion); Default_Config_SchemaVersionIsThree test removed and replaced with
  Default_Config_SchemaVersionIsFour.

- 27 new tests: LogArchiverTests (9): non-existent directory returns without error, empty
  directory is no-op, old file is compressed, old file original deleted, recent file not
  touched, existing gz skipped, CompressFileAsync creates gz, deletes original, content
  preserved after decompress. ExportManagerTests (15): plain text empty, message format,
  action asterisk, notice dashes, server-message triple-asterisk, UTC timestamp, multiple
  lines; HTML empty returns complete document, contains style block, message nick and text,
  timestamp, action class, notice class, special chars HTML-encoded, multiple entries;
  ExportToString matches stream for both formats. ConfigTests (3 net new): schema version 4,
  default archive settings, v3-to-v4 migration.

- LogFtsIndex (storage/logs/Indexer.cs): SQLite FTS5 search index over IRC log entries.
  Single standalone virtual table log_messages: from_nick and text are FTS-indexed with
  the unicode61 tokenizer; server, target, ts, kind are UNINDEXED (stored for retrieval
  and metadata filtering). InitializeAsync creates the table idempotently. IndexAsync
  inserts a LogEntry and returns it with the assigned SQLite rowid as Id. SearchAsync
  accepts a SearchQuery (Text, Nick, Server, After, Before), a zero-based page index,
  and a page size (default 50); returns a SearchResultPage. When SearchQuery.Text is
  non-empty it is passed to "log_messages MATCH @ftsQuery" using FTS5 query syntax
  (quotes for phrases, hyphen for NOT, column:term for column-scoped search); results are
  ordered by FTS5 rank then ts DESC. When Text is empty, only metadata filters are applied
  (full table scan) and results are ordered by ts DESC. Invalid FTS5 queries are caught
  (SqliteException) and return an empty page rather than surfacing the error. Nick filter
  uses COLLATE NOCASE exact match. After/Before filters compare Unix-second timestamps
  stored as INTEGER. Uses two separate SqliteCommand objects (INSERT then SELECT
  last_insert_rowid()) to avoid multi-statement limitations in Microsoft.Data.Sqlite.

- LogEntry (storage/logs/LogEntry.cs): sealed record (Id, Server, Target, FromNick, Text,
  Timestamp, Kind). LogEntryKind enum: Message, Action, Notice, ServerMessage. Id is 0
  for unsaved entries and is the SQLite rowid after indexing.

- SearchQuery (storage/logs/SearchQuery.cs): sealed record (Text, Nick?, Server?,
  After?, Before?). SearchResultPage: sealed record (Entries, TotalCount, Page, PageSize)
  with computed HasMore = (Page + 1) * PageSize < TotalCount.

- 27 new tests (tests/DataJack.Core.Tests/LogFtsIndexTests.cs): empty database; single
  entry found by text; FTS case-insensitive; no-match; FTS phrase query; invalid FTS5
  query returns empty; empty/whitespace text returns all; nick filter match/case-insensitive/
  exclude; server filter match/exclude; after/before date filters; combined date range;
  text+nick combined; text+server combined; TotalCount; timestamp-descending ordering;
  pagination (page 0, page 1, HasMore true, HasMore false); action entry indexed; assigned
  Id; full field round-trip.

- HighlightMatcher (core/irc/HighlightMatcher.cs): stateless, thread-safe static class for
  evaluating highlight patterns against IRC message text. IsHighlight(text, currentNick,
  patterns) checks the current nick as a whole word first (ContainsNickAsWord: OrdinalIgnoreCase,
  bounded by non-alphanumeric/non-underscore chars or string edges, returns false for empty
  nick), then evaluates each HighlightPattern in order and returns true on first match.
  Matches(text, pattern) dispatches by HighlightPatternKind: Literal uses
  OrdinalIgnoreCase (or Ordinal when CaseSensitive=true) substring search; Wildcard
  converts the glob expression to an anchored .NET regex via GlobToRegex (* -> .*, ? -> .,
  all other metacharacters escaped) and matches case-insensitively with a 100 ms timeout;
  Regex compiles the expression with IgnoreCase unless CaseSensitive is set, applies a
  100 ms timeout, and returns false on RegexParseException. Empty expressions always return
  false. ContainsNickAsWord is public for use by scripts/plugins.

- HighlightPattern (storage/config/Schema.cs): sealed record with Expression (string),
  Kind (HighlightPatternKind enum, JSON-serialized as string via JsonStringEnumConverter),
  and CaseSensitive (bool, default false). HighlightPatternKind enum: Literal, Wildcard,
  Regex. AppConfig.HighlightPatterns (List<HighlightPattern>) added; schema version bumped
  from 2 to 3; MigrateToV3 in Loader.cs adds an empty highlight_patterns array to v2 files.

- NotificationDispatcher (platform/notifications/Service.cs): refactored channel highlight
  detection to delegate to HighlightMatcher.IsHighlight. New optional constructor parameter
  Func<IReadOnlyList<HighlightPattern>>? patternsGetter (default null) is invoked on each
  channel message; null is treated as an empty pattern list. ContainsNickAsWord and
  IsWordChar removed from NotificationDispatcher (now live in HighlightMatcher).

- 50 new tests; 8 ContainsNickAsWord tests moved from NotificationDispatcherTests to
  HighlightMatcherTests (DataJack.Core.Tests/HighlightMatcherTests.cs, 50 tests total):
  9 ContainsNickAsWord tests (empty nick, standalone, start/end/middle/embedded/underscore-
  prefix/case-insensitive/repeated-occurrence); 7 Literal tests (case-insensitive, exact,
  substring, no-match, empty expression, case-sensitive-match, case-sensitive-no-match);
  9 Wildcard tests (star alone, star at ends, star in middle, question mark match, question
  mark too-short, case-insensitive, no match, empty, dot-escaped); 7 Regex tests (simple,
  default-case-insensitive, case-sensitive-wrong-case, case-sensitive-correct-case, word-
  boundary, invalid-pattern, empty); 4 GlobToRegex tests (star, question, dot-escaped,
  anchored); 14 IsHighlight integration tests (null nick, nick match, nick no-match,
  literal/wildcard/regex pattern, first/second pattern match, no-match, pattern-matches-
  without-nick, nick-matches-without-pattern). 3 schema-v3 tests added to ConfigTests
  (HighlightPatterns empty by default, schema version 3, v2→v3 migration, v1 full chain).
  Stale Default_Config_SchemaVersionIsTwo test removed; Loader_MigratesV1ToV2 updated to
  assert CurrentVersion and also check HighlightPatterns is present and empty.

- NotificationService (platform/notifications/): INotificationService interface with
  IsSupported and NotifyAsync(NotificationInfo, CancellationToken). NotificationInfo
  record carries Title, Body, and NotificationKind (Highlight, PrivateMessage, DccOffer,
  WatchedNickOnline). NullNotificationService is the no-op implementation used in tests
  and on unsupported platforms. NotificationServiceFactory.Create() selects
  LinuxNotificationService, MacosNotificationService, or WindowsNotificationService at
  runtime via RuntimeInformation.

- NotificationDispatcher (platform/notifications/Service.cs): subscribes to
  MessageReceived and ActionReceived on the event bus; reads the current nick from
  IRCStateModel.CreateQuery() on each event. Fires PrivateMessage notifications for
  non-channel PRIVMSGs and ACTIONs not sent by the local user. Fires Highlight
  notifications for channel messages and actions whose text contains the current nick
  as a whole word (ContainsNickAsWord: case-insensitive, bounded by non-alphanumeric /
  non-underscore characters or string edges; returns false for empty nick; scans all
  occurrences to handle repeated non-matching positions). Implements IDisposable to
  unsubscribe from the bus on disposal.

- LinuxNotificationService (platform/notifications/Linux.cs): [SupportedOSPlatform("linux")].
  Spawns notify-send with title, body, --app-name=DataJack, --expire-time=5000, and a
  freedesktop icon name chosen by NotificationKind. Uses ProcessStartInfo.ArgumentList
  (no shell interpolation). Silently swallows all exceptions.

- MacosNotificationService (platform/notifications/Macos.cs): [SupportedOSPlatform("macos")].
  Spawns osascript -e "display notification..." with title and body escaped for AppleScript
  double-quoted strings. Silently swallows all exceptions. Target API is
  UNUserNotificationCenter; osascript is the Phase 3 vehicle pending code-signing.

- WindowsNotificationService (platform/notifications/Windows.cs): [SupportedOSPlatform("windows")].
  Spawns powershell.exe -NoProfile -NonInteractive with a WinRT script loading
  Windows.UI.Notifications via ContentType=WindowsRuntime and showing a ToastText02 toast.
  Silently swallows all exceptions. Target API is the WinRT C# projection; PowerShell is
  the Phase 3 vehicle pending net10.0-windows retarget.

- 28 new tests (tests/DataJack.Core.Tests/NotificationDispatcherTests.cs): 8 unit tests
  for ContainsNickAsWord (empty nick, alone, start/end/middle with punctuation, embedded
  in longer word, underscore-prefixed, case-insensitive); 20 dispatcher integration tests
  (private message fires/kind/title/body/self-suppressed/multiple; channel highlight
  fires/kind/title/body/case-insensitive/no-match/substring-only/self-suppressed; action
  PM/channel-highlight/no-match/self-suppressed; no registered nick suppresses; unknown
  server suppresses).

- ServerListDialog (ui/dialogs/ServerList.cs): completed all per-entry fields. Added
  username override, realname override, and a full SASL section (Mechanism ComboBox with
  None/SCRAM-SHA-512/SCRAM-SHA-256/EXTERNAL/PLAIN, Account, and SASL Password fields).
  Added Connect Commands multi-line TextBox (one command per line, stored as List<string>
  in ServerEntry.ConnectCommands). Section labels group Connection, Identity, SASL, and
  Behavior fields. Edit form wrapped in ScrollViewer so all fields are reachable at any
  window height. Added Import and Export buttons (bottom-left) using Avalonia
  StorageProvider file picker (JSON Files filter); export writes the current list,
  import appends parsed entries. Errors are surfaced via a modal dialog. The Blank()
  helper converts whitespace-only text to null for optional string fields.
- ServerListExport (storage/config/ServerListExport.cs): new static class for server list
  serialization. ExportToJson serializes to a versioned JSON envelope with
  datajack_server_list_version, exported_at, and servers array; all ServerEntry fields
  including passwords are written verbatim (credential encryption is a future phase
  feature). ImportFromJson deserializes, assigns a fresh Guid to each entry to avoid ID
  collisions, and coerces null auto_join/connect_commands to empty lists and blank
  encoding to UTF-8. Throws JsonException on invalid JSON and InvalidDataException when
  the servers array is absent.
- 24 new export/import tests (tests/DataJack.Core.Tests/ServerListExportTests.cs):
  structural checks (valid JSON, format version key, exported_at, servers array count),
  field preservation (network name, port/TLS, plaintext password, null password, SASL
  credentials, null SASL, auto-join, connect commands, encoding), multiple entries,
  round-trip full-field fidelity, fresh-ID assignment, unique IDs across entries,
  sanitization of null auto_join / null connect_commands / empty encoding, error paths
  (invalid JSON throws JsonException, missing servers key throws InvalidDataException).

- Alias system (core/irc/AliasManager.cs): AliasManager stores user-defined command
  aliases and expands them at dispatch time. Set(name, expansion) adds or replaces an
  alias; Remove(name) removes one; GetAll() returns a snapshot. TryExpand(commandLine)
  takes the raw text after the '/' (e.g. "weather Seattle"), matches the first word
  against the alias map (case-insensitive), and returns the fully expanded command
  string including a leading '/' (e.g. "/msg #weather Seattle") or null when no alias
  matches. Substitution is single-pass to prevent double-substitution when arguments
  themselves contain '%' tokens: %1..%9 expand to individual whitespace-delimited
  arguments (empty string when the argument is absent), %* expands to all arguments
  joined by a single space. HandleAlias(args) implements the /alias command: no args
  lists all aliases alphabetically, one-word args shows a single alias definition,
  "name expansion" adds or replaces. HandleUnalias(name) implements /unalias.
  AliasesChanged event fires on Set and successful Remove so callers can persist to
  config. Expansions stored with or without a leading '/' are both handled correctly.
- AppConfig.Aliases (storage/config/Schema.cs): Dictionary<string, string> field added
  to AppConfig; stores alias name -> expansion pairs. Schema version bumped from 1 to 2.
- Config schema v2 migration (storage/config/Loader.cs): MigrateToV2 adds an empty
  "aliases" JSON object to v1 config files and sets schema_version to 2.
- 37 new alias tests (tests/DataJack.Core.Tests/AliasManagerTests.cs): constructor
  initialization (3), Set/GetAll semantics (6), Remove (3), AliasesChanged event (3),
  TryExpand null/no-match (2), %1 substitution (3), %2..%9 (2), %* (2), mixed tokens
  (2), expansion without leading slash (1), case-insensitive lookup (2), HandleAlias
  list/show/set (5), HandleUnalias (3).
- 4 new config tests (tests/DataJack.Core.Tests/ConfigTests.cs): Default_Aliases_IsEmpty,
  Default_Config_SchemaVersionIsTwo, Loader_RoundTrip_PreservesAliases,
  Loader_MigratesV1ToV2_AddsEmptyAliases.

- Phase 3 built-in command set (core/irc/CommandRouter.cs): 21 new methods on
  IRCCommandRouter. Channel operator actions: KickAsync (KICK), BanAsync /
  UnbanAsync (MODE +/-b), KickBanAsync (MODE +b then KICK in one call),
  OpAsync / DeopAsync (MODE +/-o), VoiceAsync / DevoiceAsync (MODE +/-v),
  ModeAsync (general MODE with optional parameter list). Channel info:
  InviteAsync (INVITE), TopicAsync (TOPIC set or bare clear), NamesAsync
  (NAMES), ListAsync (LIST with optional filter). User queries: WhoisAsync
  (WHOIS), WhoAsync (WHO). User interaction: QueryAsync (PRIVMSG if message
  provided, no-op if not), MeAsync (CTCP ACTION), CtcpAsync (arbitrary CTCP
  request), PingAsync (CTCP PING with UTC millisecond timestamp). Away:
  AwayAsync (AWAY with message or bare), BackAsync (bare AWAY). All new
  methods follow the same argument-validation pattern as Phase 1 commands.
- 40 new command router tests (tests/DataJack.Core.Tests/CommandRouterTests.cs)
  covering exact wire format for every new command: kick with and without
  reason, ban/unban mask, kickban two-line sequence, op/deop/voice/devoice,
  mode with and without params, invite, topic set and clear, names with and
  without channel, list with and without filter, whois, who with and without
  mask, query with and without message, me CTCP ACTION, ctcp with and without
  params, ping SOH-wrapped timestamp, away with message and bare, back; plus
  validation-error assertions for invalid channel, nick, empty mask/command.

- IRCStateUpdater (core/state/IRCStateUpdater.cs): one instance per server;
  subscribes to all IRC protocol events on the event dispatch thread and drives
  IRCStateModel.Apply to keep the snapshot tree current. Handles: connection
  lifecycle (ConnectionAttempted creates server entry, ConnectionEstablished /
  ConnectionClosed set IsConnected, WelcomeReceived sets RegisteredNick and
  ConnectionClosed clears channels/caps and marks monitored nicks offline);
  ISUPPORT token accumulation (merges across multiple 005 lines); IRCv3
  active-cap set (CapabilityNegotiated replaces, ServerCapabilityChanged
  adds/removes); nick changes (NickChanged renames local nick and updates all
  channel user dictionary entries); channel membership (JoinedChannel/
  PartedChannel/KickReceived for self or others, UserQuit across all channels);
  topic (TopicChanged sets text + preserves existing setter/time, TopicWhoTime
  updates setter and timestamp while preserving text, ChannelCreated stores
  creation time); channel and prefix modes (OnChannelModeChanged parses MODE
  strings using PREFIX and CHANMODES ISUPPORT tokens, applies prefix modes to
  ChannelUser.ChannelModes and channel flags/params to ChannelState.Modes,
  skips type-A list modes, falls back to IRC defaults when ISUPPORT is absent);
  NAMES list (NamesListReceived replaces the Users dictionary, maps prefix
  symbols to mode chars via PREFIX ISUPPORT, carries forward existing
  user/host/account/realname); WHO and WHOIS backfill (WhoReplyEntry /
  WhoIsReply update user/host/account/realname in all channels the nick
  appears in); user metadata (UserHostChanged / UserAwayChanged /
  UserAccountChanged / UserRealNameChanged each update the matching field in
  all channel entries for that nick); MONITOR (MonitorStatusChanged adds or
  updates MonitoredNick entries).
- 40 new state updater tests (tests/DataJack.Core.Tests/IRCStateUpdaterTests.cs)
  covering: connection lifecycle (5), ISUPPORT merge (2), capabilities (3),
  nick changes (3), channel membership (7), topic (3), NAMES prefix mapping (1),
  channel modes (3), WHO/WHOIS backfill (2), user metadata (5),
  MONITOR (2), cross-server isolation (1), snapshot isolation (1) -- 32 total,
  plus 8 additional edge-case assertions.

- IRCv3 capability handlers (core/caps/handlers/): one file per capability.
  CapabilityRegistry tracks active capabilities (from CapabilityNegotiated /
  ServerCapabilityChanged) and the local nick (from WelcomeReceived /
  NickChanged); active set is cleared on ConnectionEstablished so stale caps
  are not visible during reconnect. ServerTimeHandler wraps the registry and
  provides GetTimestamp(tags) which returns the parsed 'time' tag value when
  server-time is active, falling back to DateTimeOffset.UtcNow otherwise.
  EchoMessageHandler provides IsEchoedMessage(nick) for UI callers to detect
  server echoes of the client's own messages when echo-message is active.
  MonitorHandler manages the MONITOR watchlist: AddNickAsync / RemoveNickAsync
  / ClearAsync send MONITOR +/-/C protocol lines when the monitor cap is active;
  the watchlist is re-sent automatically on CapabilityNegotiated (covers
  reconnects). BatchHandler subscribes to RawLineReceived and processes
  batch-tagged lines directly (bypassing the main parser's async queue) to
  guarantee correct accumulation order; BATCH +/- lines start and end batches;
  PRIVMSG / ACTION / NOTICE lines tagged with batch= are accumulated per batch
  ID; BatchReceived is emitted on BATCH -. LabeledResponseHandler tracks the
  labeled-response cap and generates unique hex label strings via TryCreateLabel().
- 31 new handler tests (tests/DataJack.Core.Tests/CapabilityHandlersTests.cs)
  covering all handlers: registry cap tracking, local-nick tracking, nick-change
  filtering, server-time timestamp selection, echo-message nick matching,
  monitor send / dedup / clear / reconnect resubscription, batch accumulation
  (PRIVMSG, ACTION, Notice, multi-batch, unknown-id guard), labeled-response
  label generation.

- IRCParser phase-3 numerics and IRCv3 protocol commands
  (core/irc/Parser.cs): switched from Task.Run to a single sequential
  channel drain loop (same pattern as CapabilityNegotiator) so multi-line
  reply sequences (WHOIS, NAMES) are processed in TCP arrival order.
  Added handlers for: 005 ISUPPORT token parsing, 311/312/317/318/330
  WHOIS assembly (buffers partial replies, flushes on 318), 315 WHO end,
  352 WHO reply, 322/323 LIST, 329 channel creation time, 332/333
  TOPIC reply + TOPICWHOTIME, 353/366 NAMES accumulation and flush,
  367/368 ban list, 730/731 MONITOR online/offline, MODE (channel and
  user), AWAY (away-notify), CHGHOST, ACCOUNT, SETNAME.
  TopicChanged.SetterNick changed to string? (null when setter is
  unknown, e.g. 332 reply).
- New event types (core/events/Types.cs): IsupportTokensReceived,
  ChannelListEntry, ChannelListEnd, NamesEntry, NamesListReceived,
  UserModeChanged, WhoEnd.
- 21 new parser tests (tests/DataJack.Core.Tests/IrcParserTests.cs)
  covering all new numeric and command handlers.


- Configuration system (storage/config): AppConfig versioned schema (schema_version=1)
  with IdentitySettings, ServerEntry, SaslCredentials, AppearanceSettings, LoggingSettings,
  AdvancedSettings records. ConfigLoader reads/writes settings.json with atomic rename-on-save
  and a sequential migration runner (v1 initial schema). SettingsScope resolves the
  global -> server scope chain for nick, username, realname, encoding, SASL, and all
  appearance/logging/advanced settings with explicit per-scope override semantics.
- IrcTextParser (core/irc/TextParser.cs): pure parsing logic (no UI dependency) that
  converts a raw IRC message string into IrcSpan[] with per-span Bold, Italic, Underline,
  Strikethrough, Monospace, Reverse, and IrcColor (palette index or 24-bit hex) fields.
  Handles all mIRC control codes (x02, x03, x04, x0F, x11, x16, x1D, x1E, x1F), the full
  99-color mIRC palette (16 theme-defined + 83 fixed extended colors per the IRCv3 spec),
  and IRCv3 hex color (x04RRGGBB[,RRGGBB]). URL detection splits spans so URLs are always
  their own IrcSpan with the Url field set. GetExtendedColorRgb(int) exposes the fixed
  extended palette for the rendering layer.
- Theme system (ui/themes): ThemeData and ChromeColors records (theme.json schema) covering
  the 16-entry IRC color palette, 15 UI chrome colors, font family/size, and timestamp
  format. ThemeManager loads from the user theme directory (Paths.ThemesDirectory/<name>),
  falls back to the built-in default theme compiled in as an EmbeddedResource, and hot-reloads
  via FileSystemWatcher without restart. Default dark theme (assets/themes/default/theme.json)
  uses a Catppuccin Mocha color palette.
- Buffer system (ui/buffers): BufferKind enum (NetworkStatus, ServerStatus, Channel, Query,
  DccChat, Notices, RawLog, Highlights), MessageEntry readonly record struct, MessageKind
  enum, IBuffer interface, BufferBase abstract class. Concrete types: NetworkStatusBuffer,
  ServerStatusBuffer, ChannelBuffer (with Topic and Members), QueryBuffer, NoticesBuffer,
  RawLogBuffer, HighlightsBuffer, ChannelMember record. BufferManager subscribes to 19 IRC
  events (connection, registration, channel, message, nick, topic, raw) and routes each to
  the correct buffer, creating buffers on demand. Raises BufferCreated, BufferDestroyed,
  MessageAdded events for the UI layer.
- BufferLogWriter (storage/logs/Writer.cs): append-only per-buffer log writer with a bounded
  Channel queue (default 4096 depth) and a dedicated background write task. Log format:
  ISO8601 timestamp TAB nick TAB kind TAB text, one line per message. One file per buffer
  per calendar day under <logdir>/<server>/<date>_<buffer>.log; handles file rotation at
  midnight; atomic double-dispose guard via Interlocked.Exchange.
- IrcTextRenderer (ui/rendering/IrcTextRenderer.cs): Avalonia rendering wrapper that converts
  IrcSpan[] (from IrcTextParser) into Avalonia Inline elements. Applies bold/italic/underline/
  strikethrough/monospace font properties, SolidColorBrush for foreground/background (resolving
  palette indices through ThemeManager and extended colors through IrcTextParser), reverse video
  swap, and underlined link color for URL spans. RenderNick() produces consistently hashed
  nick colors from an 8-color palette.
- MessageView (ui/rendering/MessageView.cs): non-virtualized scrollback ScrollViewer + StackPanel
  (Phase 4 will add virtualization). Each message row shows timestamp, nick (colored via
  IrcTextRenderer.RenderNick for Normal messages, plain label for events), and IRC-formatted
  body text. Routing kind to event label text and brush. URL clicks raise UrlClicked event.
  SetBuffer() switches the active buffer; ApplyTheme() redraws on theme change.
- NicklistPanel (ui/rendering/Nicklist.cs): channel member list grouped by highest mode
  prefix (owners ~ admins & ops @ halfops % voiced + users) with case-insensitive
  alphabetical sort within each group. Right-click context menu for whois, query, op, deop,
  voice, devoice, kick, ban, ignore actions via NickAction event. Hover highlight.
- InputBox (ui/layout/InputBox.cs): single-line text entry with '/' command detection,
  per-buffer circular history ring (depth 100, Up/Down navigation, draft save), and Tab
  nick+command completion (sorted candidate cycling). Raises CommandSubmitted or
  MessageSubmitted on Enter.
- LayoutManager (ui/layout/LayoutManager.cs): main window Grid layout. Tab strip (top) +
  MessageView (center) + NicklistPanel (right, channel-only) + InputBox + status bar (bottom).
  Reacts to BufferManager.BufferCreated/BufferDestroyed to add/remove TabItems; shows unread
  tab indicator via foreground color. CommandIssued and MessageIssued events route to
  MainWindow. Tab selection drives buffer activation via TabControl.SelectionChanged.
- ServerListDialog (ui/dialogs/ServerList.cs): modal Window with a list/edit split layout.
  Displays all server entries; Add/Remove buttons manage the list; edit form covers network
  name, address, port, TLS, password, nick override, auto-join channels, auto-connect toggle.
  Save/Connect/Cancel actions; ConnectRequested event for immediate connection; config is
  persisted on close via ConfigLoader.UpdateAsync.
- MainWindow.cs: wires the full Phase 2 component graph (ConfigLoader -> ThemeManager ->
  EventDispatcher -> BufferManager -> LayoutManager). Loads config and theme asynchronously
  after window open; auto-connects servers with AutoConnect=true; routes /connect and
  /server commands to connection management; opens ServerListDialog on /serverlist; hot-reloads
  theme on ThemeChanged.
- ConfigTests (33 tests): AppConfig default values and schema version; ConfigLoader
  missing-file creation, round-trip persistence, atomic save (no .tmp left behind);
  ServerEntry.New defaults and unique IDs; SettingsScope global vs. server override,
  unknown server fallback, encoding scope.
- IrcTextParserTests (26 tests): plain text, empty, all eight formatting codes (bold,
  italic, underline, strikethrough, monospace, reverse, reset, mixed), mIRC color with
  1- and 2-digit index and fg+bg pair, bare color reset, IRCv3 hex color with and without
  background, URL detection/splitting in middle of text and irc:// scheme, plain text URL
  absence, extended palette index 16 and 98 and out-of-range.
- BufferLogWriterTests (7 tests): GetLogPath is under log directory and contains date;
  different server/buffer/date paths are distinct; slash in buffer ID is replaced in
  filename; Log writes and flushes a line; 10 entries all persisted; four-field TSV format
  with correct nick/kind/text; null nick writes hyphen; append mode does not truncate.


- IRCCommandRouter (CommandRouter.cs): translates user slash commands into correctly-formatted
  IRC protocol lines sent via IRCConnection.SendLineAsync. Phase 1 minimum command set:
  JoinAsync (with optional key), PartAsync (with optional reason), MsgAsync (PRIVMSG),
  NoticeAsync (NOTICE), NickAsync, QuitAsync (with optional reason), RawAsync. Each method
  validates its arguments before sending: channel name prefix and character validity, nick
  first-character and space/comma checks, target non-empty/no-space check, raw line CR/LF
  check. All methods accept a CancellationToken as the last parameter.
- CommandRouterTests: 40 tests covering correct wire format for all 7 commands, optional
  parameter variants (key/reason/no-reason), all four channel prefix characters (#&+!),
  all 7 RFC-defined nick-starting special chars ([]\\^_`{|}), and ArgumentException on every
  invalid-input path (empty args, space in target, digit-leading nick, CR/LF in raw line).
- ReconnectController (Reconnect.cs): subscribes to ConnectionClosed events and drives
  reconnection with exponential backoff. Config: 2s initial delay, 2x multiplier, 5-minute
  cap, +-20% jitter, configurable MaxAttempts (0 = unlimited). Publishes ReconnectScheduled
  before each delay, ReconnectSucceeded on success, ReconnectFailed when MaxAttempts is
  exhausted. SemaphoreSlim gate prevents concurrent loops; DisposeAsync cancels any
  in-flight delay and waits for the loop to exit. CapabilityNegotiator and SaslAuthenticator
  re-run automatically on reconnect because they subscribe to ConnectionEstablished; channel
  rejoin is deferred to IRCCommandRouter (Phase 2+). Injectable delay function for testing.
- IRCConnection.PrepareForReconnectAsync: internal method called by ReconnectController to
  cancel and await the receive task, dispose the stream, and clear all connection state so
  ConnectAsync can be called again on the same IRCConnection instance.
- ReconnectControllerTests: 8 tests covering ReconnectScheduled attempt numbers and delay
  doubling, ReconnectSucceeded on first success, ReconnectFailed after MaxAttempts
  exhausted, jitter bounds (+-20% of 10s initial), DisposeAsync cancels an in-flight loop,
  multi-server isolation (wrong-server close does not start loop), concurrent close events
  do not start two loops, MaxDelaySeconds cap (100x multiplier stays at cap).
- Complete typed event vocabulary (Types.cs): added all events from ARCHITECTURE.md s5.2
  that were not yet defined. New events: BatchReceived (batch type, batch ID, messages list);
  TopicWhoTime (333 numeric: setter nick, set timestamp); ChannelModeChanged (mode string,
  params, setter); ChannelCreated (329 numeric: created-at timestamp); UserHostChanged
  (CHGHOST: new ident/host); UserAwayChanged (AWAY/away-notify: is_away, message);
  UserAccountChanged (account-notify/extended-join: account); UserRealNameChanged (SETNAME);
  MonitorStatusChanged (730/731: is_online); WhoReplyEntry (352: channel?, nick, user, host,
  account?, realname); WhoIsReply (311/312/317/330: assembled whois data); WhoIsEnd (318);
  BanListEntry (367: mask, setter, set_at); BanListEnd (368); PrivilegeError (482 and
  related: command, reason).
- FloodController (FloodControl.cs): token-bucket rate limiter for outbound IRC lines.
  Configurable burst capacity (default 10 tokens), drain rate (default 2 tokens/sec), and
  max queue depth (default 50). `SendBypassAsync` sends immediately with no token cost for
  PONG, PING, QUIT, and server-initiated NOTICEs. `TrySend` enqueues at Normal or CTCP
  priority; Normal queue is always drained before the CTCP queue; `FloodQueueFull` event
  is emitted and the line is dropped when the queue is at capacity. Token cost per line:
  1.0 + 0.1 per full 100 bytes over 200 bytes. `IAsyncDisposable` lifecycle. Configurable
  per-server via `FloodController.Config`.
- FloodControllerTests: 14 tests covering cost formula (Theory), bypass path, burst
  capacity, rate limiting, Normal-before-CTCP priority, FIFO ordering, and queue overflow.
  Includes a regression test documenting that `BoundedChannelFullMode.DropWrite` returns
  `true` in .NET 10 even when items are silently discarded; `Wait` mode is used instead.
- SaslAuthenticator (SaslAuthenticator.cs in caps/): drives the AUTHENTICATE exchange after
  `CapabilityNegotiated` grants the `sasl` capability. Mechanism preference order per
  ARCHITECTURE.md §4.3: SCRAM-SHA-512, SCRAM-SHA-256, EXTERNAL, PLAIN (PLAIN only over
  TLS). Falls back to the next mechanism on 904; handles 908 (available mechanisms list)
  to skip unsupported options. Publishes `SASLStarted`, `SASLSucceeded`, `SASLFailed`.
  `SaslCredentials` record type for configuration.
- ScramSha256Mechanism / ScramSha512Mechanism (Scram.cs): RFC 5802 / RFC 7677 SCRAM
  implementation using PBKDF2 (Rfc2898DeriveBytes), HMAC-SHA-256/512, and SHA-256/512
  from System.Security.Cryptography. GS2 channel binding header `n,,` for IRC SASL.
  Verifies server signature (`v=`) in the server-final message; throws `SaslException` on
  mismatch (MITM protection). Testable via injected nonce factory.
- PlainMechanism (Plain.cs): PLAIN mechanism — base64(`\0authcid\0password`). Must only be
  used over TLS; enforcement is in SaslAuthenticator, not the mechanism itself.
- ExternalMechanism (External.cs): EXTERNAL mechanism — sends empty payload (AUTHENTICATE +)
  and relies on the TLS client certificate already presented during the handshake.
- ISaslMechanism interface and SaslException in ISaslMechanism.cs.
- SaslTests: 20 tests — 11 mechanism unit tests (SCRAM structure and error cases using RFC
  7677 inputs with self-consistent server-signature verification, PLAIN payload, EXTERNAL
  empty response) and 9 SaslAuthenticator integration tests (EXTERNAL full flow, 904
  fallback, exhaustion → SASLFailed, PLAIN gated to TLS, multi-server isolation).
- CapabilityNegotiator (Negotiator.cs in caps/): full IRCv3 CAP LS 302 → REQ → ACK/NAK
  negotiation. Accumulates multiline LS responses; requests the intersection of the 18
  wanted capabilities and server-advertised capabilities; strips ACK modifier prefixes
  (-/~/=); handles CAP NAK with graceful fallback; handles cap-notify NEW and DEL for
  runtime capability changes; publishes `CapabilityNegotiated` and `ServerCapabilityChanged`.
- CapabilityNegotiatorTests: 10 integration tests covering single-line and multiline LS,
  ACK with modifier stripping, NAK, no-wanted-caps path, cap-notify NEW (wanted and unknown
  caps), cap-notify DEL, and multi-server isolation.
- `IRCConnection.IsTls` property: true when the active connection was established over TLS;
  used by SaslAuthenticator to gate PLAIN mechanism inclusion.

### Fixed
- CI test step: replaced `--no-build` with `--no-restore`. The `--no-build` flag
  requires the test assembly to already exist on disk; when the build step had not produced
  it, VSTest reported "The argument ... is invalid" instead of a build error, masking the
  real failure. The `--no-restore` flag preserves the pipeline intent (packages already
  restored in the Restore step) while allowing the test step to compile the assembly itself
  if the artifact is absent or stale.
- `MultipleConnectionClosed_DoesNotStartConcurrentLoops` test rewritten to be
  deterministic. The original version used a zero-delay synchronous delay function; the
  entire reconnect loop could complete before the second `ConnectionClosed` event was
  published, allowing a second loop to start after the gate was released. The test now uses
  a `TaskCompletionSource` (`loopInDelay`) to confirm the loop has entered its delay before
  the second event fires, and a `SemaphoreSlim` (`delayGate`) to keep the loop blocked
  until the test releases it. No changes to `ReconnectController` -- the gate logic was
  correct.
- CapabilityNegotiator: replaced `Task.Run` + `SemaphoreSlim` event dispatch with a single
  `Channel<string?>` drain loop. The previous design could process consecutive CAP LS lines
  out of order (non-deterministic thread pool scheduling), causing multiline LS accumulation
  to fail in production when both lines arrived in the same TCP segment. The channel-based
  drain guarantees FIFO processing. `ConnectionEstablished` is signalled via a `null` item
  in the same channel to avoid a separate lock on `_serverCaps`.

### Added
- IRCParser (Parser.cs): subscribes to RawLineReceived; parses full IRCv3 format
  (tags with value unescaping, nick!user@host prefix decomposition, command, params);
  dispatches PRIVMSG/NOTICE/JOIN/PART/KICK/QUIT/NICK/TOPIC/INVITE/WALLOPS/ERROR and
  numerics 001/372/376/433 as typed events; CTCP ACTION dispatches ActionReceived,
  other CTCP dispatches CtcpRequest/CtcpReply; server-filtered per serverId
- IrcMessage internal struct with Param(index) safe accessor
- Encoder.cs Phase 1 placeholder stub
- Phase 1 event types added to Types.cs: WelcomeReceived, MOTDReceived, MOTDEnd,
  CapabilityNegotiated, ServerCapabilityChanged, SASLStarted, SASLSucceeded, SASLFailed,
  JoinedChannel, PartedChannel, KickReceived, TopicChanged, InviteReceived, NickChanged,
  NickInUse, UserQuit, WallopsReceived
- IrcParserTests: 21 tests covering pure parse logic (tags, prefix, params, numerics)
  and 5 integration tests through the full dispatcher pipeline
- IRCConnection (Connection.cs): raw line I/O over any INetworkProvider stream; PING answered
  before the event bus to prevent latency-induced disconnects; publishes ConnectionAttempted,
  ConnectionEstablished, ConnectionFailed, ConnectionClosed, RawLineReceived, RawLineSent;
  SemaphoreSlim write lock for thread-safe concurrent sends; configurable encoding with
  UTF-8 + replacement chars as default; max line length enforced at 8192 bytes
- TryParsePing: internal static helper handles bare "PING :token" and prefixed
  ":server PING :token" forms; exposed as internal for unit testing
- ConnectionFailed, ReconnectSucceeded, ReconnectFailed events added to Types.cs
- DuplexPipeStream and FakeNetworkProvider test helpers in TestHelpers.cs; reusable
  by IRCParser and later component tests
- IrcConnectionTests: 13 tests covering PING parsing (Theory), connection events,
  receive loop line delivery, automatic PONG, and send path
- NetworkProvider: INetworkProvider interface, NetworkEndpoint record, AddressFamilyPreference
  enum, and TlsCertificateException (Provider.cs)
- HappyEyeballs dual-stack connection helper (Ipv6.cs): resolves A+AAAA records, sorts by
  preference, starts attempts 250 ms apart per RFC 8305, returns first winner and disposes
  losing sockets; Nagle disabled on all sockets for low-latency IRC line delivery
- TcpTransport (Tcp.cs): thin wrapper over HappyEyeballs returning an owning NetworkStream
- TlsTransport (Tls.cs): TLS 1.2/1.3 with SNI, fingerprint-pin override for self-signed
  certs, TlsCertificateException on validation failure; client certificate support for
  SASL EXTERNAL
- Socks5Transport stub (Socks5.cs): Phase 3 placeholder
- InternalsVisibleTo DataJack.Core.Tests added to DataJack.Core.csproj
- NetworkProviderTests: 13 unit tests covering endpoint defaults, address sorting for all
  four preference modes, certificate fingerprint validation logic, and exception properties
- CI pipeline (.github/workflows/ci.yml): GitHub Actions matrix across ubuntu-latest,
  windows-latest, and macos-latest; steps are checkout, .NET 10 setup with NuGet cache,
  restore, Release build, test, and TRX artifact upload (always, including on failure)
- IRCStateModel (Model.cs): single-writer state model with volatile snapshot field and
  pure Apply(Func<snapshot, snapshot>) mutation pattern; CreateQuery() returns a
  snapshot-bound IRCStateQuery for consistent point-in-time reads from any thread
- IRCStateQuery interface and StateQuery implementation (Query.cs): GetCurrentNick,
  GetChannelUsers, GetChannelModes, GetChannelTopic, GetUserModes, IsConnected,
  GetActiveCapabilities; all read from the snapshot captured at query creation time
- Immutable state tree snapshot types (Snapshot.cs): IrcWorldSnapshot, ServerState,
  ChannelState, ChannelUser, QueryState, MonitoredNick, Topic, ModeSet; all sealed
  records supporting "with" expressions for immutable updates in Phase 1 handlers
- IrcStateModelTests: 5 tests covering initial empty state, Apply mutation, connected
  state reflection, snapshot isolation, and unknown server/channel edge cases
- EventDispatcher (Bus.cs): three bounded Channel<Action> queues (Critical/Normal/Low);
  Subscribe<T>/Unsubscribe<T>/PublishAsync<T>; single dispatch thread with priority
  ordering; IAsyncDisposable lifecycle; ReaderWriterLockSlim-protected handler registry
- Event type vocabulary skeleton (Types.cs): readonly record struct definitions for
  connection, message, and error event categories; full vocabulary populated in Phase 1
- EventPriority enum (Priority.cs): Critical, Normal, Low tiers matching ARCHITECTURE.md §5.1
- EventDispatcherTests: 4 tests covering subscribe/dispatch, multi-handler dispatch,
  unsubscribe, and no-subscriber no-op; all 10 tests passing
- Platform path resolution: Paths.cs resolves config, log, plugin, script, and theme
  directories per OS (XDG on Linux, Application Support on macOS, AppData on Windows);
  respects XDG_CONFIG_HOME when set to an absolute path
- PathsTests: 6 unit tests covering absolute path guarantee, app-name segment, and
  subdirectory relationships
- Solution scaffolding: DataJack.Core class library, DataJack Avalonia executable, and
  DataJack.Core.Tests xUnit project wired into DataJack.slnx
- Minimal Avalonia entry point (Program.cs, App.cs, MainWindow.cs) producing a compiling,
  launchable application shell targeting .NET 10
- NuGet references: Avalonia 12.0.4, MoonSharp 2.0.0, Microsoft.Data.Sqlite 10.0.8, xunit 2.9.3

### Changed
- Target framework updated to net10.0 (current LTS; .NET 10 SDK is what is installed)
- Solution file format updated from .sln to .slnx (.NET 10 default XML format)
- CLAUDE.md updated to reflect net10.0 target, .slnx solution file, and current phase status

### Deprecated

### Removed

### Fixed

### Security

---

## [0.1.0] - YYYY-MM-DD

### Added
- Initial release

[Unreleased]: https://github.com/1kaius1/DataJack/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/1kaius1/DataJack/releases/tag/v0.1.0
