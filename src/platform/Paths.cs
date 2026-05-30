// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace DataJack.Platform;

/// <summary>
/// Resolves platform-appropriate directory paths for config, logs, plugins, scripts, and themes.
/// Returns absolute paths only. Does not create directories -- callers are responsible for that.
/// </summary>
public static class Paths
{
    private static readonly string _configRoot = ResolveConfigRoot();

    /// <summary>Root config directory. All other DataJack paths are subdirectories of this.</summary>
    public static string ConfigDirectory => _configRoot;

    /// <summary>Directory for per-buffer log files and the SQLite FTS5 search index.</summary>
    public static string LogDirectory => Path.Combine(_configRoot, "logs");

    /// <summary>Directory for installed native plugins.</summary>
    public static string PluginsDirectory => Path.Combine(_configRoot, "plugins");

    /// <summary>Directory for user Lua scripts.</summary>
    public static string ScriptsDirectory => Path.Combine(_configRoot, "scripts");

    /// <summary>Directory for user-installed themes.</summary>
    public static string ThemesDirectory => Path.Combine(_configRoot, "themes");

    private static string ResolveConfigRoot()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "DataJack");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "DataJack");
        }

        // Linux and other Unix: follow XDG Base Directory Specification.
        // Only accept XDG_CONFIG_HOME if it is set and is an absolute path; a relative
        // value would be ambiguous and is disallowed by the spec.
        string xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? string.Empty;
        if (!string.IsNullOrEmpty(xdgConfigHome) && Path.IsPathRooted(xdgConfigHome))
            return Path.Combine(xdgConfigHome, "datajack");

        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userHome, ".config", "datajack");
    }
}
