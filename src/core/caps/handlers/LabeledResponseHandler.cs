// SPDX-License-Identifier: GPL-3.0-or-later

namespace DataJack.Core.Caps.Handlers;

/// <summary>
/// Tracks the <c>labeled-response</c> capability and generates label values for outgoing commands.
///
/// When labeled-response is active, the client may prefix any command with a
/// <c>@label=value</c> tag. The server echoes back the same label tag on the response,
/// making it possible to correlate a response with the command that triggered it. This
/// enables reliable echo-message dedup and request/response sequencing.
///
/// Phase 3 scope: capability tracking and label generation only.
/// Full correlation (attaching a <see cref="Task{T}"/> to each label and resolving it
/// when the labeled response arrives) is a Phase 4 refinement that requires hooking into
/// the <see cref="BatchHandler"/> for <c>labeled-response</c> batch types.
/// </summary>
public sealed class LabeledResponseHandler
{
    private readonly CapabilityRegistry _registry;
    private int _labelCounter;

    public LabeledResponseHandler(CapabilityRegistry registry) => _registry = registry;

    /// <summary>True when the <c>labeled-response</c> capability is currently active.</summary>
    public bool IsActive => _registry.IsActive("labeled-response");

    /// <summary>
    /// Generates a short unique label string suitable for use as an IRCv3 message tag value.
    /// Labels are lowercase hex integers, unique within this handler instance per connection.
    /// Returns null when labeled-response is not active (avoids sending unsupported tags).
    /// </summary>
    public string? TryCreateLabel()
    {
        if (!IsActive) return null;
        int n = Interlocked.Increment(ref _labelCounter);
        return n.ToString("x");
    }
}
