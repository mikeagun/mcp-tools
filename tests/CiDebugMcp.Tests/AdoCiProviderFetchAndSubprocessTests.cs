// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using CiDebugMcp.Engine;
using Xunit;

namespace CiDebugMcp.Tests;

/// <summary>
/// Coverage for two behaviors on <see cref="AdoCiProvider"/>:
///
///  - <see cref="AdoCiProvider.FetchLogsInParallel"/> — must dedupe by
///    log id (so the same underlying ADO log is not fetched twice when
///    multiple failed jobs reference it), and must treat individual
///    per-log fetch failures as non-fatal (logged and skipped, the
///    surrounding report still completes).
///
///  - <see cref="AdoCiProvider.RunCredentialSubprocess"/> — must enforce
///    its wall-clock timeout by returning <c>null</c> promptly (and
///    requesting process-tree kill) rather than hanging indefinitely.
///    Note: these tests verify the prompt-null-return behavior; whether
///    the underlying process tree is actually killed is documented in
///    the production code and would need an external process-tracking
///    harness to verify, which is out of unit-test scope.
/// </summary>
public class AdoCiProviderFetchAndSubprocessTests
{
    private static AdoCiProvider CreateProvider(Func<int, string, Task<ParsedLog?>> logFetcher)
    {
        return new AdoCiProvider(
            "https://dev.azure.com/test-org",
            "test-project",
            new LogCache(),
            originalHost: null,
            logFetcher: logFetcher);
    }

    private static ParsedLog MakeLog(string firstLine) => new()
    {
        Lines = [firstLine],
        Steps = [],
    };

    // ── FetchLogsInParallel ─────────────────────────────────────

    [Fact]
    public async Task FetchLogsInParallel_DedupesByLogId()
    {
        var calls = new System.Collections.Concurrent.ConcurrentBag<string>();
        var provider = CreateProvider((buildId, logId) =>
        {
            calls.Add(logId);
            return Task.FromResult<ParsedLog?>(MakeLog($"log-{logId}"));
        });

        // Three failed jobs reference only two distinct logs (job-A and job-B
        // both share log "shared-7"; job-C has its own log "unique-9").
        var failedJobs = new List<AdoCiProvider.FailedJobItem>
        {
            new(Name: "job", JobRecordId: null, Result: "failed", FailedTask: null, FailedLogId: "shared-7"),
            new(Name: "job", JobRecordId: null, Result: "failed", FailedTask: null, FailedLogId: "shared-7"),
            new(Name: "job", JobRecordId: null, Result: "failed", FailedTask: null, FailedLogId: "unique-9"),
        };

        var result = await provider.FetchLogsInParallel(buildId: 100, failedJobs);

        // Each distinct log id is fetched exactly once.
        Assert.Equal(2, calls.Count);
        Assert.Contains("shared-7", calls);
        Assert.Contains("unique-9", calls);

        // Result is keyed by log id and contains both logs.
        Assert.Equal(2, result.Count);
        Assert.Equal("log-shared-7", result["shared-7"].Lines[0]);
        Assert.Equal("log-unique-9", result["unique-9"].Lines[0]);
    }

    [Fact]
    public async Task FetchLogsInParallel_EmptyInput_ShortCircuitsWithoutFetch()
    {
        var calls = 0;
        var provider = CreateProvider((buildId, logId) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<ParsedLog?>(MakeLog(logId));
        });

        var result = await provider.FetchLogsInParallel(buildId: 100, new List<AdoCiProvider.FailedJobItem>());

