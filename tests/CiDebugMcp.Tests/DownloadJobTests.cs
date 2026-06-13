// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.IO.Compression;
using CiDebugMcp.Engine;
using Xunit;

namespace CiDebugMcp.Tests;

public class DownloadJobTests
{
    // ── MatchesAnyPattern (via SearchContents) ──────────────────

    [Fact]
    public void SearchContents_GlobStar_MatchesExtension()
    {
        var (job, _) = CreateCompletedJobWithContents(["dir/file.dll", "dir/file.exe", "other.txt"]);
        var matches = job.SearchContents(["*.dll"]);

        Assert.Single(matches);
        Assert.Equal("dir/file.dll", matches[0]);
    }

    [Fact]
    public void SearchContents_ExactName_MatchesFilename()
    {
        var (job, _) = CreateCompletedJobWithContents(["x64/Debug/ebpfapi.dll", "x64/Debug/other.dll"]);
        var matches = job.SearchContents(["ebpfapi.dll"]);

        Assert.Single(matches);
    }

    [Fact]
    public void SearchContents_PathSubstring_Matches()
    {
        var (job, _) = CreateCompletedJobWithContents(["x64/Debug/ebpfapi.dll", "x64/Release/ebpfapi.dll"]);
        var matches = job.SearchContents(["Debug"]);

        Assert.Single(matches);
        Assert.Contains("Debug", matches[0]);
    }

    [Fact]
    public void SearchContents_NoMatch_ReturnsEmpty()
    {
        var (job, _) = CreateCompletedJobWithContents(["a.txt", "b.txt"]);
        Assert.Empty(job.SearchContents(["*.dll"]));
    }

    [Fact]
    public void SearchContents_MultiplePatterns()
    {
        var (job, _) = CreateCompletedJobWithContents(["a.dll", "b.exe", "c.txt"]);
        var matches = job.SearchContents(["*.dll", "*.exe"]);

        Assert.Equal(2, matches.Count);
    }

    // ── Extract ─────────────────────────────────────────────────

    [Fact]
    public void Extract_SelectiveExtraction()
    {
        var (job, _) = CreateCompletedJobWithContents(["target.dll", "other.txt"], writeContent: true);
        var destDir = Path.Combine(Path.GetTempPath(), $"mcptest-{Guid.NewGuid():N}");

        try
        {
            var extracted = job.Extract(["*.dll"], destDir);
            Assert.Single(extracted);
            Assert.Equal("target.dll", extracted[0].ZipPath);
            Assert.True(File.Exists(extracted[0].LocalPath));
        }
        finally
        {
            if (Directory.Exists(destDir)) Directory.Delete(destDir, true);
        }
    }

    [Fact]
    public void Extract_NoMatch_ReturnsEmpty()
    {
        var (job, _) = CreateCompletedJobWithContents(["a.txt"], writeContent: true);
        var destDir = Path.Combine(Path.GetTempPath(), $"mcptest-{Guid.NewGuid():N}");

        try
        {
            var extracted = job.Extract(["*.dll"], destDir);
            Assert.Empty(extracted);
        }
        finally
        {
            if (Directory.Exists(destDir)) Directory.Delete(destDir, true);
        }
    }

    // ── GetStatus ───────────────────────────────────────────────

    [Fact]
    public void GetStatus_CompletedJob()
    {
        var (job, _) = CreateCompletedJobWithContents(["a.dll", "b.dll"]);
        var status = job.GetStatus(includeContents: true);

        Assert.Equal("completed", status.Status);
        Assert.True(status.IsCompleted);
        Assert.Equal(2, status.TotalContents);
        Assert.NotNull(status.Contents);
    }

    [Fact]
    public void GetStatus_ContentsLimited()
    {
        var files = Enumerable.Range(0, 50).Select(i => $"file{i}.dll").ToArray();
        var (job, _) = CreateCompletedJobWithContents(files);
        var status = job.GetStatus(includeContents: true, maxContents: 5);

        Assert.Equal(5, status.Contents!.Count);
        Assert.True(status.HasMoreContents);
        Assert.Equal(50, status.TotalContents);
    }

    [Fact]
    public void GetStatus_HasMoreContents_FalseWhenExactCount()
    {
        var files = Enumerable.Range(0, 5).Select(i => $"file{i}.dll").ToArray();
        var (job, _) = CreateCompletedJobWithContents(files);
        var status = job.GetStatus(includeContents: true, maxContents: 5);

        Assert.Equal(5, status.Contents!.Count);
        Assert.False(status.HasMoreContents);
        Assert.Equal(5, status.TotalContents);
    }

    // ── SearchContents (additional) ─────────────────────────────

