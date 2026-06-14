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
        // Exercises the real HTTP-download → ParseZipDirectory → completion
        // path (unlike the other DownloadJobTests which use the in-memory
        // test-only ctor that bypasses Task.Run). Two assertions:
        //
        //   Part 1 — the real async path eventually completes successfully.
        //   Polls via WaitForNews with a generous cumulative deadline so
        //   the test does not flake on ThreadPool scheduling latency under
        //   parallel CI load. WaitForNews is invoked as the polling
        //   primitive each iteration, but the assertion does NOT
        //   distinguish "signal arrived" from "per-call timeout expired";
        //   that invariant is covered separately by
        //   <see cref="WaitForNews_PulseDeliveredOnCompletion"/>. The
        //   post-completion state checks (Status, Error, ZIP contents)
        //   ensure the download actually succeeded — a failed download
        //   also marks `_completed = true` and would otherwise let this
        //   test pass on a broken HTTP/ZIP path.
        //
        //   Part 2 — WaitForNews on an already-completed job returns
        //   promptly. The production implementation short-circuits both
        //   via the `if (_completed) return;` fast-path AND via the
        //   `while (!_completed && ...)` loop guard; this assertion pins
        //   the combined invariant. It catches regressions that bypass
        //   both short-circuits (e.g., a refactor that replaced the
        //   lock/Monitor.Wait dance with a naive `Thread.Sleep(timeoutMs)`).
        var sourcePath = Path.Combine(Path.GetTempPath(), $"mcptest-src-{Guid.NewGuid():N}.zip");
        var destPath = Path.Combine(Path.GetTempPath(), $"mcptest-dst-{Guid.NewGuid():N}.zip");

        try
        {
            // Create a small ZIP file for the job to download
            using (var zip = System.IO.Compression.ZipFile.Open(sourcePath, System.IO.Compression.ZipArchiveMode.Create))
            {
                var e = zip.CreateEntry("test.txt");
                using var s = e.Open();
                s.Write("content"u8);
            }

            var handler = new StaticFileHandler(sourcePath);
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var job = new DownloadJob(client, "http://localhost/fake.zip", destPath,
                "dl-signal-test", 99999, "signal-test");

            try
            {
                // Part 1: poll for completion via WaitForNews with a 60s cumulative
                // deadline. Per-call timeout of 2s keeps the loop responsive while
                // still giving the signal path a chance to wake the wait early.
                var deadline = System.Diagnostics.Stopwatch.StartNew();
                while (deadline.Elapsed < TimeSpan.FromSeconds(60))
                {
                    job.WaitForNews(2_000);
                    if (job.IsCompleted) break;
                }

                Assert.True(job.IsCompleted,
                    $"Job did not complete within 60s — real async download path appears broken (elapsed {deadline.ElapsedMilliseconds}ms)");

                // Defend against the failure mode where DownloadAsync's catch
                // block also sets `_completed = true` (DownloadJob.cs ~L133-139):
                // a broken HTTP/ZIP/write path would otherwise let `IsCompleted`
                // alone pass this test.
                var status = job.GetStatus(includeContents: true, maxContents: 5);
                Assert.Null(status.Error);
                Assert.Equal("completed", status.Status);
                Assert.Equal(1, status.TotalContents);
                Assert.Contains("test.txt", status.Contents!);

                // Part 2: WaitForNews on an already-completed job returns promptly.
                // The 500ms bound is generous relative to a lock acquisition
                // (microseconds) but tight enough to catch a regression that
                // bypasses both the `if (_completed) return;` fast-path AND the
                // `while (!_completed)` loop guard (e.g., a refactor that replaced
                // the lock/Monitor.Wait dance with a naive `Thread.Sleep(timeoutMs)`).
                var fastPathSw = System.Diagnostics.Stopwatch.StartNew();
                job.WaitForNews(5_000);
                fastPathSw.Stop();
                Assert.True(fastPathSw.ElapsedMilliseconds < 500,
                    $"WaitForNews on a completed job took {fastPathSw.ElapsedMilliseconds}ms — the completed-job short-circuit is broken");
            }
            finally
            {
                job.Dispose();
            }
        }
        finally
        {
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            if (File.Exists(destPath)) File.Delete(destPath);
        }
    }

    [Fact]
    public void WaitForNews_PulseDeliveredOnCompletion()
    {
        // Deterministic coverage of the Monitor.Wait → PulseAll signal
        // delivery path that the original
        // RealDownload_CompletesAndSignalsViaWaitForNews tried (flakily)
        // to pin. A gated HttpMessageHandler holds DownloadAsync open
        // until the test explicitly releases it, decoupling ThreadPool
        // scheduling latency from the timing assertion: the assertion
        // bounds the time between "gate released" and "waiter thread
        // returned", not the time from job-construction to completion.
        //
        // The waiter is set to WaitForNews(90_000). If PulseAll is removed
        // from DownloadAsync's completion path, the waiter observes
        // completion only at the per-call wait timeout (~90s from waiter
        // start). The test thread polls for waiterReturned with a 60s
        // budget after gate release — under any realistic load, a working
        // signal arrives well within 60s; a broken signal does not, so
        // the assertion fires. This is a wide budget by design: it trades
        // worst-case test duration for robustness against ThreadPool
        // contention in parallel CI.
        var sourcePath = Path.Combine(Path.GetTempPath(), $"mcptest-pulse-src-{Guid.NewGuid():N}.zip");
        var destPath = Path.Combine(Path.GetTempPath(), $"mcptest-pulse-dst-{Guid.NewGuid():N}.zip");

        try
        {
            using (var zip = System.IO.Compression.ZipFile.Open(sourcePath, System.IO.Compression.ZipArchiveMode.Create))
            {
                var e = zip.CreateEntry("test.txt");
                using var s = e.Open();
                s.Write("content"u8);
            }

            using var releaseGate = new ManualResetEventSlim(false);
            var handler = new GatedFileHandler(sourcePath, releaseGate);
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
            var job = new DownloadJob(client, "http://localhost/fake.zip", destPath,
                "dl-pulse-test", 99998, "pulse-test");

            try
            {
                // Spin up a waiter that calls WaitForNews(90_000). It blocks
                // immediately because _completed is false (gate not released).
                var waiterReturned = new ManualResetEventSlim(false);
                var waiterThread = new Thread(() =>
                {
                    job.WaitForNews(90_000);
                    waiterReturned.Set();
                }) { IsBackground = true, Name = "wait-for-news-pulse-test" };
                waiterThread.Start();

                // Ensure the waiter has had enough time to enter Monitor.Wait
                // (no direct observation API; 500ms is conservative for thread
                // start + first lock acquisition). Verify the job is still
                // in-flight and the waiter is still blocked.
                Thread.Sleep(500);
                Assert.False(job.IsCompleted, "Job should still be in-flight before gate release");
                Assert.False(waiterReturned.IsSet, "Waiter should still be blocked before gate release");

                // Release the gate; download proceeds, completion path runs,
                // PulseAll fires, waiter wakes. The 60s post-release budget is
                // generous relative to the actual work (one in-memory HTTP
                // exchange + a small ZIP parse — typically sub-second), but
                // wide enough to absorb ThreadPool contention under stress.
                // A broken PulseAll path would cause WaitForNews to time out
                // at 90s instead — well beyond the 60s budget — so the
                // assertion still discriminates working vs broken signal.
                releaseGate.Set();
                Assert.True(waiterReturned.Wait(60_000),
                    "Waiter did not return within 60s of gate release — PulseAll signal was not delivered (or the lock/wait machinery is broken)");
                Assert.True(job.IsCompleted, "Job should be completed after gate release");

                waiterThread.Join(5_000);
            }
            finally
            {
                job.Dispose();
            }
        }
        finally
        {
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            if (File.Exists(destPath)) File.Delete(destPath);
        }
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

    /// <summary>
    /// Test handler that holds <see cref="SendAsync"/> open until an
    /// external gate is released. Used by
    /// <see cref="WaitForNews_PulseDeliveredOnCompletion"/> to decouple
    /// ThreadPool scheduling latency from the signal-arrival timing
    /// assertion — the test can guarantee a waiter thread is blocked in
    /// <c>Monitor.Wait</c> before <see cref="DownloadJob"/>'s completion
    /// path runs and fires <c>PulseAll</c>.
    /// </summary>
    private sealed class GatedFileHandler(string filePath, ManualResetEventSlim gate) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Run(() => gate.Wait(cancellationToken), cancellationToken).ConfigureAwait(false);
            var content = File.ReadAllBytes(filePath);
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            };
            response.Content.Headers.ContentLength = content.Length;
            return response;
        }
    }
}
