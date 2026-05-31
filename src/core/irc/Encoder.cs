// SPDX-License-Identifier: GPL-3.0-or-later
// Outbound line serialization and character set handling. Phase 1 placeholder.
// IRCConnection currently handles encoding inline; this module will own structured
// outbound serialization (prefix, command, params → wire bytes) once IRCCommandRouter
// is implemented and the encode/decode boundary is finalised.

namespace DataJack.Core.Irc;
