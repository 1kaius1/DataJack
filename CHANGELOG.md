# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
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
