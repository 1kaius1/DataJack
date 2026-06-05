// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Compression;
using System.Text;
using DataJack.Core.Storage.Logs;
using Xunit;

namespace DataJack.Core.Tests;

/// <summary>
/// Tests for <see cref="LogArchiver"/>: gzip compression, deletion of originals,
/// rotation by age, and handling of edge cases.
/// Each test gets an isolated temporary directory.
/// </summary>
public sealed class LogArchiverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public LogArchiverTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // Write a file and optionally back-date its last-write time.
    private async Task<string> CreateLogAsync(string name, string content = "line\n", int daysOld = 0)
    {
        string path = Path.Combine(_dir, name);
        await File.WriteAllTextAsync(path, content, Encoding.UTF8);
        if (daysOld > 0)
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-daysOld));
        return path;
    }

    // ---------------------------------------------------------------------------
    // ArchiveOldLogsAsync
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ArchiveOldLogs_NonExistentDirectory_ReturnsWithoutError()
    {
        string missing = Path.Combine(_dir, "does_not_exist");
        // Should not throw.
        await LogArchiver.ArchiveOldLogsAsync(missing, maxAgeDays: 30);
    }

    [Fact]
    public async Task ArchiveOldLogs_EmptyDirectory_NoOp()
    {
        await LogArchiver.ArchiveOldLogsAsync(_dir, maxAgeDays: 30);
        Assert.Empty(Directory.EnumerateFiles(_dir));
    }

    [Fact]
    public async Task ArchiveOldLogs_OldFile_IsCompressed()
    {
        await CreateLogAsync("old.log", daysOld: 100);

        await LogArchiver.ArchiveOldLogsAsync(_dir, maxAgeDays: 90);

        Assert.True(File.Exists(Path.Combine(_dir, "old.log.gz")));
    }

    [Fact]
    public async Task ArchiveOldLogs_OldFile_OriginalIsDeleted()
    {
        await CreateLogAsync("old.log", daysOld: 100);

        await LogArchiver.ArchiveOldLogsAsync(_dir, maxAgeDays: 90);

        Assert.False(File.Exists(Path.Combine(_dir, "old.log")));
    }

    [Fact]
    public async Task ArchiveOldLogs_RecentFile_IsNotTouched()
    {
        await CreateLogAsync("recent.log", daysOld: 1);

        await LogArchiver.ArchiveOldLogsAsync(_dir, maxAgeDays: 90);

        Assert.True(File.Exists(Path.Combine(_dir, "recent.log")));
        Assert.False(File.Exists(Path.Combine(_dir, "recent.log.gz")));
    }

    [Fact]
    public async Task ArchiveOldLogs_ExistingGzFile_IsSkipped()
    {
        // A .gz file should never be re-compressed (the pattern "*.log" won't match "*.log.gz").
        string gzPath = Path.Combine(_dir, "already.log.gz");
        await File.WriteAllBytesAsync(gzPath, new byte[] { 0x1f, 0x8b }); // gzip magic
        File.SetLastWriteTimeUtc(gzPath, DateTime.UtcNow.AddDays(-200));

        await LogArchiver.ArchiveOldLogsAsync(_dir, maxAgeDays: 90);

        // The .gz file should still exist and not be double-compressed.
        Assert.True(File.Exists(gzPath));
        Assert.False(File.Exists(gzPath + ".gz"));
    }

    // ---------------------------------------------------------------------------
    // CompressFileAsync
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task CompressFileAsync_CreatesGzFile()
    {
        string path = await CreateLogAsync("sample.log", "Hello, world!");

        await LogArchiver.CompressFileAsync(path);

        Assert.True(File.Exists(path + ".gz"));
    }

    [Fact]
    public async Task CompressFileAsync_OriginalIsDeleted()
    {
        string path = await CreateLogAsync("sample.log", "Hello, world!");

        await LogArchiver.CompressFileAsync(path);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task CompressFileAsync_ContentIsPreserved()
    {
        string original = "IRC log line one\nIRC log line two\n";
        string path = await CreateLogAsync("sample.log", original);

        await LogArchiver.CompressFileAsync(path);

        // Decompress and verify content matches the original.
        string gzPath = path + ".gz";
        await using var fs   = File.OpenRead(gzPath);
        await using var gzip = new GZipStream(fs, CompressionMode.Decompress);
        using  var sr   = new StreamReader(gzip, Encoding.UTF8);
        string recovered = await sr.ReadToEndAsync();

        Assert.Equal(original, recovered);
    }
}
