// SPDX-License-Identifier: GPL-3.0-or-later
// macOS desktop notification backend via osascript (AppleScript).
// Target API: UNUserNotificationCenter (UserNotifications.framework). The osascript
// implementation is the Phase 3 delivery vehicle; a native UNUserNotificationCenter
// P/Invoke binding is planned for a future phase when the app is code-signed with the
// com.apple.security.application-groups entitlement required by UNUserNotificationCenter.
// See ARCHITECTURE.md §15.1.

using System.Diagnostics;
using System.Runtime.Versioning;

namespace DataJack.Platform.Notifications;

/// <summary>
/// Delivers desktop notifications on macOS via <c>osascript</c> using the AppleScript
/// <c>display notification</c> command (available on macOS 10.9+).
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacosNotificationService : INotificationService
{
    /// <inheritdoc/>
    public bool IsSupported => true;

    /// <inheritdoc/>
    public Task NotifyAsync(NotificationInfo notification, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        try
        {
            // AppleScript string literals use double quotes; escape any in the content.
            string title = EscapeAppleScript(notification.Title);
            string body  = EscapeAppleScript(notification.Body);
            string script = $"display notification \"{body}\" with title \"{title}\" subtitle \"DataJack\"";

            var psi = new ProcessStartInfo("osascript")
            {
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = false,
                RedirectStandardError  = false,
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(script);

            using var _ = Process.Start(psi);
        }
        catch
        {
            // Silently swallow: osascript unavailable, notification permission denied, etc.
        }
        return Task.CompletedTask;
    }

    // Escape backslashes first, then double quotes, so the resulting string is safe
    // to embed inside an AppleScript double-quoted string literal.
    private static string EscapeAppleScript(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
