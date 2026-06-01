# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
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
