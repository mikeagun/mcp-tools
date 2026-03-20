// Copyright (c) ci-debug-mcp contributors
// SPDX-License-Identifier: MIT

using CiDebugMcp.Engine;
using Xunit;

namespace CiDebugMcp.Tests;

public class TestExtractionTests
{
    // ── ExtractTestCaseName ─────────────────────────────────────

    [Fact]
    public void ExtractTestCaseName_Catch2_FindsNameBetweenSeparators()
    {
        // Catch2 format: --- separator, test name, --- separator, file(line), ... separator, FAILED:
        var lines = new[]
        {
            "-------------------------------------------------------------------------------",
            "show hash PE file with hash section hashonly",
            "-------------------------------------------------------------------------------",
            "netsh_test.cpp(1183)",
            "...............................................................................",
            "netsh_test.cpp(1183): FAILED:",
        };
        var name = LogParser.ExtractTestCaseName(lines, 5, 0);
        Assert.Equal("show hash PE file with hash section hashonly", name);
    }

    [Fact]
    public void ExtractTestCaseName_Gtest_FindsFromRunMarker()
    {
        var lines = new[]
        {
            "[ RUN      ] MyTest.ShouldPass",
            "some output",
            "test assertion failed",
        };
        var name = LogParser.ExtractTestCaseName(lines, 2, 0);
        Assert.Equal("MyTest.ShouldPass", name);
    }

    [Fact]
    public void ExtractTestCaseName_NotFound_ReturnsNull()
    {
        var lines = new[]
        {
            "some random output",
            "no separators here",
            "FAILED: some assertion",
        };
        var name = LogParser.ExtractTestCaseName(lines, 2, 0);
        Assert.Null(name);
    }

    [Fact]
    public void ExtractTestCaseName_Catch2_SkipsFileReference()
    {
        // The line between separators that contains '(' is a file reference, not a test name
        var lines = new[]
        {
            "-------------------------------------------------------------------------------",
            "my test case name",
            "-------------------------------------------------------------------------------",
            "test_file.cpp(42)",
            "...............................................................................",
            "FAILED:",
        };
        var name = LogParser.ExtractTestCaseName(lines, 5, 0);
        Assert.Equal("my test case name", name);
    }

    // ── ExtractFailedTestNames ──────────────────────────────────

    [Fact]
    public void ExtractFailedTestNames_MultipleCatch2()
    {
        var lines = new[]
        {
            "-------------------------------------------------------------------------------",
            "Test Alpha",
            "-------------------------------------------------------------------------------",
            "file.cpp(10)",
            "...............................................................................",
            "FAILED:",
            "  REQUIRE( x == 1 )",
            "some output",
            "-------------------------------------------------------------------------------",
            "Test Beta",
            "-------------------------------------------------------------------------------",
            "file.cpp(20)",
            "...............................................................................",
            "FAILED:",
            "  REQUIRE( y == 2 )",
        };
        var step = MakeStep(lines);
        var names = LogParser.ExtractFailedTestNames(lines, step);

        Assert.Equal(2, names.Count);
        Assert.Contains("Test Alpha", names);
        Assert.Contains("Test Beta", names);
    }

    [Fact]
    public void ExtractFailedTestNames_Gtest()
    {
        var lines = new[]
        {
            "[  FAILED  ] MyFixture.TestOne (15 ms)",
            "[  FAILED  ] MyFixture.TestTwo (3 ms)",
        };
        var step = MakeStep(lines);
        var names = LogParser.ExtractFailedTestNames(lines, step);

        Assert.Equal(2, names.Count);
        Assert.Contains("MyFixture.TestOne", names);
        Assert.Contains("MyFixture.TestTwo", names);
    }

    [Fact]
    public void ExtractFailedTestNames_DeduplicatesSameName()
    {
        var lines = new[]
        {
            "-------------------------------------------------------------------------------",
            "Repeated Test",
            "-------------------------------------------------------------------------------",
            "file.cpp(10)",
            "...............................................................................",
            "FAILED:",
            "  REQUIRE( x == 1 )",
            "-------------------------------------------------------------------------------",
            "Repeated Test",
            "-------------------------------------------------------------------------------",
            "file.cpp(20)",
            "...............................................................................",
            "FAILED:",
            "  REQUIRE( x == 2 )",
        };
        var step = MakeStep(lines);
        var names = LogParser.ExtractFailedTestNames(lines, step);

        Assert.Single(names);
        Assert.Equal("Repeated Test", names[0]);
    }

    [Fact]
    public void ExtractFailedTestNames_EmptyStep_ReturnsEmpty()
    {
        var lines = new[] { "just some output", "nothing failed" };
        var step = MakeStep(lines);
        Assert.Empty(LogParser.ExtractFailedTestNames(lines, step));
    }

