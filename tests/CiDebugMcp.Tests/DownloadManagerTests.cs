// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using CiDebugMcp.Engine;
using Xunit;

namespace CiDebugMcp.Tests;

public class DownloadManagerTests : IDisposable
{
    private readonly DownloadManager _manager;

    public DownloadManagerTests()
    {
        _manager = new DownloadManager(new FakeGitHubApi());
    }

    public void Dispose()
    {
        _manager.Dispose();
    }

    // ── Dedup race — concurrent StartDownload for same artifact ───

    [Fact]
    public async Task StartDownload_ConcurrentCalls_SameArtifact_ReturnsSameJob()
    {
        // Use the HttpClient overload to avoid needing a real GitHub API
        const long artifactId = 42;
        var barrier = new Barrier(10);
        var results = new ConcurrentBag<string>();

        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            var job = _manager.StartDownload(
                new HttpClient(new NoOpHandler()),
                "http://localhost/fake.zip",
                artifactId,
                "test-artifact");
            results.Add(job.DownloadId);
        })).ToArray();

        await Task.WhenAll(tasks);

        // All 10 concurrent calls should return the same download ID (dedup worked)
        Assert.Single(results.Distinct());
    }

    [Fact]
    public void StartDownload_DifferentArtifacts_CreatesSeparateJobs()
    {
        var job1 = _manager.StartDownload(
            new HttpClient(new NoOpHandler()), "http://localhost/a.zip", 1, "artifact-a");
        var job2 = _manager.StartDownload(
            new HttpClient(new NoOpHandler()), "http://localhost/b.zip", 2, "artifact-b");

        Assert.NotEqual(job1.DownloadId, job2.DownloadId);
    }

    [Fact]
    public void StartDownload_SameArtifact_Sequential_ReturnsSameJob()
    {
        var job1 = _manager.StartDownload(
            new HttpClient(new NoOpHandler()), "http://localhost/a.zip", 99, "artifact");
        var job2 = _manager.StartDownload(
            new HttpClient(new NoOpHandler()), "http://localhost/a.zip", 99, "artifact");

        Assert.Same(job1, job2);
    }

    // ── GetDownload / GetAllDownloads ────────────────────────────────────

    [Fact]
    public void GetDownload_ExistingId_ReturnsJob()
    {
        var job = _manager.StartDownload(
            new HttpClient(new NoOpHandler()), "http://localhost/a.zip", 10, "artifact");

        Assert.NotNull(_manager.GetDownload(job.DownloadId));
    }

    [Fact]
    public void GetDownload_UnknownId_ReturnsNull()
    {
        Assert.Null(_manager.GetDownload("dl-nonexistent"));
    }

    [Fact]
    public void GetAllDownloads_ReturnsAllActive()
    {
        _manager.StartDownload(
            new HttpClient(new NoOpHandler()), "http://localhost/a.zip", 1, "a");
        _manager.StartDownload(
            new HttpClient(new NoOpHandler()), "http://localhost/b.zip", 2, "b");

        var all = _manager.GetAllDownloads();
        Assert.Equal(2, all.Count);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// HttpMessageHandler that always returns 404 — prevents real HTTP traffic.
    /// Downloads will fail, but that's fine for testing dedup and manager logic.
    /// </summary>
    private sealed class NoOpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