        Assert.Empty(result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task FetchLogsInParallel_FailedJobsWithNoLogId_AreSkipped()
    {
        var calls = 0;
        var provider = CreateProvider((buildId, logId) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<ParsedLog?>(MakeLog(logId));
        });

        // FailedJobItem with FailedLogId == null is the "we know this job
        // failed but timeline didn't surface a log id" case. Nothing to
        // fetch for those.
        var failedJobs = new List<AdoCiProvider.FailedJobItem>
        {
            new(Name: "job", JobRecordId: null, Result: "failed", FailedTask: null, FailedLogId: null),
            new(Name: "job", JobRecordId: null, Result: "failed", FailedTask: null, FailedLogId: null),
        };

        var result = await provider.FetchLogsInParallel(buildId: 100, failedJobs);

        Assert.Empty(result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task FetchLogsInParallel_IndividualFailure_IsNonFatal_OthersStillReturned()
    {
        var provider = CreateProvider((buildId, logId) =>
        {
            if (logId == "bad-log")
                throw new InvalidOperationException("simulated transient ADO error");
            return Task.FromResult<ParsedLog?>(MakeLog($"ok-{logId}"));
        });

        var failedJobs = new List<AdoCiProvider.FailedJobItem>
        {
            new(Name: "job", JobRecordId: null, Result: "failed", FailedTask: null, FailedLogId: "good-1"),
            new(Name: "job", JobRecordId: null, Result: "failed", FailedTask: null, FailedLogId: "bad-log"),
            new(Name: "job", JobRecordId: null, Result: "failed", FailedTask: null, FailedLogId: "good-2"),
        };

        var result = await provider.FetchLogsInParallel(buildId: 100, failedJobs);

        // The failed fetch must NOT poison the result. The two good logs
        // are still returned; the failed log is silently absent. This
        // preserves the single-job-fails-don't-fail-the-report semantic
        // that the production xml-doc on FetchLogsInParallel documents.
        Assert.Equal(2, result.Count);
        Assert.Equal("ok-good-1", result["good-1"].Lines[0]);
        Assert.Equal("ok-good-2", result["good-2"].Lines[0]);
        Assert.False(result.ContainsKey("bad-log"));
    }

    [Fact]
    public async Task FetchLogsInParallel_FetcherReturnsNull_LogOmittedFromResult()
    {
        var provider = CreateProvider((buildId, logId) =>
        {
            return Task.FromResult<ParsedLog?>(logId == "null-log" ? null : MakeLog(logId));
        });

        var failedJobs = new List<AdoCiProvider.FailedJobItem>
        {
            new(Name: "job", JobRecordId: null, Result: "failed", FailedTask: null, FailedLogId: "present-log"),
            new(Name: "job", JobRecordId: null, Result: "failed", FailedTask: null, FailedLogId: "null-log"),
        };

        var result = await provider.FetchLogsInParallel(buildId: 100, failedJobs);

        Assert.Single(result);
        Assert.True(result.ContainsKey("present-log"));
        Assert.False(result.ContainsKey("null-log"));
    }

    // ── Credential subprocess timeout / kill ──────────────────────

    [Fact]
    public void RunCredentialSubprocess_ExceedsTimeout_ReturnsNullPromptly()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Test relies on `cmd.exe /c ping`; CI is windows-latest so
            // skipping on non-Windows would mask the test, not protect it.
            // The repo CI matrix is windows-only — fail loudly if this is
            // ever invoked on a different OS.
            throw new System.PlatformNotSupportedException(
                "Test designed for Windows runner; repo CI matrix is windows-latest.");
        }

        // Run `ping` for ~30 seconds against a 1-second timeout. The helper
        // must return null within a margin of the timeout — it must NOT
        // hang for the full ping duration. The wide gap (~15s threshold
        // against a 30s ping) gives generous headroom for cross-project
        // test load and slow Process.Start while still catching a
        // regression that removes the timeout enforcement entirely.
        //
        // Scope note: this verifies the prompt-timeout-and-return-null
        // contract. The helper also calls proc.Kill(entireProcessTree:
        // true) on timeout to terminate the child; verifying that the
        // underlying process tree is actually killed would require an
        // external process-tracking harness (PID enumeration before /
        // after) which is out of unit-test scope. The kill behavior is
        // documented in the helper's source and is covered by code
        // inspection.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = AdoCiProvider.RunCredentialSubprocess(
            fileName: "cmd.exe",
            arguments: "/c ping 127.0.0.1 -n 30",
            timeoutMs: 1_000,
            timeoutLogContext: "test ping subprocess");
        sw.Stop();

        Assert.Null(result);
        Assert.True(sw.ElapsedMilliseconds < 15_000,
            $"Subprocess helper took {sw.ElapsedMilliseconds}ms — expected to return within a margin of the 1s timeout, not hang for anywhere near the 30s ping duration");
    }

    [Fact]
    public void RunCredentialSubprocess_QuickSubprocess_ReturnsExitCodeAndOutput()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new System.PlatformNotSupportedException(
                "Test designed for Windows runner; repo CI matrix is windows-latest.");
        }

        // `cmd /c echo hello` exits immediately with stdout "hello\r\n".
        // Pin the happy-path result shape so a future refactor of the
        // helper can't accidentally drop the stdout or exit code.
        var result = AdoCiProvider.RunCredentialSubprocess(
            fileName: "cmd.exe",
            arguments: "/c echo hello",
            timeoutMs: 5_000);

        Assert.NotNull(result);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello", result.Stdout);
    }

    [Fact]
    public void RunCredentialSubprocess_NonZeroExit_ReturnsResultWithExitCode()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new System.PlatformNotSupportedException(
                "Test designed for Windows runner; repo CI matrix is windows-latest.");
        }

        // `cmd /c exit 7` produces a non-zero exit code with no stdout.
        // The helper must surface the exit code (callers like
        // GetTokenFromAzCli use it to distinguish success/failure).
        var result = AdoCiProvider.RunCredentialSubprocess(
            fileName: "cmd.exe",
            arguments: "/c exit 7",
            timeoutMs: 5_000);

        Assert.NotNull(result);
        Assert.Equal(7, result.ExitCode);
    }

    [Fact]
    public void RunCredentialSubprocess_FileNotFound_ReturnsNull()
    {
        // A binary that definitely does not exist must produce a clean null
        // return (caught by the helper's outer try/catch), not propagate
        // Win32Exception out to callers.
        var result = AdoCiProvider.RunCredentialSubprocess(
            fileName: "this-binary-does-not-exist.exe",
            arguments: "anything",
            timeoutMs: 5_000);

        Assert.Null(result);
    }
}
