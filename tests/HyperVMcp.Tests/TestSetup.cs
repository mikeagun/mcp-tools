// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using Xunit;

// Run this assembly's tests serially. Some tests here are timing-sensitive
// (e.g. CommandJob.WaitForCompletion drain/race tests spawn threads and assert
// prompt completion). Under xUnit's default parallel execution on
// CPU-constrained CI runners (2 cores), a blocking test can prevent a
// timing-sensitive test's threads from being scheduled within its window,
// causing flaky failures. Serial execution removes that contention.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace HyperVMcp.Tests;

/// <summary>
/// Assembly-wide test setup. Raises the thread-pool minimum thread count so a
/// test that spawns several threads/tasks or relies on prompt scheduling gets
/// them immediately instead of waiting on the pool's gradual thread injection
/// (~1-2/sec above ProcessorCount), which on 2-core CI runners can delay work
/// past a test's timing window. Combined with serial execution above, this
/// makes the timing-sensitive tests deterministic.
///
/// This affects only the test process, not the production server.
/// </summary>
internal static class TestSetup
{
    [ModuleInitializer]
    internal static void Init()
    {
        ThreadPool.GetMinThreads(out var worker, out var completion);
        ThreadPool.SetMinThreads(Math.Max(worker, 64), Math.Max(completion, 64));
    }
}
