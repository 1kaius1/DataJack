// SPDX-License-Identifier: GPL-3.0-or-later
// Linux desktop notification backend via notify-send (org.freedesktop.Notifications D-Bus wrapper).
// notify-send is the standard CLI tool shipped with libnotify and available on all major
// Linux desktop environments. Each call spawns a short-lived subprocess; delivery failures
// (binary not found, D-Bus session unavailable) are silently swallowed.
// See ARCHITECTURE.md §15.1.

using System.Diagnostics;
using System.Runtime.Versioning;

namespace DataJack.Platform.Notifications;

/// <summary>
/// Delivers desktop notifications on Linux via <c>notify-send</c>, which forwards the
/// call to the <c>org.freedesktop.Notifications</c> D-Bus interface provided by the
/// running desktop environment.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxNotificationService : INotificationService
{
    /// <inheritdoc/>
    public bool IsSupported => true;

    /// <inheritdoc/>
    public Task NotifyAsync(NotificationInfo notification, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        try
        {
            // Pass title and body as separate arguments to avoid any shell-injection risk.
            var psi = new ProcessStartInfo("notify-send")
            {
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = false,
                RedirectStandardError  = false,
            };
            psi.ArgumentList.Add(notification.Title);
            psi.ArgumentList.Add(notification.Body);
            psi.ArgumentList.Add("--app-name=DataJack");
            psi.ArgumentList.Add("--expire-time=5000");
            psi.ArgumentList.Add($"--icon={NotificationIcon(notification.Kind)}");

            using var _ = Process.Start(psi);
        }
        catch
        {
            // Silently swallow: notify-send not installed, D-Bus session unavailable, etc.
        }
        return Task.CompletedTask;
    }

    private static string NotificationIcon(NotificationKind kind) => kind switch
    {
        NotificationKind.PrivateMessage  => "dialog-information",
        NotificationKind.Highlight       => "emblem-important",
        NotificationKind.DccOffer        => "document-save",
        NotificationKind.WatchedNickOnline => "user-available",
        _                                => "dialog-information",
    };
}
