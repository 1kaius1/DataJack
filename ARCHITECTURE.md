# Architecture

* [1. Stack Decision](#1-stack-decision)
* [2. High-level architecture](#2-high-level-architecture)
* [3. Threading model](#3-threading-model)
* [4. IRC Core (Protocol Engine)](#4-irc-core-protocol-engine)
* [5. Event Dispatcher](#5-event-dispatcher)
* [6. UI Layer](#6-ui-layer)
* [7. Scripting System](#7-scripting-system)
* [8. Plugin System (native extensions)](#8-plugin-system-native-extensions)
* [9. State Management](#9-state-management)
* [10. Networking Layer](#10-networking-layer)
* [11. DCC](#11-dcc)
* [12. Logging and Persistence](#12-logging-and-persistence)
* [13. Command System](#13-command-system)
* [14. Configuration System](#14-configuration-system)
* [15. Notification System](#15-notification-system)
* [16. Bouncer and ZNC Support](#16-bouncer-and-znc-support)
* [17. Module Breakdown](#17-module-breakdown)

---

# 1. Stack Decision

**Chosen stack: C# (.NET 10) with Avalonia UI**

This is not a deferral. The stack choice is a foundational constraint that every other architectural decision inherits, so it is resolved here first.

### Why C# + Avalonia

| Concern | Rationale |
|---------|-----------|
| Cross-platform rendering | Avalonia renders natively on Linux (X11/Wayland), Windows (DirectX/Direct2D), and macOS (Metal/CoreGraphics). It does not embed a web engine, so text rendering, custom draw, and scrollback virtualization are fully controllable. |
| Async model | C#'s `async`/`await` with `System.Threading.Channels` is a natural fit for IRC's asynchronous I/O model. |
| Memory safety | No manual memory management. Plugin crashes in isolated AppDomains or separate processes do not corrupt the host. |
| TLS/networking | `System.Net.Security.SslStream` covers TLS 1.2/1.3, SNI, client certificates, and custom validation. |
| Lua embedding | `MoonSharp` is a mature, MIT-licensed Lua 5.2 interpreter in pure C# with a well-defined sandbox API. |
| Ecosystem | NuGet has mature libraries for SQLite (Microsoft.Data.Sqlite), full-text search (Lucene.NET or SQLite FTS5), notifications, spell checking, and compression. |

### Why not the alternatives

**Rust + Tauri**: Tauri renders the UI inside a system WebView (WebKit/Edge). This means text rendering, mIRC color codes, custom scrollback virtualization, and pixel-level theming are implemented in HTML/CSS/JS. Tight IRC-specific rendering is a poor fit for a web renderer. Rust's async ecosystem is still maturing for GUI applications.

**C++ + Qt**: Viable and battle-tested (HexChat uses it). Rejected because manual memory management introduces use-after-free risks at plugin and script boundaries, build systems are more complex across three platforms, and modern C++ async is far less ergonomic than C#'s `await`.

---

# 2. High-level architecture

```
+--------------------------------------------------------------+
|                        UI Layer                              |
|  (buffers, tabs, rendering, input, themes, server list)      |
+--------------------------------------------------------------+
         |                                        ^
    user actions                             display events
         v                                        |
+--------------------------------------------------------------+
|                    Event Dispatcher                          |
|   (async message bus — the ONLY inter-layer channel)         |
+--------------------------------------------------------------+
     ^           ^           ^           ^
     |           |           |           |
+--------+  +--------+  +--------+  +--------+
|  IRC   |  |Scripts |  |Plugins |  |  DCC   |
|  Core  |  | (Lua)  |  |(native)|  | Engine |
+--------+  +--------+  +--------+  +--------+
     |
+--------------------------------------------------------------+
|                  Platform Abstraction                        |
|  (networking, TLS, filesystem, notifications, spell check)   |
+--------------------------------------------------------------+
```

Key correction from the original diagram: **Scripts and Plugins are peers of IRC Core at the event bus level.** They are not a layer between the UI and the bus. Every consumer of the bus is equal; none sits above another.

**The central rule:** Every cross-component communication — IRC Core to UI, UI to IRC Core, plugin to UI, script to IRC — goes through the Event Dispatcher. No layer holds a reference to another layer's objects. This is what prevents the tightly coupled spaghetti that kills IRC client codebases.

**One necessary exception:** Read-only synchronous state queries. A plugin asking "what is my current nick?" cannot go through an async event round-trip. The `IRCStateModel` exposes a read-only query interface directly. All writes still go through events. This distinction is documented in [Section 9](#9-state-management).

---

# 3. Threading model

IRC is asynchronous by nature. The threading model must be explicit or the event-bus design cannot be implemented correctly.

### Thread roles

| Thread | Owns | Notes |
|--------|------|-------|
| **UI thread** | All Avalonia controls and layout | Only thread allowed to mutate UI state |
| **Network I/O thread(s)** | One per connection; async TCP/TLS receive loop | Reads raw bytes, reconstructs lines, posts `RawLineReceived` to the bus |
| **Event dispatch thread** | Event queue processing, state model writes | Single-threaded; processes one event at a time to avoid state corruption |
| **Script thread pool** | Lua VM execution | Bounded pool; scripts run off the event thread to avoid blocking it |
| **Log writer thread** | Append-only log writes | Dedicated thread with a blocking queue to avoid blocking the dispatch thread |

### Event flow through threads

```
Network I/O thread
  → receives raw line
  → posts RawLineReceived to Channel<Event> (lock-free, non-blocking)

Event dispatch thread (runs the consumer loop on the Channel<Event>)
  → parses raw line → IRCParser
  → updates IRCStateModel (single-writer; all reads are concurrent-safe via snapshot copy)
  → dispatches typed event to all registered handlers

Handlers that are UI components
  → marshalled to UI thread via Dispatcher.UIThread.InvokeAsync()

Handlers that are Lua scripts
  → posted to script thread pool via bounded Channel<ScriptInvocation>
  → result/output events from scripts are posted back to the event bus

Handlers that are native plugins
  → called synchronously on the event dispatch thread by default
  → plugins that declare themselves "async" get a task posted to the thread pool
```

### Backpressure and queue limits

`System.Threading.Channels.Channel<T>` with `BoundedChannelOptions` is used for all inter-thread queues. If the event queue exceeds its capacity, the network I/O thread blocks. If the script invocation queue is full, the dispatched event is dropped with a `ScriptInvocationDropped` warning event. These limits must be configurable per-connection.

### State model thread safety

`IRCStateModel` uses a single-writer / multiple-reader pattern (via `ReaderWriterLockSlim` or immutable snapshot copies). The event dispatch thread is the only writer. All other threads — UI, scripts, plugins — read only through the snapshot or query interface. This eliminates the class of bugs where a plugin reads channel state while the disconnect handler is writing it.

---

# 4. IRC Core (Protocol Engine)

Pure logic layer with no UI awareness. All output is events posted to the bus.

### 4.1 Character encoding

IRC has no mandatory encoding. The parser operates at the byte level and applies encoding per-connection or per-channel:

- Default assumption: UTF-8 with fallback detection
- Configurable per-server: UTF-8, Latin-1, CP1252, ISO-8859-15
- Per-channel overrides stored in config
- Detection strategy: attempt UTF-8 decode; on failure fall back to the configured server encoding
- All outbound strings are encoded in the configured encoding for that connection
- The parser never silently drops bytes; invalid sequences are replaced with U+FFFD and a `EncodingWarning` event is emitted

### 4.2 IRCv3 capability negotiation

The `CapabilityNegotiator` component manages the CAP LS/REQ/ACK lifecycle. The following capabilities are explicitly supported and each has specific handling:

| Capability | Impact |
|-----------|--------|
| `message-tags` | Parser extracts tag dict from every message; passed through to all events |
| `batch` | Parser accumulates batch-tagged messages; emits a single `BatchReceived` event |
| `labeled-response` | Correlates responses to outbound commands; enables echo-message dedup |
| `chathistory` | Drives the bouncer/history replay path (see Section 16) |
| `server-time` | All displayed timestamps use the `time` tag value, not local wall clock |
| `away-notify` | Drives `UserAwayChanged` events without polling |
| `chghost` | Emits `UserHostChanged`; updates state model without re-WHO |
| `echo-message` | Deduplicates outbound messages confirmed by the server |
| `extended-join` | Emits `UserJoined` with account name populated |
| `invite-notify` | Emits `InviteReceived` for third-party invites |
| `monitor` | Used for nick online/offline tracking; drives `MonitorStatusChanged` |
| `multi-prefix` | Nicklist must handle multiple mode prefixes per user |
| `sasl` | Drives the SASL authentication flow |
| `account-notify` | Emits `UserAccountChanged` events |
| `setname` | Emits `UserRealNameChanged` events |
| `cap-notify` | Handles capability additions/removals at runtime |
| `draft/reply` | Threaded message support; stored in message metadata |
| `draft/react` | Reaction support; stored in message metadata |

Capabilities are negotiated in a single CAP LS 302 round trip. Unknown capabilities are ignored. The negotiator handles partial ACK (server grants a subset of requested caps).

### 4.3 SASL authentication

Supported mechanisms, in preference order:

| Mechanism | Notes |
|-----------|-------|
| `SCRAM-SHA-512` | Preferred. Mutual authentication; password never sent in plaintext. |
| `SCRAM-SHA-256` | Fallback if 512 unavailable. |
| `EXTERNAL` | Certificate-based. Requires client certificate configured in TLS settings. |
| `PLAIN` | Final fallback. Only permitted if the connection is TLS-encrypted; refused on plaintext connections. |

The `SASLAuthenticator` component handles the full challenge-response exchange. On `AUTHENTICATE` timeout or failure, a `SASLFailed` event is emitted before the connection is dropped or continues (per user config: abort vs. continue).

### 4.4 Flood control

Token bucket per connection. Not a bullet point — an actual design:

```
FloodController
  ├── token_capacity: f64        // max burst (default: 10.0)
  ├── token_drain_rate: f64      // tokens per second refill (default: 2.0)
  ├── current_tokens: f64        // current available tokens
  └── queue: VecDeque<PendingLine>
```

Rules:
- Each line sent costs 1.0 token plus 0.1 per additional 100 bytes over 200 bytes
- Priority lanes: PONG, PING, QUIT, and NOTICE from server bypass the bucket entirely
- CTCP replies are lowest priority (behind normal messages)
- If the queue exceeds a configurable depth, excess messages are dropped with a `FloodQueueFull` event
- Capacity and drain rate are configurable per-server

### 4.5 Auto-reconnect

The `ReconnectController` manages reconnection with exponential backoff:

```
initial_delay:   2 seconds
multiplier:      2.0
max_delay:       5 minutes
jitter:          ±20% of computed delay
max_attempts:    configurable (default: unlimited)
```

On reconnect:
1. TCP/TLS connection is re-established
2. CAP negotiation re-runs from scratch (server may have changed)
3. SASL re-authenticates
4. All previously joined channels are re-joined (from the state snapshot)
5. `ReconnectSucceeded` event is emitted with a list of rejoined channels

On permanent failure (max attempts exhausted): `ReconnectFailed` event is emitted and the server is moved to disconnected state.

### 4.6 Key components

**IRCConnection**
- Wraps `System.Net.Security.SslStream` / `NetworkStream`
- Reads raw bytes, reconstructs newline-delimited lines
- Emits: `RawLineReceived`
- Handles: PING/PONG at this level (before the event bus) to prevent latency-induced disconnections

**IRCParser**
- Parses IRCv3 message format: `[@tags] [:prefix] command [params] [:trailing]`
- Extracts tag dictionary (key=value pairs, vendor prefixes)
- Handles BATCH accumulation
- Emits: structured typed events (see complete event list in Section 5)

**IRCStateModel**
- Single-writer (event dispatch thread); snapshot-readable by all other threads
- Tracks: servers, channels, users per channel, modes, topic + who/time, away status, account names
- Provides synchronous read-only query interface (not through the event bus)

**IRCCommandRouter**
- Converts user commands (`/join`, `/msg`, `/kick`) into correctly formatted IRC protocol lines
- Validates arguments before sending (e.g. channel name format, nick validity)
- Handles command aliases (see Section 13)

**CapabilityNegotiator**
- Manages CAP LS 302 → CAP REQ → CAP ACK/NAK lifecycle
- Tracks active capabilities; re-negotiates on `cap-notify` changes

**SASLAuthenticator**
- SCRAM-SHA-256/512 implementation with server proof verification
- EXTERNAL: passes client cert fingerprint
- PLAIN: gated behind TLS check

---

# 5. Event Dispatcher

### 5.1 Event model

Events have three properties beyond their data:

**Priority** — processed in order within the same dispatch cycle:
1. `Critical` — errors, disconnects, PING responses
2. `Normal` — messages, joins, parts (default)
3. `Low` — WHOIS replies, ban lists, MODE floods

**Cancellation** — any handler (plugin or script) may cancel an event before it reaches later handlers. Cancellation prevents the event from reaching the UI layer, but does not prevent it from reaching handlers at the same or lower priority level that have already received it. This is how mIRC's `halt` and `haltdef` are modelled.

**Mutation** — handlers may mutate the event's content fields (e.g. alter message text before display). Mutations are applied in handler registration order. This enables script-driven message filtering, highlight injection, and content transformation.

### 5.2 Complete event vocabulary

This is the contract for the entire system. New events must be added here before implementing them.

**Connection events**
```
ConnectionAttempted        (server, address, port, tls)
ConnectionEstablished      (server)
ConnectionFailed           (server, reason)
ConnectionClosed           (server, reason)
ReconnectScheduled         (server, delay_seconds, attempt_number)
ReconnectSucceeded         (server, rejoined_channels[])
ReconnectFailed            (server, reason)
RawLineReceived            (server, line)
RawLineSent                (server, line)
```

**Registration events**
```
CapabilityNegotiated       (server, granted[], denied[])
SASLStarted                (server, mechanism)
SASLSucceeded              (server)
SASLFailed                 (server, reason)
WelcomeReceived            (server, nick)
MOTDReceived               (server, text)
MOTDEnd                    (server)
ServerCapabilityChanged    (server, added[], removed[])
```

**Channel events**
```
JoinedChannel              (server, channel, nick, account?)
PartedChannel              (server, channel, nick, reason?)
KickReceived               (server, channel, kicked_nick, kicker_nick, reason)
TopicChanged               (server, channel, new_topic, setter_nick)
TopicWhoTime               (server, channel, setter_nick, set_at)
ChannelModeChanged         (server, channel, modes, params[], setter)
ChannelCreated             (server, channel, created_at)
InviteReceived             (server, channel, from_nick)
```

**User events**
```
NickChanged                (server, old_nick, new_nick)
NickInUse                  (server, nick)
UserQuit                   (server, nick, reason)
UserHostChanged            (server, nick, new_user, new_host)
UserAwayChanged            (server, nick, is_away, message?)
UserAccountChanged         (server, nick, account?)
UserRealNameChanged        (server, nick, new_realname)
MonitorStatusChanged       (server, nick, is_online)
WhoReplyEntry              (server, channel?, nick, user, host, account?, realname)
WhoIsReply                 (server, nick, user, host, realname, server_name, idle_seconds, account?)
WhoIsEnd                   (server, nick)
BanListEntry               (server, channel, mask, setter, set_at)
BanListEnd                 (server, channel)
```

**Message events**
```
MessageReceived            (server, channel_or_nick, from_nick, text, tags{}, is_self)
NoticeReceived             (server, channel_or_nick, from_nick, text, tags{})
ServerNoticeReceived       (server, text)
CTCPRequest                (server, from_nick, command, params?)
CTCPReply                  (server, from_nick, command, params?)
WallopsReceived            (server, from_nick, text)
ActionReceived             (server, channel_or_nick, from_nick, text, tags{})
BatchReceived              (server, batch_type, batch_id, messages[])
```

**Authentication / error events**
```
PrivilegeError             (server, command, reason)
ErrorReceived              (server, message)
EncodingWarning            (server, raw_bytes, applied_encoding)
FloodQueueFull             (server, dropped_count)
ScriptInvocationDropped    (script_name, event_type)
```

**DCC events** — see Section 11.

### 5.3 Synchronous state queries

Plugins and scripts frequently need synchronous answers without going through the async event bus. These are satisfied through the `IRCStateQuery` interface, which is a read-only view of the state model snapshot:

```
IRCStateQuery
  ├── GetCurrentNick(server) → string
  ├── GetChannelUsers(server, channel) → User[]
  ├── GetChannelModes(server, channel) → ModeSet
  ├── GetChannelTopic(server, channel) → Topic
  ├── GetUserModes(server, channel, nick) → ModeSet
  ├── IsConnected(server) → bool
  └── GetActiveCapabilities(server) → string[]
```

This interface is explicitly read-only. No writes are permitted through it. This is the one allowed bypass of the event bus, and its scope is strictly limited to reads.

---

# 6. UI Layer

### 6.1 Buffer model

Every displayable surface is a buffer. Buffers are typed:

| Buffer type | Description |
|-------------|-------------|
| `ServerStatus` | Per-server status window; connection messages, MOTD |
| `NetworkStatus` | Aggregated view across all servers (HexChat-style) |
| `Channel` | A joined IRC channel |
| `Query` | Private message conversation |
| `DCCChat` | DCC CHAT session |
| `Notices` | Aggregated server notices |
| `RawLog` | Raw protocol lines for debugging |
| `Highlights` | Aggregated view of all messages matching highlight patterns |

### 6.2 Scrollback virtualization

The `MessageView` component maintains a virtual message list. This is non-trivial and must be designed explicitly:

- At most `scrollback_memory_limit` messages (configurable, default 5000) are held in memory per buffer
- Older messages are spilled to the per-buffer log on disk (see Section 12)
- Scroll-up beyond the in-memory window triggers an async log read that appends the retrieved messages to the virtual list
- The virtual list renders only the visible rows plus a configurable overscan, not all messages; row heights are cached after first render
- Font size changes and window resizes invalidate the height cache and trigger a re-measure pass

### 6.3 IRC text rendering

The `IRCTextRenderer` component handles all IRC text formatting before display:

- **mIRC color codes**: `\x03fg[,bg]` pairs; all 99 defined color codes
- **Formatting codes**: `\x02` bold, `\x1D` italic, `\x1F` underline, `\x1E` strikethrough, `\x11` monospace, `\x16` reverse video, `\x0F` reset
- **IRCv3 hex color**: `\x04RRGGBB[,RRGGBB]`
- **URL detection**: regex-based with known-scheme allowlist (`irc://`, `ircs://`, `https://`, `http://`, `ftp://`); displayed as clickable links
- **Channel mentions**: `#channel` patterns auto-linked to join or switch to that buffer
- **Nick mentions**: nick-coloring based on consistent hash of the nick string

### 6.4 UI components

**BufferManager**
- Creates and destroys buffers on join/part/connect/disconnect
- Maps IRC events to the correct buffer
- Manages the buffer ordering in the tab/tree view

**MessageView** (per buffer)
- Virtual scrollback list
- IRC text rendering via `IRCTextRenderer`
- Timestamp display (using `server-time` tag when available)
- Selection and copy (plain text; strips formatting codes)

**InputBox**
- Command vs. message detection (`/` prefix)
- Per-buffer history (Up/Down arrow); circular buffer, configurable depth
- Tab completion: nick completion, channel completion, command completion, configurable completion order
- Multi-line paste detection with warning prompt

**NicklistPanel**
- Users grouped by highest mode prefix: `~` owners, `&` admins, `@` ops, `%` halfops, `+` voiced, (none)
- Within each group, case-insensitive alphabetical sort
- Supports `multi-prefix` (a user can appear in one group but display all their prefixes)
- Right-click context menu: whois, query, op, deop, kick, ban, ignore

**LayoutManager**
- Tab bar (HexChat-style)
- Tree view (mIRC-style, server → channel hierarchy)
- Split view (configurable; at minimum vertical split of two buffers)
- Layout preference is persisted per-session

**ServerListDialog**
- Network/server address book with favorites
- Fields per entry: network name, server address(es), port, TLS on/off, password, preferred nick, username, realname, SASL credentials, auto-join channels, connect commands
- Import/export in a documented JSON format

**ThemeManager**
- Themes are directories containing a `theme.json` descriptor
- `theme.json` defines: the 16 IRC color palette, UI chrome colors, font family, font sizes, timestamp format, and print event format strings
- Themes are hot-reloaded without restart when the theme directory is modified
- A built-in default theme is compiled into the binary as a fallback

### 6.5 Spell checking

Spell checking in the `InputBox` is platform-delegated:

| Platform | Backend |
|----------|---------|
| Windows | `Windows.Data.Text.TextPredictionGenerator` / WinRT spell check API |
| macOS | `NSSpellChecker` via P/Invoke bridge |
| Linux | `Enchant-2` library (supports Hunspell, Aspell, Nuspell backends) |

Misspelled words are underlined. Right-click shows suggestions. Spell checking is skipped on `/` command lines.

### 6.6 Accessibility

- All interactive controls expose accessible names and roles via the platform accessibility tree
- Full keyboard navigation: tab order, focus indicators, keyboard-driven buffer switching
- High-contrast theme support: the theme system treats the OS high-contrast flag as a theme override
- Screen reader compatibility is a first-class requirement, not an afterthought

---

# 7. Scripting System

### 7.1 Lua via MoonSharp

The scripting engine embeds MoonSharp (Lua 5.2 compatible, pure C#). Each script file gets its own Lua VM instance. Scripts communicate with the application only through the defined API bridge — they hold no references to internal objects.

### 7.2 Sandbox definition

"Sandboxed" is not a label. These are the explicit boundaries:

**Blocked entirely (not exposed to the Lua VM):**
- `io.*`, `file.*` — no arbitrary file system access
- `os.execute`, `os.exit`, `io.popen` — no shell execution
- `require` (replaced with a controlled loader; only approved modules load)
- `debug.*` — no VM introspection
- Raw socket access of any kind — scripts may not open network connections directly; all network goes through the `irc.*` API

**Permitted with limits:**
- `math.*`, `string.*`, `table.*` — standard; no restrictions
- `os.time`, `os.date`, `os.clock` — read-only time access
- `print()` — redirected to the script's own output buffer; not stdout

**Resource limits (enforced by MoonSharp's `ScriptExecutionContext`):**
- CPU: maximum instruction count per event handler call (default: 1,000,000 steps); scripts exceeding this are aborted with `ScriptCPULimitExceeded`
- Memory: MoonSharp's allocator is monitored; scripts exceeding the configured heap limit (default: 16 MB) are aborted with `ScriptMemoryLimitExceeded`
- Execution time: a CancellationToken with a wall-clock timeout (default: 2 seconds) is passed to every script invocation

**Isolation between scripts:**
- Script VMs do not share global state; one script cannot read another's variables
- Scripts can only see message content for buffers they are explicitly granted access to (granted by user, not by the script itself)

### 7.3 Script API surface

```lua
-- Event hooks
on(event_name, handler_function)           -- register a global event handler
on_channel(channel, event_name, handler)   -- register a channel-scoped handler
on_server(server, event_name, handler)     -- register a server-scoped handler

-- Event control (within a handler)
event.cancel()           -- prevent event from reaching later handlers and UI
event.halt_display()     -- prevent UI display but allow later script handlers
event.set_text(str)      -- mutate displayed text of the current message event

-- IRC actions
irc.join(channel)
irc.part(channel, reason)
irc.msg(target, text)
irc.notice(target, text)
irc.raw(line)            -- send a raw line; rate-limited; cannot send QUIT
irc.nick(new_nick)

-- UI
ui.print(buffer, text)             -- print to a specific buffer
ui.print_active(text)              -- print to the active buffer
ui.focus_buffer(buffer)            -- switch active buffer
ui.get_active_buffer() → string    -- returns current buffer name

-- State queries (read-only)
irc.state.current_nick(server) → string
irc.state.channel_users(server, channel) → table
irc.state.is_connected(server) → bool

-- Timers
timer.create(name, delay_ms, callback)
timer.cancel(name)

-- Storage (scoped to the script)
store.set(key, value)
store.get(key) → value
store.delete(key)

-- Custom commands
command.register(name, handler)    -- registers /name as a user command
command.unregister(name)
```

### 7.4 Hook priority and event cancellation

Handlers are called in registration order within the same priority level. `event.cancel()` stops propagation to handlers registered after the cancelling handler, and prevents the event from reaching the UI. It does not affect handlers that already ran. This models mIRC's `halt`/`haltdef` behavior directly.

---

# 8. Plugin System (native extensions)

### 8.1 Plugin types

| Type | Safety | Use case |
|------|--------|---------|
| Native (.dll/.so/.dylib) | Trusted | Performance-critical integrations; deep OS access |
| Script (Lua) | Sandboxed | Automation, bots, UI customization |

### 8.2 Native plugin isolation

Native plugins are trusted code, but trust does not mean unmitigated risk. Compensating controls:

**Crash isolation**: Native plugins run in a separate OS process with a remoted plugin host. If the plugin process crashes, the main application logs the failure, emits a `PluginCrashed` event, and continues. The plugin host process is restarted on the next plugin call with a backoff.

**Plugin signing**: Plugins must be signed with a certificate. The application verifies the signature on load. Unsigned plugins are rejected by default; a user override allows loading unsigned plugins with an explicit per-plugin prompt.

**ABI versioning**: The plugin API is versioned. A plugin declares the API version it was compiled against in its manifest. If the declared version is incompatible with the running application, the plugin is refused with a `PluginVersionMismatch` event. The API follows semantic versioning; patch bumps are always backward-compatible.

**Permission declaration**: Every plugin declares, in its manifest, which API surfaces it requires. The application presents these to the user on first load. A plugin that calls an API surface it did not declare is terminated immediately.

### 8.3 Plugin manifest format (JSON)

```json
{
  "name": "example-plugin",
  "version": "1.0.0",
  "api_version": "1",
  "description": "Example native plugin",
  "author": "Author Name",
  "signature": "base64-encoded-signature",
  "permissions": ["irc.send", "buffer.read", "settings.read"]
}
```

Available permission tokens: `irc.send`, `irc.raw`, `buffer.read`, `buffer.write`, `settings.read`, `settings.write`, `log.read`, `filesystem.read`, `filesystem.write`, `notifications`.

### 8.4 Plugin API surface

```csharp
// Event subscription
void Subscribe(string eventName, EventHandler handler);
void Unsubscribe(string eventName, EventHandler handler);

// IRC send API
void SendMessage(string server, string target, string text);
void SendRaw(string server, string line);   // rate-limited

// Buffer API
void PrintToBuffer(string server, string buffer, string text);
BufferSnapshot ReadBuffer(string server, string buffer);

// State query
IRCStateQuery GetState();

// Settings API
string GetSetting(string key);
void SetSetting(string key, string value);

// Logging API
void Log(LogLevel level, string message);
```

### 8.5 Plugin lifecycle

```
discover → validate_manifest → verify_signature → check_permissions (prompt user) →
start_host_process → load → init → register_hooks → running → unload → terminate_host_process
```

---

# 9. State Management

### 9.1 IRC world state tree

```
IRCWorld
 ├── Servers[]
 │    ├── address, port, tls
 │    ├── connected_at, registered_nick, username
 │    ├── active_capabilities[]
 │    ├── isupport_tokens{}
 │    ├── Channels[]
 │    │    ├── name, topic, topic_setter, topic_set_at, created_at
 │    │    ├── modes (ModeSet)
 │    │    └── Users[]
 │    │         ├── nick, user, host, account?, realname?
 │    │         ├── channel_modes (mode prefixes)
 │    │         └── away_message?
 │    ├── Queries[]
 │    │    └── nick, user?, host?
 │    └── MonitoredNicks[]
 │         └── nick, is_online
 └── DCC[]
      └── see Section 11
```

### 9.2 Write path

Every state change originates from an event processed on the event dispatch thread. The state model is the only writer. This means:
- No UI component writes to the state model directly
- No plugin writes to the state model directly; they emit events that the IRC core processes and reflects into state
- State is always consistent after any event cycle completes

### 9.3 Read path

Any thread may read state through `IRCStateQuery` (see Section 5.3). Reads are served from an immutable snapshot that is replaced atomically after each event cycle. This means reads are always consistent and non-blocking; they may observe state that is one event cycle stale, which is acceptable.

### 9.4 Why unified state matters

Without this model, every component tracks its own fragment of channel state. When a nick changes, you must update the nicklist, all open queries, the tab labels, the log headers, the ignore list matches, and every script's idea of who is in what channel. With unified state, the event fires once, the state model updates once, and every reader gets the new snapshot.

---

# 10. Networking Layer

Never bind IRC logic to sockets directly. The `NetworkProvider` interface is the only way the IRC core initiates or uses connections.

### 10.1 NetworkProvider interface

```
NetworkProvider
 ├── TCP (plaintext; disableable for security-conscious deployments)
 ├── TLS (SslStream wrapping TCP)
 └── SOCKS5 (wraps TCP or TLS; supports username/password auth)
```

Tor support is achieved through the SOCKS5 provider pointed at the local Tor SOCKS port.

### 10.2 TLS policy

| Setting | Value |
|---------|-------|
| Minimum TLS version | TLS 1.2; TLS 1.3 preferred |
| SNI | Always sent (required for shared hosting) |
| Certificate validation | Full chain validation by default |
| Self-signed handling | User prompted on first connection; fingerprint is stored and pinned; subsequent connections verify against the pinned fingerprint |
| Expired certificate | Shown as a warning; user may accept or reject; not silently accepted |
| Client certificates | Loaded from a PEM/PKCS#12 file specified in per-server config; used for SASL EXTERNAL |
| Certificate errors | Emits `TLSCertificateError` event; never silently bypassed |

### 10.3 IPv6

IPv6 is supported. Address family preference is configurable per-server: `prefer_ipv6` (default), `prefer_ipv4`, `ipv6_only`, `ipv4_only`. DNS resolution returns both A and AAAA records; the preference setting controls which is tried first. Happy-eyeballs-style parallel connection attempts are used if the preferred family fails within 250ms.

### 10.4 SOCKS5 proxy configuration

Proxy settings are per-server (not global), allowing some servers to go through Tor and others to connect directly. Proxy DNS (remote DNS resolution) is always used when a proxy is configured, preventing DNS leaks.

---

# 11. DCC

DCC (Direct Client-to-Client) is a major feature set, not a future consideration.

### 11.1 DCC capabilities

| Feature | Notes |
|---------|-------|
| DCC SEND | Outbound file offer to a nick |
| DCC RECV | Accept an incoming file offer |
| DCC RESUME | Resume an interrupted transfer at byte offset |
| DCC CHAT | Direct plaintext chat session |
| Passive/Reverse DCC | Sender provides a token; receiver initiates the connection; works behind NAT |
| DCC over proxy | File transfers can be routed through the configured SOCKS5 proxy |

### 11.2 DCC state tree

```
DCCSession
 ├── id (UUID)
 ├── type (Send | Receive | Chat)
 ├── peer_nick
 ├── peer_address
 ├── peer_port
 ├── status (Pending | Active | Paused | Completed | Failed)
 ├── filename?
 ├── file_size?
 ├── bytes_transferred
 ├── transfer_rate
 └── error_message?
```

### 11.3 DCC events

```
DCCOfferReceived    (session_id, peer_nick, type, filename?, file_size?)
DCCOfferSent        (session_id, peer_nick, type, filename?)
DCCStarted          (session_id)
DCCProgress         (session_id, bytes_transferred, transfer_rate)
DCCCompleted        (session_id, bytes_transferred)
DCCFailed           (session_id, reason)
DCCChatMessageReceived (session_id, text)
DCCChatMessageSent     (session_id, text)
```

### 11.4 Security controls

- Incoming DCC SEND offers do not auto-accept. The user is always prompted.
- Accepted files are saved to a configurable download directory. The filename from the CTCP message is sanitized (path traversal stripped; null bytes rejected; length capped).
- The configurable download directory defaults to the platform downloads folder; relative paths in filenames cannot escape it.
- DCC CHAT sessions are displayed in a `DCCChat` buffer type with a clear visual indicator that it is a direct connection, not an IRC server channel.
- File type warnings: executable file extensions (.exe, .bat, .sh, .py, .js, and others) trigger an additional confirmation prompt.

### 11.5 NAT traversal

For passive DCC (when the local machine is behind NAT):
- The application supports a configurable external IP/hostname for advertising in CTCP DCC messages
- UPnP port mapping is attempted if configured and the router supports it
- If neither is configured, passive DCC defaults to the reverse DCC protocol (receiver-initiated)

---

# 12. Logging and Persistence

### 12.1 Log format

Logs are per-buffer, stored as line-delimited files in a configurable log directory. Each line is:

```
[ISO8601_TIMESTAMP] <TAB> [NICK_OR_SOURCE] <TAB> [MESSAGE_TYPE] <TAB> [TEXT]
```

This format is directly grep-able. Binary formats add complexity without compensating benefit for log files that users routinely inspect with external tools.

### 12.2 Log structure

```
LogManager
 ├── BufferLogWriter      -- appends log lines; one file per buffer per day
 ├── Indexer              -- maintains a SQLite FTS5 index for full-text search
 ├── ArchiveManager       -- compresses logs older than configurable age (gzip/zstd)
 └── ExportManager        -- exports a date range to plain text or HTML
```

### 12.3 Search index

Full-text search uses SQLite FTS5 (built into .NET's `Microsoft.Data.Sqlite`). The index stores:

- Timestamp
- Server + buffer
- Nick
- Message text (FTS5-indexed)

Search queries support: simple terms, quoted phrases, `nick:` prefix filter, `server:` prefix filter, date range filters. Results are returned paginated to avoid loading millions of rows into memory.

### 12.4 Scrollback spill

When a buffer's in-memory message count exceeds the `scrollback_memory_limit`, the oldest messages are written to disk (they may already be in the log; the spill is into a separate fast-access ring file). On scroll-up past the in-memory window, the ring file is read asynchronously and the result is prepended to the virtual list.

---

# 13. Command System

All slash commands are routed through a single `CommandRouter`. Command lookup is case-insensitive.

```
CommandRouter
 ├── built-in commands     (immutable; defined by the application)
 ├── alias commands        (user-defined; stored in config; can chain and parameterize)
 ├── script commands       (registered by Lua scripts via command.register())
 └── plugin commands       (registered by native plugins)
```

Priority order on name conflict: built-in > plugin > script > alias. Built-in commands cannot be overridden.

### Built-in command coverage (minimum for HexChat/mIRC parity)

`/join`, `/part`, `/quit`, `/msg`, `/notice`, `/query`, `/nick`, `/me`, `/ctcp`,
`/mode`, `/op`, `/deop`, `/voice`, `/devoice`, `/kick`, `/ban`, `/unban`, `/kickban`,
`/invite`, `/topic`, `/away`, `/back`, `/whois`, `/who`, `/ignore`, `/unignore`,
`/server`, `/connect`, `/disconnect`, `/reconnect`, `/list`, `/names`, `/raw`,
`/quote`, `/charset`, `/dcc`, `/dns`, `/ping`, `/set`, `/alias`, `/load`, `/unload`,
`/exec` (if permitted), `/timer`, `/help`

### Alias format

```
/alias weather /msg #weather %1   -- %1 = first argument, %* = all arguments
```

Aliases are stored in config and available immediately without restart.

---

# 14. Configuration System

### 14.1 Schema and versioning

Configuration is stored in a versioned JSON file. The root object contains a `schema_version` integer. On startup, if the on-disk version is lower than the current version, migration functions are run in sequence to bring it up to date. Migration functions are one-way and non-destructive (old values are preserved under a `_deprecated` key for one major version before removal).

### 14.2 Configuration scopes

Settings exist at three scopes, with narrower scopes overriding wider ones:

```
Global defaults (compiled in)
  └── User global settings  (~/.config/datajack/settings.json or platform equivalent)
        └── Per-server overrides  (keyed by server network name)
              └── Per-channel overrides  (keyed by server + channel name)
```

Example: default encoding is UTF-8 globally, but `irc.libera.chat` is set to UTF-8, while `#legacy-channel` on that server is set to Latin-1.

### 14.3 Settings categories

- **Identity**: nick, alt_nick, username, realname (global; per-server override)
- **Servers**: the server address book (see Section 6.4 ServerListDialog)
- **Appearance**: active theme, font, timestamps, nick colors, scrollback limit
- **Logging**: log directory, log enable/disable per buffer type, compression settings
- **Notifications**: highlight patterns, notification enable/disable, platform-specific settings
- **Scripting**: scripts directory, enabled scripts list
- **Plugins**: enabled plugins list
- **DCC**: download directory, auto-accept rules, external IP/UPnP settings
- **Encoding**: default encoding, per-server encoding overrides
- **Proxy**: SOCKS5 host/port/credentials (per-server)
- **Advanced**: flood control parameters, reconnect parameters, scrollback limits

### 14.4 Import and export

The full configuration (including server list and settings, excluding credentials by default) can be exported to a single JSON file and imported on another machine or installation. Credentials (passwords, SASL secrets) are excluded from export unless the user explicitly opts in, in which case they are encrypted with a user-supplied passphrase (AES-256-GCM).

---

# 15. Notification System

Desktop notifications for highlights and private messages are fundamental to usability. This requires platform-specific backends behind a common `NotificationService` interface.

### 15.1 Platform backends

| Platform | Backend |
|----------|---------|
| Windows 10/11 | WinRT `Windows.UI.Notifications.ToastNotificationManager` |
| Windows 7/8 | System tray balloon tip via `NotifyIcon` (fallback) |
| macOS 10.14+ | `UNUserNotificationCenter` via P/Invoke |
| macOS < 10.14 | `NSUserNotification` (deprecated but functional) |
| Linux | `org.freedesktop.Notifications` D-Bus interface (libnotify protocol) |

### 15.2 Notification triggers

Configurable per-type:
- Highlighted message (nick or custom pattern matched)
- Private message received
- DCC offer received
- Watched nick comes online (via `monitor`)

### 15.3 Highlight pattern matching

Highlight patterns support:
- Literal strings (case-insensitive by default)
- Wildcards (`*`, `?`)
- Regular expressions (opt-in per pattern)
- The user's current nick is always an implicit highlight pattern

---

# 16. Bouncer and ZNC Support

Many power users connect through a bouncer (ZNC, soju, Ergo's built-in bouncer). The client must not feel broken in this scenario.

### 16.1 Bouncer detection

The client detects bouncer behavior through:
- Presence of `znc.in/` vendor capabilities
- `soju.im/` vendor capabilities
- `draft/chathistory` + server sending pre-JOIN history on connect
- The `BOUNCER` ISUPPORT token (proposed IRCv3 extension)

### 16.2 History replay

When `chathistory` is available:
- On joining a channel or reconnecting, the client requests history since the last seen message (stored per-buffer as a `last_seen_msgid` using the `msgid` tag)
- History messages are tagged with the `server-time` tag and displayed with their original timestamps, not the current time
- History messages are visually differentiated (dimmed or preceded by a separator line "--- History ---")
- The history injected into the buffer is not re-logged (it is already in the bouncer's log)

### 16.3 ZNC-specific handling

- `znc.in/playback` capability handled if `chathistory` is unavailable
- `znc.in/self-message` used to correctly attribute outbound messages replayed by ZNC
- ZNC's `*status` pseudo-user is recognized and its messages are displayed in the `ServerStatus` buffer rather than opening a query

---

# 17. Module Breakdown

```
/core
  irc/
    connection.cs         -- IRCConnection: raw TCP/TLS I/O
    parser.cs             -- IRCParser: byte stream → typed events
    encoder.cs            -- outbound line serialization + charset
    command_router.cs     -- IRCCommandRouter: user commands → protocol
    flood_control.cs      -- FloodController: token bucket
    reconnect.cs          -- ReconnectController: backoff logic
    sasl/
      scram.cs            -- SCRAM-SHA-256/512
      external.cs         -- SASL EXTERNAL
      plain.cs            -- SASL PLAIN (TLS-gated)
    caps/
      negotiator.cs       -- CapabilityNegotiator
      handlers/           -- one file per capability
  state/
    model.cs              -- IRCStateModel: single-writer state tree
    query.cs              -- IRCStateQuery: read-only snapshot interface
    snapshot.cs           -- immutable snapshot type
  events/
    bus.cs                -- EventDispatcher: Channel<Event> consumer loop
    types.cs              -- all event struct definitions
    priority.cs           -- priority queue logic
  protocol/
    dcc/
      engine.cs           -- DCC session management
      transfer.cs         -- file transfer I/O
      chat.cs             -- DCC CHAT session
      nat.cs              -- passive DCC / UPnP

/ui
  buffers/
    manager.cs            -- BufferManager
    types.cs              -- buffer type definitions
  rendering/
    message_view.cs       -- virtual scrollback list
    irc_text_renderer.cs  -- mIRC color codes, formatting, URL detection
    nicklist.cs           -- NicklistPanel
  layout/
    layout_manager.cs     -- tabs, tree, splits
    input_box.cs          -- InputBox: completion, history, paste detection
  dialogs/
    server_list.cs        -- ServerListDialog
    dcc_manager.cs        -- DCC transfer list UI
    settings.cs           -- Settings dialog
  themes/
    manager.cs            -- ThemeManager: load, hot-reload
    schema.cs             -- theme.json type definitions

/scripting
  lua/
    engine.cs             -- ScriptEngine: MoonSharp VM management
    sandbox.cs            -- sandbox construction: blocked modules, resource limits
    api/
      irc_api.cs          -- irc.* bindings
      ui_api.cs           -- ui.* bindings
      state_api.cs        -- irc.state.* bindings
      timer_api.cs        -- timer.* bindings
      store_api.cs        -- store.* bindings
      command_api.cs      -- command.register() binding

/plugins
  loader.cs               -- manifest parsing, signature verification, ABI check
  host_process.cs         -- out-of-process plugin host
  api/
    event_api.cs          -- Subscribe/Unsubscribe
    irc_api.cs            -- SendMessage, SendRaw
    buffer_api.cs         -- PrintToBuffer, ReadBuffer
    settings_api.cs       -- GetSetting, SetSetting
    log_api.cs            -- Log()

/net
  provider.cs             -- NetworkProvider interface
  tcp.cs                  -- TCP transport
  tls.cs                  -- TLS transport: SslStream, cert validation, pinning
  socks5.cs               -- SOCKS5 proxy transport
  ipv6.cs                 -- dual-stack / happy-eyeballs

/storage
  logs/
    writer.cs             -- BufferLogWriter: append-only per-buffer logs
    indexer.cs            -- SQLite FTS5 index
    archive.cs            -- compression, rotation
    export.cs             -- date-range export
    spill.cs              -- scrollback ring file
  config/
    schema.cs             -- config type definitions + schema_version
    migrations/           -- one file per schema version increment
    loader.cs             -- read/write + migration runner
    scopes.cs             -- global / server / channel scope resolution

/platform
  notifications/
    service.cs            -- NotificationService interface
    windows.cs            -- WinRT backend
    macos.cs              -- UNUserNotification backend
    linux.cs              -- D-Bus / libnotify backend
  spell/
    service.cs            -- SpellCheckService interface
    windows.cs            -- WinRT backend
    macos.cs              -- NSSpellChecker backend
    linux.cs              -- Enchant-2 backend
  paths.cs                -- platform config/data/log directory resolution
```

---

# The central rule (unchanged, but now properly bounded)

> **Every cross-component notification flows through the event dispatcher. Nothing bypasses it.**

The bounded exception: read-only synchronous state queries go through `IRCStateQuery` directly. No writes, ever.

That rule, plus the threading model in Section 3, is what keeps this codebase maintainable as it grows.
