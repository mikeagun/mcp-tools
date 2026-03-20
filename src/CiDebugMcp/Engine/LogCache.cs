using System.Collections.Concurrent;

namespace CiDebugMcp.Engine;

/// <summary>
/// In-memory cache for downloaded CI job logs. Keyed by job_id.
/// Thread-safe. Entries expire after TTL.
/// </summary>
public sealed class LogCache
{
    private readonly ConcurrentDictionary<long, CachedLog> _cache = new();
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(10);
    private const long MaxTotalBytes = 100 * 1024 * 1024; // 100 MB

    public sealed class CachedLog
    {
        public required string RawText { get; init; }
        public required string[] Lines { get; init; }
        public required ParsedStep[] Steps { get; init; }
        public DateTime FetchedAt { get; init; } = DateTime.UtcNow;
    }

    public CachedLog? Get(long jobId)
    {
        if (_cache.TryGetValue(jobId, out var entry))
        {
            if (DateTime.UtcNow - entry.FetchedAt < _ttl)
                return entry;
            _cache.TryRemove(jobId, out _);
        }
        return null;
    }

    public void Set(long jobId, CachedLog log)
    {
        // Evict expired entries and check size
        long totalSize = 0;
        foreach (var kvp in _cache)
        {
            if (DateTime.UtcNow - kvp.Value.FetchedAt >= _ttl)
            {
                _cache.TryRemove(kvp.Key, out _);
            }
            else
            {
                totalSize += kvp.Value.RawText.Length * 2; // rough UTF-16 size
            }
        }

        // If still over budget, evict oldest
        while (totalSize > MaxTotalBytes && !_cache.IsEmpty)
        {
            var oldest = _cache.MinBy(kvp => kvp.Value.FetchedAt);
            if (_cache.TryRemove(oldest.Key, out var removed))
            {
                totalSize -= removed.RawText.Length * 2;
            }
        }

        _cache[jobId] = log;
    }
}
