# DataJack

A first-class IRC client for Linux, macOS, and Windows with full feature parity with HexChat and mIRC.

<!-- Badges - replace OWNER/REPO with the real repository path -->
![Version](https://img.shields.io/github/v/release/1kaius1/DataJack)
![License](https://img.shields.io/badge/license-GPL--3.0--or--later-blue)
![Build](https://img.shields.io/github/actions/workflow/status/1kaius1/DataJack/ci.yml)

---

## Overview

DataJack is a cross-platform IRC client built for power users. It provides the scripting depth of mIRC, the multi-server tabbed workflow of HexChat, and full IRCv3 protocol support, all in a single compiled binary that runs natively on Linux, macOS, and Windows without a web engine or interpreter.

<!-- Screenshot placeholder - replace with a real screenshot before first release -->
<!--
![Screenshot](docs/screenshot.png)
-->

---

## Features

- **Multi-server** - connect to any number of IRC networks simultaneously, with a HexChat-style tab bar or a mIRC-style tree sidebar (switch with `/layout tree` or `/layout tabs`)
- **IRCv3** - full capability negotiation: `message-tags`, `batch`, `labeled-response`, `chathistory`, `server-time`, `away-notify`, `monitor`, `sasl`, and more
- **SASL authentication** - SCRAM-SHA-512, SCRAM-SHA-256, EXTERNAL (client certificates), and PLAIN (TLS-only)
- **Lua scripting** - sandboxed MoonSharp engine; hook events, send commands, register custom `/commands`, automate anything
- **Native plugins** - out-of-process plugin host; plugins cannot crash the main application
- **DCC** - file transfers (SEND/RECV/RESUME), DCC CHAT, passive/reverse DCC for NAT traversal
- **Bouncer support** - ZNC and soju detection, `chathistory` replay, `znc.in/*` capability handling
- **Full logging** - per-buffer append-only logs with SQLite FTS5 full-text search
- **mIRC formatting** - all color codes (0-99), bold, italic, underline, strikethrough, monospace, reverse, IRCv3 hex color
- **Themes** - fully customizable color palette, fonts, and print event formats via `theme.json`
- **Spell checking** - platform-native backends (WinRT, NSSpellChecker, Enchant-2)
- **Desktop notifications** - highlight and PM notifications via platform-native APIs
- **SOCKS5 / Tor** - per-server proxy support with remote DNS to prevent leaks
- **Flood control** - configurable token bucket with priority lanes; avoids G-lines
- **Auto-reconnect** - exponential backoff with automatic channel rejoin

---

## Requirements

### Pre-built packages

No runtime dependencies. All packages are self-contained.

### Building from source

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- git

No native toolkit libraries are required. Avalonia bundles its own rendering layer.

Linux packaging scripts additionally require: `dpkg-dev` (DEB), `rpm-build` (RPM).

---

## Installation

### Pre-built package

**Linux**

```bash
# DEB (Debian, Ubuntu, Mint)
sudo dpkg -i datajack_amd64.deb

# RPM (Fedora, openSUSE, RHEL)
sudo rpm -i datajack_x86_64.rpm

# AppImage (no installation required)
chmod +x DataJack-x86_64.AppImage
./DataJack-x86_64.AppImage

# Flatpak
flatpak install flathub io.github.1kaius1.DataJack

# Snap
snap install datajack
```

**macOS**

Open `DataJack.dmg` and drag DataJack to Applications.

**Windows**

Run `DataJack_setup.exe` and follow the installer.

---

### From source

**All platforms**

```bash
git clone https://github.com/1kaius1/DataJack.git
cd DataJack
dotnet build DataJack.slnx -c Release
dotnet run --project src/DataJack
```

**Self-contained binary**

```bash
# Linux x64
dotnet publish src/DataJack -c Release -r linux-x64 --self-contained -o dist/linux-x64

# macOS arm64 (Apple Silicon)
dotnet publish src/DataJack -c Release -r osx-arm64 --self-contained -o dist/osx-arm64

# Windows x64
dotnet publish src/DataJack -c Release -r win-x64 --self-contained -o dist/win-x64
```

See `scripts/package/` for distribution packaging scripts.

---

## Quick Start

1. Launch DataJack
2. Open the server list (Network menu or `Ctrl+S`)
3. Add a network - enter the server address, port, and your preferred nick
4. Click Connect
5. Use `/join #channel` to join a channel, or configure auto-join channels in the server list

---

## Usage

### Scripting

Place Lua scripts in the scripts directory (`~/.config/datajack/scripts/` on Linux, `%APPDATA%\DataJack\scripts\` on Windows, `~/Library/Application Support/DataJack/scripts/` on macOS).

```lua
-- Example: respond to greetings
on("MessageReceived", function(msg)
    if msg.text:find("hello") then
        irc.msg(msg.channel, "Hi, " .. msg.nick .. "!")
    end
end)
```

Scripts are loaded on startup. Use `/load scriptname.lua` to load a script at runtime and `/unload scriptname.lua` to remove it.

### Custom commands

```lua
command.register("weather", function(args)
    irc.msg(irc.state.current_nick(), "Fetching weather for: " .. args)
end)
-- Usage: /weather London
```

### DCC file transfer

To send a file: right-click a nick in the nicklist and choose "Send File", or use `/dcc send nick /path/to/file`.

Incoming offers appear as a prompt in the DCC manager. Accepted files are saved to the configured download directory.

### Keyboard Shortcuts

| Action | Linux / Windows | macOS |
|--------|----------------|-------|
| Next buffer | `Alt+Down` | `Cmd+Down` |
| Previous buffer | `Alt+Up` | `Cmd+Up` |
| Jump to buffer N | `Alt+N` | `Cmd+N` |
| Server list | `Ctrl+S` | `Cmd+S` |
| Close buffer | `Ctrl+W` | `Cmd+W` |
| Clear buffer | `Ctrl+L` | `Cmd+L` |
| Search logs | `Ctrl+F` | `Cmd+F` |

---

## Configuration

Settings are stored in `settings.json` in the platform data directory:

| Platform | Path |
|----------|------|
| Linux | `~/.config/datajack/settings.json` |
| macOS | `~/Library/Application Support/DataJack/settings.json` |
| Windows | `%APPDATA%\DataJack\settings.json` |

Most settings are available through the preferences dialog. The file is human-readable JSON and can be edited directly while DataJack is not running.

---

## License

Copyright (C) 2026 DataJack Contributors

This program is free software: you can redistribute it and/or modify it under the
terms of the GNU General Public License as published by the Free Software Foundation,
either version 3 of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful, but WITHOUT ANY
WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
PARTICULAR PURPOSE. See the GNU General Public License for more details.

See [LICENSE](LICENSE) for the full license text.
