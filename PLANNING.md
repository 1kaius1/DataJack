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

**Status:** Phase 1 (IRC Core) complete. Phase 2 (Minimal Viable UI) is next.

Architecture is documented and finalized in [ARCHITECTURE.md](ARCHITECTURE.md). Stack is decided (C# .NET 10 + Avalonia 12). All Phase 1 components are implemented and tested: networking, connection, parsing, capability negotiation, SASL authentication, flood control, reconnect with exponential backoff, IRC command routing, and the complete event vocabulary.

---

## Stack

**C# (.NET 8+) with Avalonia UI.** Documented and justified in ARCHITECTURE.md §1.

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

- [ ] `BufferManager`: create/destroy buffers; all buffer types from ARCHITECTURE.md §6.1
- [ ] `MessageView`: non-virtualized scrollback (virtualization is Phase 4); `IRCTextRenderer` (mIRC color codes 0–99, bold, italic, underline, strikethrough, monospace, reverse, hex color, URL detection, nick coloring)
- [ ] `InputBox`: command vs. message detection; per-buffer history (Up/Down); nick tab completion
- [ ] `NicklistPanel`: grouped by mode prefix (owners → admins → ops → halfops → voiced → none); `multi-prefix` support; right-click context menu
- [ ] `LayoutManager`: tab bar (HexChat-style) only — tree view and splits are Phase 4
- [ ] `ThemeManager`: load `theme.json` from disk; built-in default theme compiled in as fallback
- [ ] Basic `ServerListDialog`: add/edit/delete server entries; auto-connect on launch
- [ ] `BufferLogWriter`: append-only per-buffer log files; tab-delimited format (timestamp, nick, type, text)
- [ ] Configuration system: scoped settings (global → server → channel); schema v1; read/write; versioned migrations

---

### Phase 3 — HexChat Feature Parity
*A daily-driveable client matching HexChat's feature set.*

- [ ] Full IRCv3 capability handlers (`/core/caps/handlers/` — one file per capability)
- [ ] `server-time` tag used for all displayed timestamps
- [ ] `monitor` capability for nick online/offline tracking; `MonitorStatusChanged` events
- [ ] Full `IRCStateModel`: topic + who/time, away status, account names, ISUPPORT tokens
- [ ] Full built-in command set (ARCHITECTURE.md §13): `/kick`, `/ban`, `/mode`, `/op`, `/voice`, `/whois`, `/who`, `/ignore`, `/list`, `/names`, `/timer`, `/help`, etc.
- [ ] Alias system: `/alias` command; `%1`/`%*` substitution; stored in config
- [ ] `ServerListDialog`: complete — all fields (SASL credentials, auto-join, connect commands); import/export JSON
- [ ] `NotificationService`: highlight and PM desktop notifications on all three platforms
  - Windows: WinRT `ToastNotificationManager`
  - macOS: `UNUserNotificationCenter`
  - Linux: `org.freedesktop.Notifications` D-Bus interface
- [ ] Highlight pattern matching: literal (case-insensitive), wildcard, regex; current nick always implicit
- [ ] Log search: SQLite FTS5 index; `nick:`, `server:`, date range filters; paginated results
- [ ] Log archive: compression (gzip/zstd); configurable rotation age; `ExportManager` (plain text and HTML)
- [ ] SOCKS5 proxy transport; per-server proxy config; remote DNS resolution (no DNS leaks)
- [ ] DCC SEND and DCC RECV: file path sanitization (no traversal, no null bytes); executable file type warnings; configurable download directory
- [ ] DCC RESUME: transfer restart at byte offset
- [ ] `LayoutManager`: tree view (mIRC-style server → channel hierarchy)
- [ ] Spell checking: platform-specific backends
  - Windows: WinRT spell check API
  - macOS: `NSSpellChecker`
  - Linux: Enchant-2 (Hunspell/Aspell/Nuspell backends)
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