    [Fact]
    public void SearchContents_CaseInsensitive()
    {
        var (job, _) = CreateCompletedJobWithContents(["dir/foo.dll", "dir/bar.exe"]);
        var matches = job.SearchContents(["*.DLL"]);

        Assert.Single(matches);
        Assert.Equal("dir/foo.dll", matches[0]);
    }

    [Fact]
    public void SearchContents_EmptyPatterns_ReturnsEmpty()
    {
        var (job, _) = CreateCompletedJobWithContents(["a.dll", "b.txt"]);
        Assert.Empty(job.SearchContents([]));
    }

    // ── WaitForNews ───────────────────────────────────────────────

    [Fact]
    public void WaitForNews_CompletedJob_ReturnsImmediately()
    {
        var (job, _) = CreateCompletedJobWithContents(["a.dll"]);

        // Should return immediately, not block for the full timeout
        var sw = System.Diagnostics.Stopwatch.StartNew();
        job.WaitForNews(5000);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"WaitForNews on completed job took {sw.ElapsedMilliseconds}ms, expected <1000ms");
    }

    [Fact]
    public void WaitForNews_ZeroTimeout_ReturnsImmediately()
    {
        var (job, _) = CreateCompletedJobWithContents(["a.dll"]);
        job.WaitForNews(0); // Should not throw or block
    }

    [Fact]
    public void WaitForNews_NegativeTimeout_ReturnsImmediately()
    {
        var (job, _) = CreateCompletedJobWithContents(["a.dll"]);
        job.WaitForNews(-1); // Should not throw or block
    }

    [Fact]
    public void WaitForNews_SignalDelivered_ReturnsBeforeTimeout()
    {
        // Create a job that will complete quickly via a small in-memory response
        var tempPath = Path.Combine(Path.GetTempPath(), $"mcptest-{Guid.NewGuid():N}.zip");

        // Create a small ZIP file for the job to download
        using (var zip = System.IO.Compression.ZipFile.Open(tempPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("test.txt");
            using var s = e.Open();
            s.Write("content"u8);
        }

        var handler = new StaticFileHandler(tempPath);
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var job = new DownloadJob(client, "http://localhost/fake.zip", tempPath,
            "dl-signal-test", 99999, "signal-test");

        // Wait for the download to complete — should be fast since it's in-memory
        var sw = System.Diagnostics.Stopwatch.StartNew();
        job.WaitForNews(5000);
        sw.Stop();

        Assert.True(job.IsCompleted, "Job should have completed");
        Assert.True(sw.ElapsedMilliseconds < 3000,
            $"WaitForNews took {sw.ElapsedMilliseconds}ms, expected <3000ms for in-memory download");
    }

    // ── Helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Create a completed DownloadJob with a real ZIP file containing the specified entries.
    /// </summary>
    private static (DownloadJob job, string tempPath) CreateCompletedJobWithContents(
        string[] entries, bool writeContent = false)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"mcptest-{Guid.NewGuid():N}.zip");

        using (var zip = ZipFile.Open(tempPath, ZipArchiveMode.Create))
        {
            foreach (var entry in entries)
            {
                var e = zip.CreateEntry(entry);
                if (writeContent)
                {
                    using var s = e.Open();
                    s.Write("test content"u8);
                }
            }
        }

        // Create a job that's already "completed" by using a no-op HTTP client
        // We directly set the file and use reflection-free approach: parse the ZIP
        var job = CreateJobFromFile(tempPath, entries.FirstOrDefault() ?? "artifact");

        return (job, tempPath);
    }

    private static DownloadJob CreateJobFromFile(string zipPath, string name)
    {
        // Use a dummy HTTP URL — the download won't actually run since we pre-created the file
        // We need to wait for the download task to fail, then the job still has the ZIP to work with
        // Instead, we use a simpler approach: create via the public API with a mock server

        // Since DownloadJob requires an HttpClient + URL and starts a background task,
        // we create a minimal HTTP server or use a different approach.
        // For unit tests, we test SearchContents/Extract via completed jobs only.

        // Workaround: use a small in-memory HTTP response
        var handler = new StaticFileHandler(zipPath);
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

        var job = new DownloadJob(client, "http://localhost/fake.zip", zipPath,
            "dl-test", 12345, name);

        // Wait for download to complete (it reads from the static handler)
        job.WaitForNews(5000);
        if (!job.IsCompleted)
            job.WaitForNews(5000);

        return job;
    }

    /// <summary>
    /// HttpMessageHandler that returns a file's content as the response.
    /// </summary>
    private sealed class StaticFileHandler(string filePath) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = File.ReadAllBytes(filePath);
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            };
            response.Content.Headers.ContentLength = content.Length;
            return Task.FromResult(response);
        }
    }
}
