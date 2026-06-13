// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.IO.Compression;

namespace CiDebugMcp.Engine;

/// <summary>
/// A running or completed artifact download. Streams HTTP response to a temp file,
/// tracks progress, and provides ZIP directory listing and selective extraction.
/// </summary>
public sealed class DownloadJob : IDisposable
{
    private const int BufferSize = 81920; // 80KB chunks
    private const long ProgressSignalInterval = 5 * 1024 * 1024; // Signal every 5MB

    public string DownloadId { get; }
    public long ArtifactId { get; }
    public string ArtifactName { get; }
    public string DestPath { get; }
    public DateTime StartTime { get; } = DateTime.UtcNow;

    private readonly Task _downloadTask;
    private readonly HttpClient _http;
    private readonly object _lock = new();

    // Progress (guarded by _lock)
    private long _bytesDownloaded;
    private long _totalBytes;
    private bool _completed;
    private string? _error;
    private List<string>? _contents;
    private int _totalFiles;
    private long _uncompressedSize;
    private int _newsVersion;

    public DownloadJob(HttpClient http, string downloadUrl, string destPath,
        string downloadId, long artifactId, string artifactName)
    {
        DownloadId = downloadId;
        ArtifactId = artifactId;
        ArtifactName = artifactName;
        DestPath = destPath;
        _http = http;
        _downloadTask = Task.Run(() => DownloadAsync(downloadUrl));
    }

    /// <summary>
    /// Test-only constructor: produces a <see cref="DownloadJob"/> already
    /// marked completed, with <paramref name="destPath"/> pointing to
    /// pre-written content. Bypasses the HTTP download loop entirely so
    /// tests exercising <see cref="Extract"/> / <see cref="SearchContents"/>
    /// / <see cref="GetStatus"/> on already-downloaded artifacts do not
    /// depend on <see cref="Task.Run(Action)"/> being scheduled inside the
    /// test's wait budget. The previous "real download via mock handler +
    /// blocking WaitForNews" pattern was the root cause of the
    /// DownloadJobTests parallel-execution flake: under heavy parallel
    /// load (the cross-project test run on a busy machine), the download
    /// Task could be queued but not started within the 10s wait budget,
    /// causing the test to observe an incomplete job.
    /// </summary>
    internal DownloadJob(string destPath, string downloadId, long artifactId, string artifactName)
    {
        DownloadId = downloadId;
        ArtifactId = artifactId;
        ArtifactName = artifactName;
        DestPath = destPath;
        // No HttpClient — this ctor bypasses the download loop entirely.
        // Dispose() null-checks _http so a never-downloaded job is safely
        // disposable without a client.
        _http = null!;
        _downloadTask = Task.CompletedTask;
        // Populate ZIP metadata (_contents, _totalFiles, _uncompressedSize)
        // exactly as the real download path does after streaming the file
        // to disk, so GetStatus / SearchContents / Extract observe a fully
        // populated job — identical to the post-download state.
        ParseZipDirectory();
        // Match the production post-download invariant for the progress
        // fields too — readers of GetStatus see a non-zero Percent and a
        // realistic transfer-size summary instead of a degenerate
        // 0-bytes-of-0 snapshot.
        var totalBytes = new FileInfo(destPath).Length;
        _bytesDownloaded = totalBytes;
        _totalBytes = totalBytes;
        _completed = true;
    }

    private async Task DownloadAsync(string url)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            lock (_lock)
            {
                _totalBytes = response.Content.Headers.ContentLength ?? 0;
            }

            using var httpStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = File.Create(DestPath);

            var buffer = new byte[BufferSize];
            long lastSignal = 0;
            int read;

            while ((read = await httpStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read));
                long current;
                lock (_lock)
                {
                    _bytesDownloaded += read;
                    current = _bytesDownloaded;
                }