    // ── ExtractTestSummary ──────────────────────────────────────

    [Fact]
    public void ExtractTestSummary_Catch2()
    {
        var lines = new[]
        {
            "...lots of test output...",
            "===============================================================================",
            "test cases: 247 | 245 passed | 2 failed",
            "assertions: 1893 | 1891 passed | 2 failed",
        };
        var step = MakeStep(lines);
        var summary = LogParser.ExtractTestSummary(lines, step);

        Assert.NotNull(summary);
        Assert.Equal("catch2", summary.Framework);
        Assert.Equal(247, summary.Total);
        Assert.Equal(245, summary.Passed);
        Assert.Equal(2, summary.Failed);
    }

    [Fact]
    public void ExtractTestSummary_GtestTwoPass()
    {
        // gtest prints PASSED then FAILED — both should be collected
        var lines = new[]
        {
            "[==========] 100 tests from 10 test suites ran.",
            "[  PASSED  ] 98 tests.",
            "[  FAILED  ] 2 tests, listed below:",
            "[  FAILED  ] MyTest.Alpha",
            "[  FAILED  ] MyTest.Beta",
        };
        var step = MakeStep(lines);
        var summary = LogParser.ExtractTestSummary(lines, step);

        Assert.NotNull(summary);
        Assert.Equal("gtest", summary.Framework);
        Assert.Equal(100, summary.Total); // 98 + 2
        Assert.Equal(98, summary.Passed);
        Assert.Equal(2, summary.Failed);
    }

    [Fact]
    public void ExtractTestSummary_Generic()
    {
        var lines = new[] { "Results: 3 tests failed" };
        var step = MakeStep(lines);
        var summary = LogParser.ExtractTestSummary(lines, step);

        Assert.NotNull(summary);
        Assert.Equal("unknown", summary.Framework);
        Assert.Equal(3, summary.Failed);
    }

    [Fact]
    public void ExtractTestSummary_NotFound_ReturnsNull()
    {
        var lines = new[] { "Build completed successfully", "No test output here" };
        var step = MakeStep(lines);
        Assert.Null(LogParser.ExtractTestSummary(lines, step));
    }

    [Fact]
    public void ExtractTestSummary_Pytest()
    {
        var lines = new[]
        {
            "============================= test session starts ==============================",
            "collected 10 items",
            "tests/test_auth.py::test_login PASSED",
            "tests/test_auth.py::test_logout FAILED",
            "short test summary info",
            "3 tests failed, 7 passed in 2.31s",
        };
        var step = MakeStep(lines);
        var summary = LogParser.ExtractTestSummary(lines, step);

        Assert.NotNull(summary);
        Assert.Equal("unknown", summary.Framework); // caught by generic pattern
        Assert.Equal(3, summary.Failed);
    }

    [Fact]
    public void ExtractTestSummary_Xunit()
    {
        var lines = new[]
        {
            "[xUnit.net 00:00:01.23]   Starting: MyTests",
            "[xUnit.net 00:00:02.50]     TestClass.TestMethod [FAIL]",
            "[xUnit.net 00:00:02.60]     TestClass.TestMethod2 [FAIL]",
            "2 failures",
        };
        var step = MakeStep(lines);
        var summary = LogParser.ExtractTestSummary(lines, step);

        Assert.NotNull(summary);
        Assert.Equal("unknown", summary.Framework);
        Assert.Equal(2, summary.Failed);
    }

    [Fact]
    public void ExtractFailedTestNames_EmptyLines_ReturnsEmpty()
    {
        var lines = Array.Empty<string>();
        var step = MakeStep(lines);
        Assert.Empty(LogParser.ExtractFailedTestNames(lines, step));
    }

    [Fact]
    public void ExtractTestSummary_ScansLast100Lines()
    {
        // Place summary at line 95 from end (beyond old 50-line window, within new 100)
        var lines = new string[200];
        for (int i = 0; i < 200; i++) lines[i] = $"output line {i}";
        lines[105] = "test cases: 50 | 48 passed | 2 failed";
        var step = MakeStep(lines);

        var summary = LogParser.ExtractTestSummary(lines, step);
        Assert.NotNull(summary);
        Assert.Equal(50, summary.Total);
    }

    // ── Helpers ─────────────────────────────────────────────────

    private static ParsedStep MakeStep(string[] lines)
    {
        return new ParsedStep
        {
            Number = 1,
            Name = "Test Step",
            StartLine = 0,
            EndLine = lines.Length - 1,
        };
    }
}
