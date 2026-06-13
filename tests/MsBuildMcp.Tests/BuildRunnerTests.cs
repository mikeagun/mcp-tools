// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using MsBuildMcp.Engine;

namespace MsBuildMcp.Tests;

public class BuildRunnerTests
{
    [Theory]
    [InlineData("Debug", "x64")]
    [InlineData("Release", "Any CPU")]
    [InlineData("Debug", "Mixed Platforms")]
    public void BuildArgs_QuotesPlatformAndConfiguration(string configuration, string platform)
    {
        var args = BuildManager.BuildArgs(
            @"C:\my solution\test.sln", targets: null,
            configuration, platform, restore: false, additionalArgs: null);

        var joined = string.Join(" ", args);

        // Solution path must be quoted
        Assert.Contains("\"C:\\my solution\\test.sln\"", joined);
        // Configuration and Platform values must be quoted so spaces don't split args
        Assert.Contains($"/p:Configuration=\"{configuration}\"", joined);
        Assert.Contains($"/p:Platform=\"{platform}\"", joined);
        // Verify no arg was split — each /p: should be a single element in the list
        Assert.DoesNotContain(args, a => a.StartsWith("/p:") && a.Contains(' ') && !a.Contains('"'));
    }

    [Fact]
    public void BuildArgs_IncludesTargetsAndRestore()
    {
        var args = BuildManager.BuildArgs(
            @"C:\test.sln", targets: "Build", "Debug", "x64",
            restore: true, additionalArgs: "/p:Analysis=True");

        Assert.Contains("/t:Build", args);
        Assert.Contains("-Restore", args);
        Assert.Contains("/p:Analysis=True", args);
        Assert.Contains("/v:minimal", args);
    }

    [Fact]
    public void BuildArgs_NullTargets_NoTargetArg()
    {
        var args = BuildManager.BuildArgs(
            @"C:\test.sln", targets: null, "Debug", "x64",
            restore: false, additionalArgs: null);

        Assert.DoesNotContain(args, a => a.StartsWith("/t:"));
    }

    [Fact]
    public void BuildArgs_RestoreFalse_NoRestoreFlag()
    {
        var args = BuildManager.BuildArgs(
            @"C:\test.sln", targets: null, "Debug", "x64",
            restore: false, additionalArgs: null);

        Assert.DoesNotContain("-Restore", args);
    }

    [Fact]
    public void BuildArgs_EmptyAdditionalArgs_IgnoresEmpty()
    {
        var args = BuildManager.BuildArgs(
            @"C:\test.sln", targets: null, "Debug", "x64",
            restore: false, additionalArgs: "");

        Assert.DoesNotContain("", args);
    }

    // --- PublishArgs tests ---

    [Fact]
    public void PublishArgs_MinimalArgs()
    {
        var args = BuildManager.PublishArgs(
            @"C:\my project\MyApp.csproj", "Release",
            runtime: null, framework: null, output: null,
            selfContained: null, additionalArgs: null);

        var joined = string.Join(" ", args);
        Assert.StartsWith("publish", joined);
        Assert.Contains("\"C:\\my project\\MyApp.csproj\"", joined);
        Assert.Contains("-c", args);
        Assert.Contains("Release", args);
        Assert.Contains("--nologo", args);
        Assert.Contains("-v", args);
        Assert.Contains("minimal", args);
        Assert.DoesNotContain(args, a => a == "-r");
        Assert.DoesNotContain(args, a => a == "-f");
        Assert.DoesNotContain(args, a => a == "-o");
        Assert.DoesNotContain(args, a => a.StartsWith("--self-contained"));
    }

    [Fact]
    public void PublishArgs_AllOptions()
    {
        var args = BuildManager.PublishArgs(
            @"C:\MyApp.csproj", "Debug",
            runtime: "win-x64", framework: "net9.0",
            output: @"C:\out", selfContained: true,
            additionalArgs: "/p:PublishTrimmed=true");

        var joined = string.Join(" ", args);
        Assert.Contains("-r", args);
        Assert.Contains("win-x64", args);
        Assert.Contains("-f", args);
        Assert.Contains("net9.0", args);
        Assert.Contains("-o", args);
        Assert.Contains("\"C:\\out\"", joined);
        Assert.Contains("--self-contained=true", joined);
        Assert.Contains("/p:PublishTrimmed=true", joined);
    }

    [Fact]
    public void PublishArgs_SelfContainedFalse()
    {
        var args = BuildManager.PublishArgs(
            @"C:\MyApp.csproj", "Release",
            runtime: null, framework: null, output: null,
            selfContained: false, additionalArgs: null);

        Assert.Contains("--self-contained=false", args);
    }

    [Fact]
    public void PublishArgs_EmptyAdditionalArgs_IgnoresEmpty()
    {
        var args = BuildManager.PublishArgs(
            @"C:\MyApp.csproj", "Release",
            runtime: null, framework: null, output: null,
            selfContained: null, additionalArgs: "");

        Assert.DoesNotContain("", args);
    }

    // --- Concurrency invariant ---