                // Signal progress periodically
                if (current - lastSignal >= ProgressSignalInterval)
                {
                    lastSignal = current;
                    lock (_lock) { _newsVersion++; Monitor.PulseAll(_lock); }
                }
            }

            // Parse ZIP directory (reads only central directory, no decompression)
            fileStream.Close();
            ParseZipDirectory();

            lock (_lock) { _completed = true; _newsVersion++; Monitor.PulseAll(_lock); }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _error = ex.Message;
                _completed = true;
                _newsVersion++;
                Monitor.PulseAll(_lock);
            }
        }
    }

    private void ParseZipDirectory()
    {
        try
        {
            using var zip = ZipFile.OpenRead(DestPath);
            var entries = zip.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name))
                .ToList();

            lock (_lock)
            {
                _contents = entries.Select(e => e.FullName).OrderBy(n => n).ToList();
                _totalFiles = entries.Count;
                _uncompressedSize = entries.Sum(e => e.Length);
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _error ??= $"ZIP parsing failed: {ex.Message}";
                _contents = [];
            }
        }
    }

    /// <summary>
    /// Wait up to timeoutMs for completion or new progress.
    /// </summary>
    public void WaitForNews(int timeoutMs)
    {
        if (timeoutMs <= 0) return;

        lock (_lock)
        {
            if (_completed) return;
            var observedVersion = _newsVersion;
            while (!_completed && observedVersion == _newsVersion)
            {
                if (!Monitor.Wait(_lock, timeoutMs))
                    break; // timeout
            }
        }
    }

    /// <summary>
    /// Snapshot of current download state.
    /// </summary>
    public DownloadStatus GetStatus(bool includeContents = false, int maxContents = 20)
    {
        lock (_lock)
        {
            var elapsed = (DateTime.UtcNow - StartTime).TotalSeconds;
            var bytesPerSec = elapsed > 0 ? _bytesDownloaded / elapsed : 0;
            var remainingBytes = Math.Max(0, _totalBytes - _bytesDownloaded);
            var etaSec = bytesPerSec > 0 ? (int)(remainingBytes / bytesPerSec) : 0;

            string status;
            if (_error != null) status = "failed";
            else if (!_completed) status = "downloading";
            else status = "completed";

            return new DownloadStatus
            {
                DownloadId = DownloadId,
                ArtifactId = ArtifactId,
                ArtifactName = ArtifactName,
                Status = status,
                Error = _error,
                BytesDownloaded = _bytesDownloaded,
                TotalBytes = _totalBytes,
                Percent = _totalBytes > 0 ? (int)(_bytesDownloaded * 100 / _totalBytes) : 0,
                ElapsedSeconds = (int)elapsed,
                EtaSeconds = etaSec,
                IsCompleted = _completed,
                TotalFiles = _totalFiles,
                UncompressedSizeMb = _uncompressedSize / (1024 * 1024),
                Contents = includeContents && _contents != null
                    ? _contents.Take(maxContents).ToList()
                    : null,
                HasMoreContents = _contents != null && _contents.Count > maxContents,
                TotalContents = _contents?.Count ?? 0,
            };
        }
    }

    /// <summary>
    /// Extract specific files from the downloaded ZIP.
    /// </summary>
    public List<ExtractedFile> Extract(string[] patterns, string destDir)
    {
        lock (_lock)
        {
            if (!_completed || _error != null)
                throw new InvalidOperationException("Download not completed or failed");
        }

        Directory.CreateDirectory(destDir);

        using var zip = ZipFile.OpenRead(DestPath);
        var results = new List<ExtractedFile>();

        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (!MatchesAnyPattern(entry.FullName, entry.Name, patterns)) continue;

            var dest = Path.Combine(destDir, entry.Name);
            // Handle name collisions by prefixing with parent dir
            if (File.Exists(dest))
            {
                var parent = Path.GetFileName(Path.GetDirectoryName(entry.FullName) ?? "");
                dest = Path.Combine(destDir, $"{parent}_{entry.Name}");
            }

            entry.ExtractToFile(dest, overwrite: true);
            results.Add(new ExtractedFile
            {
                ZipPath = entry.FullName,
                LocalPath = dest,
                SizeBytes = entry.Length,
            });
        }

        return results;
    }

    /// <summary>
    /// Search ZIP contents for entries matching glob patterns.
    /// </summary>
    public List<string> SearchContents(string[] patterns)
    {
        lock (_lock)
        {
            if (_contents == null) return [];
            return _contents.Where(c => MatchesAnyPattern(c, Path.GetFileName(c), patterns)).ToList();
        }
    }

    private static bool MatchesAnyPattern(string fullPath, string fileName, string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            // Simple glob: *.dll matches any .dll, exact name matches filename or full path
            if (pattern.StartsWith("*."))
            {
                var ext = pattern[1..]; // ".dll"
                if (fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (fileName.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
                     fullPath.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public bool IsCompleted
    {
        get { lock (_lock) { return _completed; } }
    }

    public void Dispose()
    {
        // Don't delete the temp file — DownloadManager handles cleanup.
        // Dispose the HttpClient so the underlying socket / connection pool
        // is released deterministically rather than waiting for GC to run
        // the HttpClient finalizer. The internal test-only ctor leaves
        // _http null because it bypasses the download path entirely, so
        // null-check before dispose.
        _http?.Dispose();
    }
}

public sealed class DownloadStatus
{
    public required string DownloadId { get; init; }
    public required long ArtifactId { get; init; }
    public required string ArtifactName { get; init; }
    public required string Status { get; init; }
    public string? Error { get; init; }
    public long BytesDownloaded { get; init; }
    public long TotalBytes { get; init; }
    public int Percent { get; init; }
    public int ElapsedSeconds { get; init; }
    public int EtaSeconds { get; init; }
    public bool IsCompleted { get; init; }
    public int TotalFiles { get; init; }
    public long UncompressedSizeMb { get; init; }
    public List<string>? Contents { get; init; }
    public bool HasMoreContents { get; init; }
    public int TotalContents { get; init; }
}

public sealed class ExtractedFile
{
    public required string ZipPath { get; init; }
    public required string LocalPath { get; init; }
    public long SizeBytes { get; init; }
}
