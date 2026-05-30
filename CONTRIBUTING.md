# Contributing

Thank you for your interest in contributing. Please read this document before
opening an issue or submitting a pull request.

---

## Licensing and the Contributor Agreement

This project is licensed under the
[GNU General Public License v3.0 or later](LICENSE).

By submitting a contribution you agree that:

- Your contribution is your original work and you have the right to license it
- Your contribution is submitted under the GPL-3.0-or-later license
- You have read and accept the terms above

If your contribution is on behalf of an employer or involves work that may be
subject to an IP agreement, ensure you have the right to contribute before submitting.

---

## Code of Conduct

Be direct, technical, and respectful. Contributions are evaluated on their technical
merit. Maintainers reserve the right to close issues or pull requests that are
off-topic, abusive, or not aligned with the project goals.

---

## Before You Start

- Check open issues and pull requests to avoid duplicating work
- For significant changes, open an issue first to discuss the approach before writing code
- For bug fixes, a brief issue description is enough - you do not need to wait for
  approval before submitting a fix for a clearly reproducible bug
- For new features, wait for maintainer acknowledgment before investing significant time

---

## Development Environment

### Prerequisites

```bash
# .NET 8 SDK or later: https://dotnet.microsoft.com/download/dotnet/8.0
# git
```

### Setup

```bash
# Clone the repository
git clone https://github.com/1kaius1/DataJack.git
cd DataJack

# Restore dependencies and build
dotnet build DataJack.sln

# Verify setup - all tests should pass
dotnet test DataJack.sln
```

---

## Workflow

### 1. Create a Branch

Branch off `master`. Use the naming convention `type/short-description`:

```bash
git checkout master
git pull origin master
git checkout -b feat/add-output-flag
```

Branch type prefixes mirror the commit types:

| Prefix | Use for |
|--------|---------|
| `feat/` | New features |
| `fix/` | Bug fixes |
| `docs/` | Documentation only |
| `refactor/` | Code changes that neither fix a bug nor add a feature |
| `test/` | Adding or fixing tests |
| `chore/` | Build process, dependency updates, tooling |

### 2. Make Your Changes

- Keep changes focused - one logical change per branch
- Do not mix unrelated changes in a single branch or commit
- Add or update tests for any changed behavior
- Update documentation if behavior changes
- Add a license header to any new source files (see below)
- Update `CHANGELOG.md` under `## [Unreleased]`

### 3. Write Good Commits

Follow the [Conventional Commits](https://www.conventionalcommits.org/) format:

```
type(scope): short imperative description under 72 characters

Optional body explaining WHY this change was made, not WHAT it does.
The code shows what - the commit message explains the reasoning.
Wrap body lines at 72 characters.

Optional footer for breaking changes or issue references:
Fixes #123
BREAKING CHANGE: description of what broke and how to migrate
```

Types: `feat`, `fix`, `docs`, `style`, `refactor`, `test`, `chore`

Examples:

```
feat(output): add --output flag with json and csv formatters

fix(config): handle missing home directory gracefully

docs(readme): update install instructions for macOS

chore(deps): update dependencies to latest patch versions
```

### 4. Run Tests

All tests must pass before opening a pull request:

```bash
dotnet test DataJack.sln
```

If you cannot run the tests, explain why in the pull request description.

### 5. Open a Pull Request

- Target branch: `master`
- Title: use the same format as a commit message (`type(scope): description`)
- Description: explain what changed, why, and how to test it
- Link any related issues (`Fixes #123` or `Relates to #456`)
- Keep pull requests small and focused - large PRs are harder to review and slower to merge

---

## License Headers

Every new source file must include the appropriate SPDX license header as the
first lines of the file:

```csharp
// SPDX-License-Identifier: GPL-3.0-or-later
```

```python
# SPDX-License-Identifier: GPL-3.0-or-later
```

```bash
#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
```

Do not modify the license header of an existing file.

---

## Code Style

### General

- ASCII only in code and comments - no emoji, no em-dashes, no decorative Unicode
- Underscores in all filenames (not hyphens, not spaces)
- Comments explain WHY, not WHAT
- Handle all errors explicitly - no silent failures
- Follow existing patterns in the codebase before introducing new ones

### C#

```bash
# Format
dotnet format DataJack.sln

# Lint (Roslyn analyzers run automatically during build)
dotnet build DataJack.sln -warnaserror
```

### BASH

- `set -euo pipefail` at the top of every script
- Quote all variable expansions: `"${var}"` not `$var`
- Use `[[ ]]` for conditionals, not `[ ]`

---

## Changelog

Update `CHANGELOG.md` in every pull request. Add your changes under `## [Unreleased]`
in the appropriate section:

- `Added` - new features
- `Changed` - changes to existing behavior
- `Deprecated` - features that will be removed in a future release
- `Removed` - features removed in this release
- `Fixed` - bug fixes
- `Security` - security fixes

Do not create a new version entry - that is the maintainer's responsibility.

---

## Reporting Issues

When reporting a bug, include:

- A clear description of the expected behavior and what actually happened
- Steps to reproduce the issue
- The version of the software (`--version` output)
- Your operating system and version
- Relevant log output (redact any sensitive information)

Feature requests are welcome. Describe the problem you are trying to solve, not just
the solution you have in mind. This helps find the best approach.

---

## What Gets Merged

Contributions are more likely to be merged if they:

- Solve a clearly defined problem
- Are consistent with the project architecture (see ARCHITECTURE.md)
- Include tests for changed behavior
- Are small and focused
- Follow the code style of the existing codebase

Contributions are less likely to be merged if they:

- Add a new dependency without prior discussion
- Refactor code that was not part of the stated change
- Are inconsistent with the project's goals (see PLANNING.md)
- Introduce platform-specific behavior that breaks cross-platform compatibility