    /// <summary>
    /// Adversarial regression test for the StartOrPoll race window.
    ///
    /// StartOrPoll holds <c>_lock</c> while checking whether a build is
    /// already running. The fix this test pins ensures the lock is also
    /// held across the new-build construction path — otherwise two
    /// concurrent callers could both observe "no running build" and both
    /// proceed to construct separate <see cref="BuildJob"/> instances,
    /// double-spawning MSBuild processes. When the second assignment to
    /// <c>_currentBuild</c> overwrites the first, the loser is disposed
    /// (killing its process tree), but only after the redundant launch.
    ///
    /// The test fires N threads at the same fresh manager and asserts that
    /// exactly one BuildJob was created. Reverting the lock-extension makes
    /// this assertion fail (counter rises with the number of concurrent
    /// callers that won the race; reverted measurement: Actual=6/6).
    ///
    /// Implementation notes:
    /// - A <see cref="Barrier"/> aligns thread entry into StartOrPoll so the
    ///   race window is open simultaneously for all callers. With the fix in
    ///   place, the first thread acquires the lock, runs Process.Start, and
    ///   assigns _currentBuild before subsequent threads enter the check —
    ///   so the in-flight branch fires for callers 2..N.
    /// - We use a non-existent .sln path so MSBuild fails fast; this bounds
    ///   wall time. The build process lifecycle is real, but MSBuild start +
    ///   load + parse + error takes longer than Process.Start + lock release,
    ///   so the in-flight branch reliably fires.
    /// </summary>
    [Fact]
    public async Task StartOrPoll_ConcurrentCalls_DoNotDoubleCreateBuild()
    {
        using var manager = new BuildManager();
        var bogusSln = Path.Combine(Path.GetTempPath(), $"bogus-{Guid.NewGuid():N}.sln");
        const int callers = 6;

        using var startBarrier = new Barrier(callers);
        var tasks = Enumerable.Range(0, callers).Select(_ => Task.Run(() =>
        {
            startBarrier.SignalAndWait();
            try
            {
                manager.StartOrPoll(bogusSln, targets: null,
                    configuration: "Debug", platform: "x64",
                    restore: false, additionalArgs: null,
                    timeoutSeconds: 2);
            }
            catch
            {
                // MSBuild fails fast on a bogus sln; the StartOrPoll call
                // either returns a failed BuildStatus or throws. Either is
                // acceptable for the invariant we care about.
            }
        })).ToArray();

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60));

        // The invariant: only ONE BuildJob was created. Without the lock
        // extension, this is observed to be in {2..callers}.
        Assert.Equal(1, manager.BuildCounter);
    }

    /// <summary>
    /// Sibling test for StartPublish — same race pattern, same lock invariant.
    /// Without the StartPublish lock-extension, multiple concurrent callers
    /// double-spawn `dotnet publish` subprocesses.
    /// </summary>
    [Fact]
    public async Task StartPublish_ConcurrentCalls_DoNotDoubleCreateBuild()
    {
        using var manager = new BuildManager();
        var bogusProj = Path.Combine(Path.GetTempPath(), $"bogus-{Guid.NewGuid():N}.csproj");
        const int callers = 6;

        using var startBarrier = new Barrier(callers);
        var tasks = Enumerable.Range(0, callers).Select(_ => Task.Run(() =>
        {
            startBarrier.SignalAndWait();
            try
            {
                manager.StartPublish(bogusProj, configuration: "Debug",
                    runtime: null, framework: null, output: null,
                    selfContained: null, additionalArgs: null,
                    timeoutSeconds: 2);
            }
            catch { /* dotnet publish fails fast on a bogus csproj */ }
        })).ToArray();

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(1, manager.BuildCounter);
    }

    /// <summary>
    /// Cross-method test: StartOrPoll and StartPublish share <c>_currentBuild</c>.
    /// A concurrent build + publish must not both observe "no running build"
    /// and double-spawn. This pins the cross-method invariant.
    /// </summary>
    [Fact]
    public async Task StartOrPoll_And_StartPublish_Concurrent_DoNotDoubleCreateBuild()
    {
        using var manager = new BuildManager();
        var bogusSln = Path.Combine(Path.GetTempPath(), $"bogus-{Guid.NewGuid():N}.sln");
        var bogusProj = Path.Combine(Path.GetTempPath(), $"bogus-{Guid.NewGuid():N}.csproj");

        using var startBarrier = new Barrier(2);
        var t1 = Task.Run(() =>
        {
            startBarrier.SignalAndWait();
            try
            {
                manager.StartOrPoll(bogusSln, targets: null,
                    configuration: "Debug", platform: "x64",
                    restore: false, additionalArgs: null,
                    timeoutSeconds: 2);
            }
            catch { }
        });
        var t2 = Task.Run(() =>
        {
            startBarrier.SignalAndWait();
            try
            {
                manager.StartPublish(bogusProj, configuration: "Debug",
                    runtime: null, framework: null, output: null,
                    selfContained: null, additionalArgs: null,
                    timeoutSeconds: 2);
            }
            catch { }
        });

        await Task.WhenAll(t1, t2).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(1, manager.BuildCounter);
    }
}
