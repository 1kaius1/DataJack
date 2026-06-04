# Planning

---

## Project Goal

Build a first-class, cross-platform IRC client for Linux, Windows, and macOS with full feature parity with HexChat and mIRC. The client ships as compiled native binaries with no runtime dependency on a web engine.

---

## Non-Goals

- No web engine (no Electron, no Tauri, no CEF embedded browser)
- No mIRC Script Language (mSL) interpreter — Lua is the scripting language; mSL migration guides may be written as documentation
- No bundled IRC network list beyond a short curated set — users manage their own server list
- No cross-device sync in v1.0 — architecture permits it as a future addition

---

## Current Status

**Status:** Phase 3 (HexChat Feature Parity) nearly complete. Phases 0-2 fully implemented and tested.

Architecture is documented and finalized in [ARCHITECTURE.md](ARCHITECTURE.md). Stack is decided (C# .NET 10 + Avalonia 12). Phase 3 remaining: away/idle management.

---

## Stack

**C# (.NET 10) with Avalonia UI.** Documented and justified in ARCHITECTURE.md §1.

| Dependency | Purpose |
|-----------|---------|
| `Avalonia` | Cross-platform native UI — X11/Wayland, DirectX, Metal |
| `MoonSharp` | Lua 5.2 scripting engine — pure C#, sandboxable |
| `Microsoft.Data.Sqlite` | Config persistence, FTS5 full-text search index |
| `System.Net.Security.SslStream` | TLS 1.2/1.3 with client certificate support |
| `System.Threading.Channels` | All inter-thread queues; backpressure support |

---

## Milestones

### Phase 0 — Foundation
*A compiling, cross-platform skeleton with CI.*

- [x] Solution structure matching the module breakdown in ARCHITECTURE.md §17
- [x] Cross-platform build (Linux, Windows, macOS) producing self-contained binaries
- [x] CI pipeline: build + test on all three platforms
- [x] Platform abstraction layer (`/platform`): path resolution only, no business logic
- [x] Core `EventDispatcher`: typed events, priority queue, `Channel<Event>` threading model
- [x] `IRCStateModel` skeleton: single-writer, immutable snapshot reads, `IRCStateQuery` interface

---

### Phase 1 — IRC Core
*Connect to a real IRC server, join a channel, send and receive messages.*

- [x] `NetworkProvider`: TCP and TLS transports; IPv6 dual-stack with happy-eyeballs
- [x] `IRCConnection`: raw line I/O on the network thread; PING/PONG handled here before the bus; `IsTls` property
- [x] `IRCParser`: full IRCv3 message format (tags, prefix, command, params, trailing); character encoding layer
- [ ] Character encoding: UTF-8 default; per-server fallback; per-channel override; invalid bytes → U+FFFD + `EncodingWarning` event — deferred to `IRCCommandRouter` step when the encode/decode boundary is finalised
- [x] `CapabilityNegotiator`: CAP LS 302 → REQ → ACK/NAK lifecycle; all capabilities in ARCHITECTURE.md §4.2; cap-notify NEW/DEL; ordered `Channel<string?>` drain loop for correct multiline LS accumulation
- [x] `SASLAuthenticator`: SCRAM-SHA-512, SCRAM-SHA-256, EXTERNAL (cert-based), PLAIN (TLS-gated only); mechanism fallback queue; server-signature mutual verification for SCRAM
- [x] `FloodController`: token bucket per connection; priority lanes (PONG/PING/QUIT bypass bucket); Normal/CTCP queues; configurable capacity, drain rate, and max depth; `FloodQueueFull` event
- [x] `ReconnectController`: exponential backoff (2s initial, 2× multiplier, 5min cap, ±20% jitter); channel rejoin on reconnect
- [x] `IRCCommandRouter`: `/join`, `/part`, `/msg`, `/notice`, `/nick`, `/quit`, `/raw` minimum
- [x] Complete typed event vocabulary wired up — all events in ARCHITECTURE.md §5.2

---

### Phase 2 — Minimal Viable UI
*A usable chat interface.*

- [x] `BufferManager`: create/destroy buffers; all buffer types from ARCHITECTURE.md §6.1
- [x] `MessageView`: non-virtualized scrollback (virtualization is Phase 4); `IRCTextRenderer` (mIRC color codes 0-99, bold, italic, underline, strikethrough, monospace, reverse, hex color, URL detection, nick coloring)
- [x] `InputBox`: command vs. message detection; per-buffer history (Up/Down); nick tab completion
- [x] `NicklistPanel`: grouped by mode prefix (owners -> admins -> ops -> halfops -> voiced -> none); `multi-prefix` support; right-click context menu
- [x] `LayoutManager`: tab bar (HexChat-style) only -- tree view and splits are Phase 4
- [x] `ThemeManager`: load `theme.json` from disk; built-in default theme compiled in as fallback
- [x] Basic `ServerListDialog`: add/edit/delete server entries; auto-connect on launch
- [x] `BufferLogWriter`: append-only per-buffer log files; tab-delimited format (timestamp, nick, type, text)
- [x] Configuration system: scoped settings (global -> server -> channel); schema v1; read/write; versioned migrations

---

### Phase 3 — HexChat Feature Parity
*A daily-driveable client matching HexChat's feature set.*

- [x] Extended `IRCParser` with all Phase 3 numerics and IRCv3 protocol commands (005 ISUPPORT,
  311/312/317/318/330 WHOIS assembly, 315/352 WHO, 322/323 LIST, 329 creation time, 332/333
  TOPIC, 353/366 NAMES, 367/368 ban list, 730/731 MONITOR, MODE, AWAY, CHGHOST, ACCOUNT,
  SETNAME); switched to sequential channel drain loop for in-order multi-line reply assembly.
  New event types: IsupportTokensReceived, ChannelListEntry, ChannelListEnd, NamesEntry,
  NamesListReceived, UserModeChanged, WhoEnd. TopicChanged.SetterNick is now string?.
- [x] Full IRCv3 capability handlers (`/core/caps/handlers/` — one file per capability):
  CapabilityRegistry (active-cap + local-nick state); ServerTimeHandler (timestamp resolution);
  EchoMessageHandler (IsEchoedMessage predicate); MonitorHandler (watchlist + MONITOR protocol);
  BatchHandler (BATCH +/- accumulation, emits BatchReceived); LabeledResponseHandler (label
  generation; full correlation is Phase 4).
- [x] `server-time` tag used for all displayed timestamps (ServerTimeHandler.GetTimestamp)
- [x] `monitor` capability for nick online/offline tracking; `MonitorStatusChanged` events
  (730/731 in parser; MonitorHandler manages watchlist and reconnect resubscription)
- [x] Full `IRCStateModel`: `IRCStateUpdater` subscribes to all protocol events for one server
  and drives `IRCStateModel.Apply` to maintain the snapshot tree: connection lifecycle,
  ISUPPORT token accumulation, IRCv3 active-cap set, channel membership (join/part/kick/quit/
  nick rename), topic + creation time, channel and prefix modes, NAMES user list with
  prefix-to-mode mapping, WHO/WHOIS user-info backfill, CHGHOST, away status, account,
  realname (SETNAME), and MONITOR online/offline status.
- [x] Full built-in command set (ARCHITECTURE.md §13): 21 new methods added to
  `IRCCommandRouter`: `/kick` (KICK), `/ban`+`/unban` (MODE +/-b), `/kickban` (MODE +b
  then KICK), `/op`+`/deop` (MODE +/-o), `/voice`+`/devoice` (MODE +/-v), `/mode`
  (general MODE with optional parameter list), `/invite` (INVITE), `/topic` (TOPIC
  set/clear), `/names` (NAMES), `/list` (LIST with optional filter), `/whois` (WHOIS),
  `/who` (WHO), `/query` (PRIVMSG on message or no-op), `/me` (CTCP ACTION), `/ctcp`
  (arbitrary CTCP request), `/ping` (CTCP PING with UTC millisecond timestamp),
  `/away`+`/back` (AWAY set/clear). Commands deferred to later tasks: `/ignore`,
  `/unignore` (require ignore-list manager), `/timer` (requires timer subsystem),
  `/set` (requires config access), `/help` (requires help-text registry), `/connect`,
  `/reconnect` (require multi-server bootstrap).
- [x] Alias system: `AliasManager` with `/alias` and `/unalias` command handlers;
  single-pass `%1`..`%9` and `%*` argument substitution; case-insensitive name
  lookup; `AliasesChanged` event for config persistence; aliases stored in
  `AppConfig.Aliases` (schema v2); schema v2 migration adds empty aliases map
  to existing v1 configs.
- [x] `ServerListDialog`: complete — all fields exposed in the edit form (username
  override, realname override, SASL mechanism/account/password, connect commands
  one-per-line); scrollable edit panel; Import/Export JSON buttons using Avalonia
  StorageProvider file picker. `ServerListExport` (storage/config/ServerListExport.cs)
  handles serialization: exports to a versioned envelope (`datajack_server_list_version`,
  `exported_at`, `servers[]`); import assigns fresh UUIDs to all entries and sanitizes
  null list fields and blank encoding.
- [x] `NotificationService`: highlight and PM desktop notifications on all three platforms.
  `INotificationService` / `NotificationInfo` / `NotificationKind` defined in
  `platform/notifications/Service.cs`. `NotificationDispatcher` subscribes to
  `MessageReceived` and `ActionReceived`; fires `PrivateMessage` notifications for
  direct messages and `Highlight` notifications for channel messages/actions whose text
  contains the current nick as a whole word (case-insensitive; bounded by non-alphanumeric,
  non-underscore characters). `NullNotificationService` no-op for testing.
  `NotificationServiceFactory` selects backend by OS at runtime.
  - Linux: `LinuxNotificationService` (notify-send subprocess → org.freedesktop.Notifications)
  - macOS: `MacosNotificationService` (osascript `display notification`; target is
    `UNUserNotificationCenter` — native P/Invoke binding deferred pending code-signing)
  - Windows: `WindowsNotificationService` (PowerShell WinRT script; target is WinRT
    C# projection — deferred pending net10.0-windows retarget)
- [x] Highlight pattern matching: `HighlightMatcher` (core/irc/HighlightMatcher.cs) — static,
  thread-safe. `HighlightPatternKind` enum (Literal/Wildcard/Regex) and `HighlightPattern`
  record (Expression, Kind, CaseSensitive) added to config schema (storage/config/Schema.cs).
  `IsHighlight(text, currentNick, patterns)`: checks the current nick as a whole word
  first (ContainsNickAsWord), then evaluates each configured pattern. `Matches(text, pattern)`:
  Literal — OrdinalIgnoreCase (or Ordinal when CaseSensitive); Wildcard — glob converted to
  anchored regex via GlobToRegex (`*`→`.*`, `?`→`.`, regex metacharacters escaped), always
  case-insensitive; Regex — full .NET regex with 100 ms timeout, invalid patterns return
  false. `AppConfig.HighlightPatterns` stored as JSON array; schema bumped to v3; migration
  v3 adds empty array to existing v2 configs. `NotificationDispatcher` updated to accept
  optional `Func<IReadOnlyList<HighlightPattern>>` patternsGetter and delegate the channel
  highlight check to `HighlightMatcher.IsHighlight` (existing nick-only behavior preserved
  when getter is null).
- [x] Log search: `LogFtsIndex` (storage/logs/Indexer.cs) backed by a standalone SQLite FTS5
  virtual table `log_messages`. `from_nick` and `text` are FTS-indexed (unicode61 tokenizer);
  `server`, `target`, `ts`, `kind` are UNINDEXED (stored, not tokenized). `InitializeAsync`
  creates the table if absent. `IndexAsync(LogEntry)` inserts and returns the entry with
  the assigned rowid as `Id`. `SearchAsync(SearchQuery, page, pageSize)` returns a
  `SearchResultPage` with `Entries`, `TotalCount`, `Page`, `PageSize`, and `HasMore`.
  When `SearchQuery.Text` is non-empty it is passed directly to `log_messages MATCH`
  (FTS5 query syntax: phrases, exclusions, etc.) and results are ordered by FTS5 rank then
  timestamp DESC; invalid FTS5 queries return empty rather than throwing. When `Text` is
  empty only metadata filters are applied (full table scan). Metadata filters: `Nick`
  (case-insensitive exact match), `Server` (exact match), `After`/`Before` (Unix timestamp
  range). `LogEntry` (storage/logs/LogEntry.cs): Id, Server, Target, FromNick, Text,
  Timestamp, Kind (`LogEntryKind`: Message/Action/Notice/ServerMessage). `SearchQuery`
  and `SearchResultPage` (storage/logs/SearchQuery.cs).
- [x] Log archive: `LogArchiver` (storage/logs/Archive.cs) — `ArchiveOldLogsAsync(dir, maxAgeDays)`
  enumerates `*.log` files recursively and gzip-compresses those whose last-write time exceeds
  `maxAgeDays` days, producing `<name>.log.gz` and deleting the original. Compression via
  `System.IO.Compression.GZipStream` (optimal level); disposal order of nested `await using`
  guarantees the gzip footer is flushed before the output FileStream closes. Already-compressed
  `.log.gz` files and non-existent directories are silently skipped. zstd planned for a future
  phase when a pure-.NET implementation is available.
  `ExportManager` (storage/logs/Export.cs) — `ExportAsync(entries, stream, format)` and
  `ExportToStringAsync` convenience overload. Two formats: `ExportFormat.PlainText` (one line per
  entry: `[yyyy-MM-dd HH:mm:ss] <nick> text` / `* nick text` / `-nick- text` / `*** text`; UTC
  timestamps) and `ExportFormat.Html` (self-contained document with embedded CSS, dark theme,
  per-kind colour classes; `WebUtility.HtmlEncode` applied to nick and text to prevent XSS).
  `StreamWriter` uses `new UTF8Encoding(false)` (no BOM) so the empty-export case returns an
  empty string.
  `ArchiveSettings` (storage/config/Schema.cs): `Enabled` (bool, default true) and `MaxAgeDays`
  (int, default 90); added to `AppConfig`; schema bumped to v4; migration v4 adds default
  archive object to existing configs.
- [x] SOCKS5 proxy transport: `Socks5Transport` (net/Socks5.cs) implements `INetworkProvider`.
  Four internal-static handshake phases (testable without a real proxy):
  1. `NegotiateMethodAsync` — sends greeting with NO AUTH (0x00) + optionally USERNAME/PASSWORD
     (0x02); returns the proxy-selected method.
  2. `AuthenticateAsync` — RFC 1929 sub-negotiation; throws `Socks5Exception` on rejection.
  3. `SendConnectAsync` — CONNECT request with ATYP=0x03 (domain name, big-endian port).
     The hostname is sent as-is; the proxy resolves DNS — no local lookup, no DNS leak.
  4. `ReadConnectResponseAsync` — reads and validates the CONNECT reply; discards the bound
     address (IPv4/IPv6/domain handled); throws `Socks5Exception` with the reply code on failure.
  After a successful handshake, optionally wraps the stream in TLS via `TlsTransport.WrapAsync`.
  `Socks5Exception` (IOException subclass with optional `ReplyCode` byte).
  `TlsTransport` refactored: TLS logic extracted into `internal static WrapAsync(Stream, endpoint, ct)`
  so both `TlsTransport.ConnectAsync` and `Socks5Transport.ConnectAsync` use the same path.
  `ProxySettings` (storage/config/Schema.cs): sealed record (Host, Port, Username?, Password?);
  added as `ServerEntry.Proxy?` (nullable, `= null` default, no schema bump — missing JSON key
  deserializes as null). `ServerEntry.New()` updated to include `Proxy: null`.
- [x] DCC SEND and DCC RECV: file path sanitization (no traversal, no null bytes, Windows-
  style `\` separator handled cross-platform); executable file type warning (30+ extensions);
  configurable download directory (DccSettings, schema v5). DccEngine (one per server)
  parses incoming CTCP DCC SEND offers via DccCtcpParser, sanitizes filenames via
  DccFilenameSanitizer, emits DccOfferReceived, and provides AcceptReceiveAsync /
  InitiateSendAsync. DccReceiver / DccSender handle the actual TCP I/O with 4-byte ACK
  protocol. DccSession snapshot tracks per-session state.
- [x] DCC RESUME: transfer restart at byte offset. DccCtcpParser.TryParseResumeOrAccept
  parses both RESUME and ACCEPT CTCP messages (same structure: filename port offset).
  Receiver role: AcceptReceiveAsync detects a partial file, sends DCC RESUME via
  IRCConnection, awaits DCC ACCEPT (30 s timeout, falls back to fresh download), then
  resumes from the confirmed offset using DccReceiver with append mode.
  Sender role: incoming DCC RESUME CTCP stores the offset in _confirmedResumeOffsets
  and replies DCC ACCEPT; background send task seeks to offset via DccSender.
- [x] `LayoutManager`: tree view (mIRC-style server → channel hierarchy).
  LayoutManager gains two modes: "tabs" (Phase 2 tab bar) and "tree" (200 px
  sidebar TreeView). Buffers grouped under collapsible server nodes; global buffers
  at root level. SetLayoutMode/ToggleLayoutMode/CurrentLayoutMode API.
  /layout command in MainWindow persists mode to AppearanceSettings.LayoutMode
  (schema v6, migration v6 adds layout_mode="tabs" to existing configs).
- [x] Spell checking: platform-specific backends via `ISpellCheckService` (platform/spell/).
  `NullSpellCheckService` fallback + `SpellCheckServiceFactory` OS selector.
  Linux: `LinuxSpellCheckService` via Enchant-2 P/Invoke (libenchant-2.so.2; supports
  Hunspell/Aspell/Nuspell); locale tag tried full then language-only fallback.
  macOS: `MacosSpellCheckService` via ObjC runtime P/Invoke (`NSSpellChecker`);
  `checkSpelling:startingAt:` + `guessesForWordRange:inString:` selectors;
  `GCHandle`-pinned `initWithBytes:length:encoding:` for safe UTF-8 NSString creation.
  Windows: `WindowsSpellCheckService` via `ISpellChecker` COM P/Invoke (vtable slot
  delegation for `CreateSpellChecker`, `Check`, `Suggest`); `IEnumSpellingError` and
  `IEnumString` drained via delegate-for-function-pointer.
  All three backends degrade to `IsAvailable=false` on missing library or COM server.
  `InputBox.SetSpellCheckService()`: right-click on a misspelled word shows up to 8
  suggestions in a `ContextFlyout`; selecting one replaces the word in place; command
  lines (`/`-prefixed) are never spell-checked. `LayoutManager.SetSpellCheckService()`
  delegates to `InputBox`. `MainWindow` creates the service via the factory at startup
  and wires it after config loads (so the UI thread owns the COM apartment on Windows).
- [ ] Away/idle management: auto-away on idle timeout; `/away`, `/back`; away message in config

---

### Phase 4 — mIRC Feature Parity
*Scripting engine with mIRC-equivalent power; UI completeness.*

- [ ] `ScriptEngine`: MoonSharp VM per script; full sandbox (ARCHITECTURE.md §7.2): blocked modules (`io`, `os.execute`, `require`, `debug`), CPU step limit, memory limit, wall-clock `CancellationToken`
- [ ] Full Lua API bridge: `irc.*`, `ui.*`, `irc.state.*`, `timer.*`, `store.*`, `command.register()`
- [ ] Event cancellation (`event.cancel()`, `event.halt_display()`) and mutation (`event.set_text()`) in handler dispatch chain
- [ ] Script-registered custom commands; script commands visible in `CommandRouter`
- [ ] Scrollback virtualization: configurable in-memory limit per buffer (default 5000); disk spill ring file; async load on scroll-up; height cache with invalidation on resize/font change
- [ ] `LayoutManager`: split view (vertical split of two buffers minimum)
- [ ] Full tab completion: nick, channel, command; configurable completion order
- [ ] Multi-line paste detection with confirmation prompt
- [ ] NickServ/ChanServ auto-identify: configurable per-server; triggered on `NOTICE` from services nick
- [ ] Bouncer/ZNC support:
  - Detection heuristics (vendor caps, `BOUNCER` ISUPPORT, history-on-join pattern)
  - `chathistory` replay with original timestamps and visual "--- History ---" separator
  - `znc.in/*` vendor capability handling
  - ZNC `*status` pseudo-user messages routed to `ServerStatus` buffer
- [ ] DCC CHAT: `DCCChat` buffer type with clear visual "direct connection" indicator
- [ ] DCC over SOCKS5 proxy; passive/reverse DCC; NAT traversal (configurable external IP + UPnP attempt)
- [ ] Raw/debug buffer; Highlights aggregate buffer
- [ ] Configuration import/export: full JSON export; optional credential encryption (AES-256-GCM + user passphrase)

---

### Phase 5 — Native Plugin System and Hardening
*Extensibility for power users; production-quality robustness.*

- [ ] Out-of-process plugin host: each native plugin runs in a separate OS process; crash cannot affect main app; host process restarted with backoff on failure
- [ ] Plugin manifest: JSON with `name`, `version`, `api_version`, `permissions[]`, `signature`
- [ ] Plugin signature verification on load; unsigned plugins require explicit per-plugin user override
- [ ] ABI versioning: refuse plugins with incompatible `api_version`
- [ ] Permission prompt on first load: user sees declared permission list before plugin activates
- [ ] Full native plugin API surface: event subscribe/unsubscribe, IRC send, buffer read/write, settings read/write, logging
- [ ] Accessibility: accessible names and roles on all controls; full keyboard navigation; high-contrast theme support; screen reader compatibility
- [ ] Performance profiling: scrollback virtualization benchmarks under 50k-message buffers; event bus throughput under multi-server load
- [ ] Security audit: sandbox escape paths in Lua; TLS cert validation policy; file path sanitization in DCC; SOCKS5 DNS leak verification; plugin permission enforcement

---

## Feature Priority Tiers

| Tier | Features |
|------|---------|
| **Must ship (Phases 0–3)** | Multi-server connect/disconnect, TLS + SASL, full IRCv3 caps, channel/query buffers, nicklist, logging, notifications, DCC file transfer, server list, aliases, flood control |
| **Must ship (Phase 4)** | Lua scripting, scrollback virtualization, tab completion, bouncer/ZNC support, DCC CHAT |
| **Must ship (Phase 5)** | Native plugins, accessibility, plugin crash isolation + signing |
| **Post-1.0** | Cross-device sync, AI/automation hooks, bot framework, plugin discovery registry |

---

## Open Questions

| # | Question | Raised | Notes |
|---|----------|--------|-------|
| 1 | ~~Binary distribution format~~ | 2026-05-29 | Resolved — see Decisions Log |
| 2 | Update mechanism | 2026-05-29 | Self-update, OS package manager, or manual only |
| 3 | Plugin discovery / repository | 2026-05-29 | Central registry vs. user-managed only |
| 4 | Log format migration tooling | 2026-05-29 | If format changes post-1.0, need a migration tool; decide before 1.0 ships |
| 5 | mSL migration documentation scope | 2026-05-29 | Decide scope before Phase 4 ships |

---

## Decisions Log

| Date | Decision | Rationale | Alternatives Considered |
|------|----------|-----------|-------------------------|
| 2026-05-30 | GPL v3 as project license | Copyleft protection prevents proprietary forks while keeping the project fully open; no commercial dual-licensing planned | AGPL v3: network-service clause is irrelevant to a desktop client and adds unnecessary complexity for contributors. MIT/Apache 2.0: too permissive - no requirement for forks to contribute changes back. |
| 2026-05-29 | C# (.NET 8+) + Avalonia UI | Native rendering on all three platforms without a web engine; strong async model for IRC I/O; managed memory safety at plugin boundaries; MoonSharp for Lua scripting; SslStream for TLS with client cert support | **Rust + Tauri**: rejected — Tauri renders UI in a WebView, making IRC text rendering and scrollback virtualization poor fits. **C++ + Qt**: rejected — manual memory management creates use-after-free risk at plugin and script boundaries; build complexity across three platforms. |
| 2026-05-29 | Lua (MoonSharp) as scripting language | Lightweight, embeddable, pure-C# interpreter with well-defined sandbox API; widely understood by IRC power users | mIRC Script Language: no mature embeddable interpreter exists; custom DSL: too much implementation cost for no user benefit |
| 2026-05-29 | Distribution formats | macOS: `.dmg`. Windows: MSI or EXE installer. Linux: DEB and RPM native packages as primary targets, plus AppImage, Flatpak, and Snap for broader distro coverage. | Portable zip: rejected as primary format — too manual for end users. Homebrew/winget: considered as supplementary channels, not primary. |
| 2026-05-29 | Event bus as sole inter-component channel | Prevents tightly coupled spaghetti; makes scripting and plugin hooks composable; one documented exception (read-only `IRCStateQuery`) for synchronous state reads | Direct object references between layers: rejected — historically the source of IRC client desync bugs and untestable code |
| 2026-05-29 | Out-of-process plugin host for native plugins | A segfault in a native plugin cannot crash the main application | In-process with AppDomain isolation: insufficient on .NET 8 (AppDomain isolation is largely removed); no isolation at all: unacceptable |
| 2026-05-29 | SQLite FTS5 for log search | Already a dependency for config; FTS5 is built into SQLite; no additional binary required; handles millions of rows adequately | Lucene.NET: heavier dependency; custom index: maintenance burden |

---

## Known Issues and Technical Debt

| Issue | Severity | Deferred Because |
|-------|----------|-----------------|
| Scrollback virtualization not in Phase 2 | Medium | Correctness first; virtualization adds significant complexity; deferred to Phase 4 when the message model is stable |
| UPnP for DCC NAT traversal is best-effort | Low | UPnP support varies widely across routers; passive/reverse DCC is the reliable fallback |

---

## Dependencies and Blockers

- MoonSharp must support the resource limit APIs needed for the CPU/memory sandbox — verify before Phase 4 begins
- Avalonia accessibility tree completeness on Linux (ATK/AT-SPI) should be evaluated before committing to Phase 5 accessibility scope

---

## Future Ideas

- Cross-device sync (requires sync protocol design, conflict resolution, E2E encryption)
- AI/automation hooks (event bus already supports this; needs API surface definition)
- Built-in bot framework (distinct from scripting; persistent, headless operation)
- Plugin discovery registry (central or federated)
- IRCv3 `draft/reacts` and threading UI (event model already captures these tags)
