# CLAUDE.md

This file provides project context and conventions for AI-assisted development sessions.
Read this file and ARCHITECTURE.md before making any changes. See .clauderules for
behavioral rules that govern this session.

---

## Project Overview

DataJack is a cross-platform desktop IRC client for Linux, macOS, and Windows. It targets
full feature parity with HexChat and mIRC: multi-server connections, tabbed and tree-view
buffer layouts, a Lua scripting engine, a native plugin system, DCC file transfers, IRCv3
capability support, and a per-buffer logging and search system. It ships as compiled native
binaries with no dependency on a web engine or runtime interpreter.

---

## Tech Stack

| Component | Choice | Reason |
|-----------|--------|--------|
| Language | C# (.NET 8+) | Strong async/await model for IRC I/O; managed memory safety at plugin boundaries; SslStream built in |
| GUI toolkit | Avalonia UI | Native rendering on X11/Wayland, DirectX, and Metal without an embedded web engine |
| Scripting | MoonSharp (Lua 5.2) | Pure C# interpreter; well-defined sandbox API; no native code to compile per platform |
| Config/persistence | JSON via System.Text.Json | Built into .NET 8; no additional dependency; versioned schema with migrations |
| Search index | SQLite FTS5 via Microsoft.Data.Sqlite | Full-text search over log history; single-file database; no separate server |
| Networking | System.Net.Security.SslStream | TLS 1.2/1.3, SNI, client certificates; built into .NET |
| Inter-thread queues | System.Threading.Channels | Lock-free bounded queues with backpressure; built into .NET |

---

## Repository Layout

```
DataJack/
- src/
  - core/               # IRC protocol engine (no UI awareness)
    - irc/              # IRCConnection, IRCParser, IRCCommandRouter
    - state/            # IRCStateModel, IRCStateQuery, snapshot
    - events/           # EventDispatcher, all event type definitions
    - protocol/
      - dcc/            # DCC engine: transfers, chat, NAT
    - caps/             # CapabilityNegotiator + per-capability handlers
    - sasl/             # SCRAM-SHA-512/256, EXTERNAL, PLAIN
  - ui/                 # Rendering and interaction layer only
    - buffers/          # BufferManager, buffer type definitions
    - rendering/        # MessageView, IRCTextRenderer, NicklistPanel
    - layout/           # LayoutManager, InputBox
    - dialogs/          # ServerListDialog, DCC manager, Settings
    - themes/           # ThemeManager, theme.json schema
  - scripting/
    - lua/              # ScriptEngine, sandbox construction
    - api/              # irc.*, ui.*, timer.*, store.*, command.* Lua bindings
  - plugins/            # Native plugin loader, out-of-process host, API surface
  - net/                # NetworkProvider: TCP, TLS, SOCKS5, IPv6
  - storage/
    - logs/             # BufferLogWriter, FTS5 indexer, archive, export, spill
    - config/           # Schema, migrations, scoped loader
  - platform/           # OS abstraction: paths, notifications, spell check
- tests/                # xUnit test suite; mirrors src/ structure
- scripts/              # Build, packaging, and CI helper scripts
  - package/
    - linux/            # DEB, RPM, AppImage, Flatpak, Snap
    - macos/            # .dmg assembly
    - windows/          # MSI/EXE installer
- assets/
  - icons/              # SVG application and toolbar icons
  - themes/             # Bundled default theme (compiled in as fallback)
- .clauderules          # AI session behavioral rules
- ARCHITECTURE.md       # Component reference, threading model, event vocabulary
- CHANGELOG.md          # Version history (Keep a Changelog format)
- CLAUDE.md             # This file
- CONTRIBUTING.md       # Contribution guidelines
- PLANNING.md           # Goals, milestones, open questions, decisions log
- README.md             # Public-facing documentation
- LICENSE               # GPL-3.0-or-later
- DataJack.sln          # Solution file
```

---

## Build and Install

### Prerequisites

- .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
- No native toolkit libraries required - Avalonia bundles its own rendering layer

Linux only (for packaging scripts, not for building or running):
```bash
# DEB packaging
sudo apt install dpkg-dev

# RPM packaging
sudo dnf install rpm-build

# AppImage
# Download appimagetool from https://github.com/AppImage/AppImageKit/releases
```

### Build

```bash
# Restore dependencies and build (debug)
dotnet build DataJack.sln

# Build release
dotnet build DataJack.sln -c Release
```

### Run in Development

```bash
# Run directly
dotnet run --project src/DataJack

# Run with verbose logging
dotnet run --project src/DataJack -- --verbose
```

### Publish Self-Contained Binaries

```bash
# Linux x64
dotnet publish src/DataJack -c Release -r linux-x64 --self-contained -o dist/linux-x64

# Linux arm64
dotnet publish src/DataJack -c Release -r linux-arm64 --self-contained -o dist/linux-arm64

# macOS x64
dotnet publish src/DataJack -c Release -r osx-x64 --self-contained -o dist/osx-x64

# macOS arm64 (Apple Silicon)
dotnet publish src/DataJack -c Release -r osx-arm64 --self-contained -o dist/osx-arm64

# Windows x64
dotnet publish src/DataJack -c Release -r win-x64 --self-contained -o dist/win-x64
```

