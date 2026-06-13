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
    public void RealDownload_CompletesAndSignalsViaWaitForNews()
    {
        // Deliberately exercises the real HTTP-download → ParseZipDirectory →
        // PulseAll signaling chain (unlike the other DownloadJobTests which
        // use the in-memory test-only ctor). The pulse-on-completion path
        // is otherwise untested by the in-memory ctor (it skips Task.Run
        // entirely), so this test is the only coverage that proves
        // WaitForNews returns on a signal rather than on timeout.
        var tempPath = Path.Combine(Path.GetTempPath(), $"mcptest-{Guid.NewGuid():N}.zip");

        // Create a small ZIP file for the job to download
        using (var zip = System.IO.Compression.ZipFile.Open(tempPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("test.txt");
            using var s = e.Open();
            s.Write("content"u8);
        }

        var handler = new StaticFileHandler(tempPath);
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var job = new DownloadJob(client, "http://localhost/fake.zip", tempPath,
            "dl-signal-test", 99999, "signal-test");

        // Wait for the completion signal with a generous timeout so the
        // assertion below isolates "signal arrived" from "wait budget
        // exhausted". If the pulse-on-complete chain breaks, this wait will
        // run the full timeout and the timing assertion will fail.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        job.WaitForNews(20_000);
        sw.Stop();

        Assert.True(job.IsCompleted, "Job should have completed via the WaitForNews signaling path");
        // Early-return semantics: signal-driven completion should be near-
        // instant for an in-memory download; the 15s bar leaves enormous
        // headroom for parallel-test ThreadPool scheduling latency without
        // weakening the "signal arrived before timeout" coverage.
        Assert.True(sw.ElapsedMilliseconds < 15_000,
            $"WaitForNews took {sw.ElapsedMilliseconds}ms — the signal did not arrive before the wait budget expired");
    }

    // ── Helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Create a completed DownloadJob whose <c>DestPath</c> points to a real
    /// ZIP file containing the specified entries.
    ///
    /// Bypasses the HTTP download loop entirely via the internal test-only
    /// constructor on <see cref="DownloadJob"/>. The previous implementation
    /// used a <c>StaticFileHandler</c> + synchronous wait pattern that
    /// caused parallel-execution flakes when the download Task did not run
    /// inside the wait budget under cross-project test load.
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

        var job = new DownloadJob(tempPath, "dl-test", 12345,
            entries.FirstOrDefault() ?? "artifact");

        return (job, tempPath);
    }

    /// <summary>
    /// HttpMessageHandler that returns a file's content as the response.
    /// Used only by <see cref="WaitForNews_SignalDelivered_ReturnsBeforeTimeout"/>
    /// which deliberately exercises the real HTTP-download → WaitForNews
    /// signaling path. All other tests use the in-memory test-only
    /// constructor on <see cref="DownloadJob"/>.
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
