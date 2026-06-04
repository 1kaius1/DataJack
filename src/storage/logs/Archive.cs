// SPDX-License-Identifier: GPL-3.0-or-later
// Log file rotation and gzip compression. See ARCHITECTURE.md §12.2.
//
// ArchiveOldLogsAsync scans a log directory tree for .log files whose last-write
// time is older than the configured rotation age and compresses each one in place,
// producing a <name>.log.gz and deleting the original.
//
// Compression: System.IO.Compression.GZipStream (no external dependencies).
// zstd is planned for a future phase when a pure-.NET implementation is available.

using System.IO.Compression;

namespace DataJack.Core.Storage.Logs;

/// <summary>
/// Compresses old log files with gzip and removes the originals. Intended to be
/// called periodically (e.g. at application start-up or on a daily timer).
/// </summary>
public static class LogArchiver
{
    // ---------------------------------------------------------------------------
    // Public API
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Scan <paramref name="logDirectory"/> recursively for <c>*.log</c> files whose
    /// last-write time is older than <paramref name="maxAgeDays"/> days. Each qualifying
    /// file is compressed to <c>&lt;name&gt;.log.gz</c> and the original is deleted.
    /// Already-compressed <c>*.log.gz</c> files are never touched.
    /// Silently returns when <paramref name="logDirectory"/> does not exist.
    /// </summary>
    /// <param name="logDirectory">Root directory to scan (subdirectories included).</param>
    /// <param name="maxAgeDays">
    /// Files last written more than this many days ago are archived. Default: 90.
    /// </param>
    public static async Task ArchiveOldLogsAsync(
        string            logDirectory,
        int               maxAgeDays = 90,
        CancellationToken ct         = default)
    {
        if (!Directory.Exists(logDirectory)) return;

        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-maxAgeDays);

        foreach (string path in Directory.EnumerateFiles(
            logDirectory, "*.log", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            var lastWrite = new DateTimeOffset(
                DateTime.SpecifyKind(File.GetLastWriteTimeUtc(path), DateTimeKind.Utc),
                TimeSpan.Zero);

            if (lastWrite < cutoff)
                await CompressFileAsync(path, ct).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------------------
    // Internal helpers (exposed for targeted unit tests)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Compress <paramref name="path"/> to <c><paramref name="path"/>.gz</c> using
    /// optimal gzip compression, then delete the original file.
    /// </summary>
    internal static async Task CompressFileAsync(string path, CancellationToken ct = default)
    {
        string outPath = path + ".gz";
        await CompressToAsync(path, outPath, ct).ConfigureAwait(false);
        File.Delete(path);
    }

    // Write a gzip-compressed copy of sourcePath to destPath.
    // Streams are disposed (and gzip flushed) before this method returns.
    private static async Task CompressToAsync(
        string sourcePath, string destPath, CancellationToken ct)
    {
        await using var input  = File.OpenRead(sourcePath);
        await using var output = File.Create(destPath);
        await using var gzip   = new GZipStream(output, CompressionLevel.Optimal);
        await input.CopyToAsync(gzip, ct).ConfigureAwait(false);
        // Disposal order (reverse of declaration): gzip → output → input.
        // GZipStream is flushed and its footer written before the output FileStream closes.
    }
}
