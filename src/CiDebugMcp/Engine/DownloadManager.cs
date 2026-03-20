using System.Collections.Concurrent;

namespace CiDebugMcp.Engine;

/// <summary>
/// Manages concurrent artifact downloads. Multiple downloads can run in parallel.
/// Provides dedup (same artifact_id reuses existing download), TTL-based cleanup.
/// </summary>
public sealed class DownloadManager : IDisposable
{
    private readonly ConcurrentDictionary<string, DownloadJob> _downloads = new();
    private readonly ConcurrentDictionary<long, string> _artifactToDownloadId = new();
    private readonly IGitHubApi _github;
    private readonly string _cacheDir;
    private int _counter;
    private readonly Timer _cleanupTimer;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CompletedTtl = TimeSpan.FromHours(1);

    public DownloadManager(IGitHubApi github)
    {
        _github = github;
        _cacheDir = Path.Combine(Path.GetTempPath(), "ci-debug-mcp", "artifacts");
        Directory.CreateDirectory(_cacheDir);
        _cleanupTimer = new Timer(_ => Cleanup(), null, CleanupInterval, CleanupInterval);
    }

    /// <summary>
    /// Start a download for an artifact. Returns existing download if same artifact_id.
    /// </summary>
    public DownloadJob StartDownload(string owner, string repo, long artifactId, string artifactName)
    {
        // Dedup: if we already have a download for this artifact, return it
        if (_artifactToDownloadId.TryGetValue(artifactId, out var existingId) &&
            _downloads.TryGetValue(existingId, out var existing))
        {
            return existing;
        }

        var downloadId = $"dl-{Interlocked.Increment(ref _counter)}";
        var destPath = Path.Combine(_cacheDir, $"{downloadId}_{artifactId}.zip");

        // Get the download URL (artifact API returns a redirect)
        var downloadUrl = $"https://api.github.com/repos/{owner}/{repo}/actions/artifacts/{artifactId}/zip";

        var job = new DownloadJob(_github.CreateAuthenticatedClient(), downloadUrl, destPath,
            downloadId, artifactId, artifactName);

        _downloads[downloadId] = job;
        _artifactToDownloadId[artifactId] = downloadId;

        return job;
    }

    /// <summary>
    /// Get an existing download by ID.
    /// </summary>
    public DownloadJob? GetDownload(string downloadId)
    {
        _downloads.TryGetValue(downloadId, out var job);
        return job;
    }

    /// <summary>
    /// Get status of all active and recent downloads.
    /// </summary>
    public List<DownloadStatus> GetAllDownloads()
    {
        return _downloads.Values
            .Select(j => j.GetStatus())
            .OrderByDescending(s => s.DownloadId)
            .ToList();
    }

    /// <summary>
    /// Get the extraction directory for a download.
    /// </summary>
    public string GetExtractDir(string downloadId)
    {
        var dir = Path.Combine(_cacheDir, downloadId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void Cleanup()
    {
        var now = DateTime.UtcNow;
        foreach (var (id, job) in _downloads)
        {
            if (job.IsCompleted && (now - job.StartTime) > CompletedTtl)
            {
                if (_downloads.TryRemove(id, out var removed))
                {
                    _artifactToDownloadId.TryRemove(removed.ArtifactId, out _);

                    // Clean up files
                    try { File.Delete(removed.DestPath); } catch { }
                    var extractDir = Path.Combine(_cacheDir, id);
                    try { if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true); } catch { }

                    removed.Dispose();
                    Console.Error.WriteLine($"ci-debug-mcp: cleaned up download {id}");
                }
            }
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        foreach (var job in _downloads.Values)
            job.Dispose();
        _downloads.Clear();
    }
}
