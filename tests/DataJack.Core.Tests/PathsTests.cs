// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Platform;
using Xunit;

namespace DataJack.Core.Tests;

public sealed class PathsTests
{
    [Fact]
    public void ConfigDirectory_IsAbsolutePath()
    {
        Assert.True(Path.IsPathRooted(Paths.ConfigDirectory),
            $"Expected an absolute path but got: {Paths.ConfigDirectory}");
    }

    [Fact]
    public void ConfigDirectory_ContainsAppNameSegment()
    {
        // The leaf segment must identify this application, case-insensitively.
        Assert.Contains("datajack", Paths.ConfigDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LogDirectory_IsSubdirectoryOfConfigDirectory()
    {
        string expected = Paths.ConfigDirectory + Path.DirectorySeparatorChar;
        Assert.StartsWith(expected, Paths.LogDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void PluginsDirectory_IsSubdirectoryOfConfigDirectory()
    {
        string expected = Paths.ConfigDirectory + Path.DirectorySeparatorChar;
        Assert.StartsWith(expected, Paths.PluginsDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptsDirectory_IsSubdirectoryOfConfigDirectory()
    {
        string expected = Paths.ConfigDirectory + Path.DirectorySeparatorChar;
        Assert.StartsWith(expected, Paths.ScriptsDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void ThemesDirectory_IsSubdirectoryOfConfigDirectory()
    {
        string expected = Paths.ConfigDirectory + Path.DirectorySeparatorChar;
        Assert.StartsWith(expected, Paths.ThemesDirectory, StringComparison.Ordinal);
    }
}
