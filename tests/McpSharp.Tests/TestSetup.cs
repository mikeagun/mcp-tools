// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using Xunit;

// Run this assembly's tests serially. Several tests here deliberately block a
// thread (Thread.Sleep in a handler) to exercise the progress timer, while
// others depend on prompt thread-pool scheduling (Timer callbacks) or spawn
// their own concurrent tasks (the PolicyEngine concurrency test). Under xUnit's
// default parallel execution on CPU-constrained CI runners (2 cores), these
// contend: a blocking test can prevent a timing-sensitive test's callback or a
// concurrency test's readers from being scheduled within their window, causing
// flaky failures. Serial execution removes that contention.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace McpSharp.Tests;

/// <summary>
/// Assembly-wide test setup. Raises the thread-pool minimum thread count so a
/// test that spawns several tasks or relies on a timer callback gets threads
/// immediately instead of waiting on the pool's gradual thread injection
/// (~1-2/sec above ProcessorCount), which on 2-core CI runners can delay work
/// past a test's timing window. Combined with serial execution above, this
/// makes the timing-sensitive tests deterministic.
///
/// This affects only the test process. The production server runs its dispatch
/// loop single-threaded and creates at most one progress timer per call, so it
/// does not contend for the pool.
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
