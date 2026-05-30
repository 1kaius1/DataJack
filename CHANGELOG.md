# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
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
