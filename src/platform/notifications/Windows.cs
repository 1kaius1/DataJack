// SPDX-License-Identifier: GPL-3.0-or-later
// Windows desktop notification backend via PowerShell WinRT interop.
// Target API: Windows.UI.Notifications.ToastNotificationManager (WinRT).
// The PowerShell script approach is used because DataJack targets net10.0 (cross-platform)
// rather than net10.0-windows, so WinRT C# projections are not available at compile time.
// A direct WinRT COM binding retargeted to net10.0-windows is planned for a future phase.
// See ARCHITECTURE.md §15.1.

using System.Diagnostics;
using System.Runtime.Versioning;

namespace DataJack.Platform.Notifications;

/// <summary>
/// Delivers desktop toast notifications on Windows 10/11 via a PowerShell script that
/// invokes the WinRT <c>Windows.UI.Notifications.ToastNotificationManager</c>.
/// Requires PowerShell 5.1+ and Windows 10 or later.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNotificationService : INotificationService
{
    /// <inheritdoc/>
    public bool IsSupported => true;

    /// <inheritdoc/>
    public Task NotifyAsync(NotificationInfo notification, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        try
        {
            string title = EscapePs(notification.Title);
            string body  = EscapePs(notification.Body);

            // Load WinRT types via PowerShell type accelerators (available without extra NuGet packages).
            // Using single-quoted PS strings so only '' needs escaping.
            string script = $"""
                [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType=WindowsRuntime] | Out-Null
                $xml = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)
                $xml.SelectSingleNode('//text[@id="1"]').InnerText = '{title}'
                $xml.SelectSingleNode('//text[@id="2"]').InnerText = '{body}'
                $toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
                [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('DataJack').Show($toast)
                """;

            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = false,
                RedirectStandardError  = false,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);

            using var _ = Process.Start(psi);
        }
        catch
        {
            // Silently swallow: PowerShell unavailable, WinRT not accessible, permission denied, etc.
        }
        return Task.CompletedTask;
    }

    // In PowerShell single-quoted strings, a literal single quote is written as ''.
    private static string EscapePs(string s) => s.Replace("'", "''");
}
