// Copyright (c) ci-debug-mcp contributors
// SPDX-License-Identifier: MIT

using CiDebugMcp.Engine;
using Xunit;

namespace CiDebugMcp.Tests;

public class LogCacheTests
{
    [Fact]
    public void Get_EmptyCache_ReturnsNull()
    {
        var cache = new LogCache();
        Assert.Null(cache.Get(123));
    }

    [Fact]
    public void Set_Get_RoundTrip()
    {
        var cache = new LogCache();
        var log = new LogCache.CachedLog
        {
            RawText = "line1\nline2",
            Lines = ["line1", "line2"],
            Steps = [new ParsedStep { Number = 1, Name = "Step 1" }],
        };

        cache.Set(42, log);
        var retrieved = cache.Get(42);

        Assert.NotNull(retrieved);
        Assert.Equal(2, retrieved.Lines.Length);
        Assert.Single(retrieved.Steps);
    }

    [Fact]
    public void Get_DifferentId_ReturnsNull()
    {
        var cache = new LogCache();
        cache.Set(1, new LogCache.CachedLog
        {
            RawText = "x",
            Lines = ["x"],
            Steps = [],
        });

        Assert.Null(cache.Get(999));
    }

    [Fact]
    public void Set_OverwritesSameId()
    {
        var cache = new LogCache();
        cache.Set(1, new LogCache.CachedLog { RawText = "old", Lines = ["old"], Steps = [] });
        cache.Set(1, new LogCache.CachedLog { RawText = "new", Lines = ["new"], Steps = [] });

        var result = cache.Get(1);
        Assert.NotNull(result);
        Assert.Equal("new", result.Lines[0]);
    }

    [Fact]
    public void Set_MultipleEntries_AllRetrievable()
    {
        var cache = new LogCache();
        cache.Set(10, new LogCache.CachedLog { RawText = "one", Lines = ["one"], Steps = [] });
        cache.Set(20, new LogCache.CachedLog { RawText = "two", Lines = ["two"], Steps = [] });
        cache.Set(30, new LogCache.CachedLog { RawText = "three", Lines = ["three"], Steps = [] });

        Assert.NotNull(cache.Get(10));
        Assert.NotNull(cache.Get(20));
        Assert.NotNull(cache.Get(30));
        Assert.Equal("one", cache.Get(10)!.Lines[0]);
        Assert.Equal("two", cache.Get(20)!.Lines[0]);
        Assert.Equal("three", cache.Get(30)!.Lines[0]);
    }

    [Fact]
    public void Set_EvictsOldEntriesOnSizeLimit()
    {
        var cache = new LogCache();
        // MaxTotalBytes = 100 * 1024 * 1024 = 104,857,600.
        // Each entry estimated at RawText.Length * 2.
        // Use 50M chars → 100 MB each (under limit individually, over collectively).
        var large1 = new string('a', 50_000_000);
        var large2 = new string('b', 50_000_000);

        cache.Set(1, new LogCache.CachedLog
        {
            RawText = large1, Lines = ["a"], Steps = [],
            FetchedAt = DateTime.UtcNow.AddMinutes(-5),
        });
        cache.Set(2, new LogCache.CachedLog
        {
            RawText = large2, Lines = ["b"], Steps = [],
            FetchedAt = DateTime.UtcNow.AddMinutes(-3),
        });
        // Third Set sees combined size (200 MB) > limit and evicts the oldest
        cache.Set(3, new LogCache.CachedLog { RawText = "small", Lines = ["c"], Steps = [] });

        Assert.Null(cache.Get(1));      // evicted (oldest by FetchedAt)
        Assert.NotNull(cache.Get(2));
        Assert.NotNull(cache.Get(3));
    }
}