### Package for Distribution

```bash
# Linux - DEB
scripts/package/linux/build_deb.sh dist/linux-x64

# Linux - RPM
scripts/package/linux/build_rpm.sh dist/linux-x64

# Linux - AppImage
scripts/package/linux/build_appimage.sh dist/linux-x64

# Linux - Flatpak
scripts/package/linux/build_flatpak.sh

# Linux - Snap
scripts/package/linux/build_snap.sh

# macOS - .dmg
scripts/package/macos/build_dmg.sh dist/osx-x64 dist/osx-arm64

# Windows - installer (run on Windows or via Wine)
scripts/package/windows/build_installer.ps1 dist/win-x64
```

---

## Running Tests

```bash
# Run all tests
dotnet test DataJack.sln

# Run with detailed output
dotnet test DataJack.sln --logger "console;verbosity=detailed"

# Run a specific test project
dotnet test tests/DataJack.Core.Tests

# Run with coverage (requires coverlet)
dotnet test DataJack.sln --collect:"XPlat Code Coverage"
```

All tests must pass before committing or opening a pull request. Never suggest a PR with
failing tests. If tests cannot be run, say so explicitly and explain why.

Note: UI rendering tests that require a display should use Avalonia's headless renderer
(`HeadlessUnitTestSession`) rather than Xvfb or a physical display.

---

## Configuration and Data Storage

Configuration paths are resolved by `src/platform/Paths.cs` - never hardcode these:

| Platform | Config directory |
|----------|-----------------|
| Linux | `~/.config/datajack/` |
| macOS | `~/Library/Application Support/DataJack/` |
| Windows | `%APPDATA%\DataJack\` |

- `settings.json` - main config file (versioned schema; see `src/storage/config/Schema.cs`)
- `logs/` - per-buffer log files and SQLite search index
- `plugins/` - installed native plugins
- `scripts/` - user Lua scripts
- `themes/` - user-installed themes

### Config schema

The config root always carries a `schema_version` integer. On startup, `ConfigLoader`
runs migration functions in sequence if the on-disk version is behind the current version.
When adding a new config key, add a corresponding migration in `src/storage/config/Migrations/`.

---

## UI Architecture

The application is a single main window with:
- A tab bar or tree view (left/top) for buffer navigation - user-configurable
- A message view (center) - virtual scrollback list with IRC text rendering
- A nicklist panel (right) - present only for channel buffers
- An input box (bottom) - command and message entry with tab completion
- A status bar (bottom) - connection status, lag indicator, topic snippet

All Avalonia controls are defined in code (no AXAML files for layout). Theming is done
entirely through the `ThemeManager` and `theme.json` - never hardcode colors or fonts
in control code.

### Key UI rules

- The UI layer never calls IRC core directly - all output is via the `EventDispatcher`
- All UI state mutations happen on the Avalonia UI thread (`Dispatcher.UIThread.InvokeAsync`)
- `MessageView` renders only visible rows plus an overscan; it does not hold all messages in memory
- `IRCTextRenderer` handles all mIRC color codes, formatting codes, and URL detection before display
- Never use `Thread.Sleep` or blocking calls on the UI thread

---

## Assets and Resources

- Icons are SVG; provide raster fallbacks at 16, 32, 48, 64, 128, 256px for platforms that require them
- The built-in default theme lives in `assets/themes/default/` and is compiled into the binary as an embedded resource
- Do not embed binary assets directly in source files

---

## Code Style and Conventions

Behavioral rules are in `.clauderules`. Key reminders and C#-specific notes:

- ASCII only in code and comments - no emoji, no em-dashes
- Explain WHY in comments, not WHAT - complex logic gets a comment block, obvious code gets none
- Errors must be handled explicitly - no swallowed exceptions, no empty catch blocks
- Conventional commits: `type(scope): description`

### C#-specific conventions

- C# source files use PascalCase filenames matching their primary class name (e.g. `IrcParser.cs`, `EventDispatcher.cs`) - this is an exception to the general underscore rule in `.clauderules`, which applies to scripts, config files, and non-source assets
- All public types and members have XML doc comments (`///`) with at minimum a `<summary>` line
- `async` methods are suffixed with `Async` (e.g. `ConnectAsync`, `SendLineAsync`)
- `CancellationToken` is the last parameter on every `async` method that performs I/O
- Use `System.Threading.Channels.Channel<T>` for all inter-thread queues - never `BlockingCollection<T>` or raw locks on hot paths
- `IRCStateModel` is the single writer of state; all other code reads through `IRCStateQuery` snapshots
- Events are `readonly record struct` types defined in `src/core/events/Types.cs`
- SPDX header on every new source file: `// SPDX-License-Identifier: GPL-3.0-or-later`

---

## Changelog and Versioning

- Format: Keep a Changelog (https://keepachangelog.com)
- Versioning: Semantic Versioning (https://semver.org)
- CHANGELOG.md must be updated in the same commit as the change
- Unreleased changes go under `## [Unreleased]`
- Do not create a new version entry - that is the maintainer's decision

---

## Current Focus

See PLANNING.md for active goals, open questions, and pending decisions.

Currently in: Phase 0 (Foundation) - no code written yet. Next step is solution scaffolding.
