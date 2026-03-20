// Copyright (c) ci-debug-mcp contributors
// SPDX-License-Identifier: MIT

using CiDebugMcp.Engine;
using Xunit;

namespace CiDebugMcp.Tests;

public class LogParserTests
{
    // ── StripTimestamp ───────────────────────────────────────────

    [Fact]
    public void StripTimestamp_RemovesTimestampPrefix()
    {
        var line = "2026-03-03T22:07:45.1234567Z Hello world";
        Assert.Equal("Hello world", LogParser.StripTimestamp(line));
    }

    [Fact]
    public void StripTimestamp_StripsAnsiEscapeCodes()
    {
        var line = "\x1b[36mcolored text\x1b[0m";
        Assert.Equal("colored text", LogParser.StripTimestamp(line));
    }

    [Fact]
    public void StripTimestamp_HandlesPlainText()
    {
        Assert.Equal("plain line", LogParser.StripTimestamp("plain line"));
    }

    [Fact]
    public void StripTimestamp_StripsCarriageReturn()
    {
        Assert.Equal("text", LogParser.StripTimestamp("text\r"));
    }

    // ── ParseSteps ──────────────────────────────────────────────

    [Fact]
    public void ParseSteps_EmptyLog_ReturnsEmpty()
    {
        Assert.Empty(LogParser.ParseSteps([]));
    }

    [Fact]
    public void ParseSteps_SingleStep()
    {
        var lines = new[]
        {
            "2026-01-01T00:00:00.0Z ##[group]Run msbuild",
            "2026-01-01T00:00:01.0Z Building...",
            "2026-01-01T00:00:02.0Z ##[endgroup]",
        };
        var steps = LogParser.ParseSteps(lines);

        Assert.Single(steps);
        Assert.Equal(1, steps[0].Number);
        Assert.Equal("Run msbuild", steps[0].Name);
        Assert.Equal(0, steps[0].StartLine);
        Assert.Equal(2, steps[0].EndLine);
    }

    [Fact]
    public void ParseSteps_MultipleSteps()
    {
        var lines = new[]
        {
            "##[group]Step 1",
            "line 1",
            "##[group]Step 2",
            "line 2",
            "line 3",
        };
        var steps = LogParser.ParseSteps(lines);

        Assert.Equal(2, steps.Count());
        Assert.Equal("Step 1", steps[0].Name);
        Assert.Equal(0, steps[0].StartLine);
        Assert.Equal(1, steps[0].EndLine); // closed by next group
        Assert.Equal("Step 2", steps[1].Name);
        Assert.Equal(2, steps[1].StartLine);
        Assert.Equal(4, steps[1].EndLine); // closed at end of input
    }

    [Fact]
    public void ParseSteps_CollectsErrors()
    {
        var lines = new[]
        {
            "##[group]Build",
            "##[error]Something failed",
            "##[error]Another error",
            "ok line",
        };
        var steps = LogParser.ParseSteps(lines);

        Assert.Single(steps);
        Assert.Equal(2, steps[0].Errors.Count);
        Assert.Equal("Something failed", steps[0].Errors[0]);
        Assert.Equal("Another error", steps[0].Errors[1]);
    }

    [Fact]
    public void ParseSteps_CollectsWarnings()
    {
        var lines = new[]
        {
            "##[group]Build",
            "##[warning]Deprecated API",
        };
        var steps = LogParser.ParseSteps(lines);

        Assert.Single(steps);
        Assert.Single(steps[0].Warnings);
        Assert.Equal("Deprecated API", steps[0].Warnings[0]);
    }

    // ── TryParseError ───────────────────────────────────────────

    [Fact]
    public void TryParseError_MsvcError()
    {
        var result = LogParser.TryParseError(@"C:\src\file.cpp(42,5): error C2084: function already has a body");
        Assert.NotNull(result);
        Assert.Equal("msvc", result.Type);
        Assert.Equal("C2084", result.Code);
        Assert.Contains("function already has a body", result.Message);
        Assert.Equal(42, result.Line);
    }

    [Fact]
    public void TryParseError_MsvcWithMsbuildPrefix()
    {
        var result = LogParser.TryParseError(@"113>C:\src\file.cpp(10): error C1234: message");
        Assert.NotNull(result);
        Assert.Equal("msvc", result.Type);
        Assert.Equal("C1234", result.Code);
        // File should NOT contain the 113> prefix
        Assert.DoesNotContain("113>", result.File);
        Assert.Contains("file.cpp", result.File!);
    }

    [Fact]
    public void TryParseError_GccError()
    {
        var result = LogParser.TryParseError("src/main.c:15:3: error: undeclared identifier 'x'");
        Assert.NotNull(result);
        Assert.Equal("gcc", result.Type);
        Assert.Equal(15, result.Line);
        Assert.Contains("undeclared identifier", result.Message);
    }

    [Fact]
    public void TryParseError_NonError_ReturnsNull()
    {
        Assert.Null(LogParser.TryParseError("Just a regular log line"));
        Assert.Null(LogParser.TryParseError("Build succeeded."));
    }

    [Fact]
    public void TryParseError_LinkerError()
    {
        // Linker errors lack the file(line) format required by the MSVC regex,
        // so TryParseError returns null. They are caught by IsMeaningfulError instead.
        Assert.Null(LogParser.TryParseError("LINK : fatal error LNK1169: one or more multiply defined symbols found"));
    }

    [Fact]
    public void TryParseError_GccWarning_ReturnsNull()
    {
        // GCC regex requires "error:" — warnings are not parsed
        Assert.Null(LogParser.TryParseError("src/main.c:15:3: warning: unused variable 'x'"));
    }

    // ── IsMeaningfulError ───────────────────────────────────────

    [Theory]
    [InlineData("FAILED: some test", true)]
    [InlineData("CRASHED during test run", true)]
    [InlineData("Mismatch found in dependencies", true)]
    [InlineData("  REQUIRE( x == 5 )", true)]
    [InlineData("error LNK2019: unresolved external", true)]
    [InlineData("Building project successfully...", false)]
    [InlineData("0.001 s: passing test name", false)]    // passed-test noise
    [InlineData("All 247 tests passed", false)]           // passed-test noise
    [InlineData("StepSecurity harden-runner warning", false)] // SPD noise
    public void IsMeaningfulError_ClassifiesCorrectly(string line, bool expected)
    {
        Assert.Equal(expected, LogParser.IsMeaningfulError(line));
    }

    // ── ExtractMeaningfulErrors ─────────────────────────────────

    [Fact]
    public void ExtractMeaningfulErrors_FindsCatch2Failed()
    {
        var lines = BuildStepLines(
            "Running tests...",
            "test passed",
            "FAILED:",
            "  REQUIRE( output.length() == 65 )",
            "with expansion:",
            "  42 == 65",
            "at netsh_test.cpp(1183)");
        var step = MakeStep(lines, 0, lines.Length - 1);

        var errors = LogParser.ExtractMeaningfulErrors(lines, step, 20);
        Assert.True(errors.Count >= 2); // FAILED: + lookahead lines
        Assert.Contains(errors, e => e.Contains("FAILED:"));
    }

    [Fact]
    public void ExtractMeaningfulErrors_ScansPastMaxErrors()
    {
        // The critical bug fix: errors at line 1138 of 1395 should be found
        // even when maxErrors=20 (scan cap is 5x max, truncate after)
        var lines = new string[1400];
        for (int i = 0; i < 1400; i++)
            lines[i] = $"line {i}: normal output";
        // Place a meaningful error deep in the output
        lines[1138] = "FAILED:";
        lines[1139] = "  REQUIRE( x == 5 )";
        var step = MakeStep(lines, 0, 1399);

        var errors = LogParser.ExtractMeaningfulErrors(lines, step, 5);
        Assert.Contains(errors, e => e.Contains("FAILED:"));
    }

    [Fact]
    public void ExtractMeaningfulErrors_FallsBackToTail()
    {
        // No meaningful patterns → takes last non-blank lines
        var lines = BuildStepLines(
            "Starting process...",
            "Processing item 1",
            "Processing item 2",
            "Unexpected result: got 42 expected 0",
            "##[error]Process completed with exit code 1");
        var step = MakeStep(lines, 0, lines.Length - 1);
        step.Errors.Add("Process completed with exit code 1");

        var errors = LogParser.ExtractMeaningfulErrors(lines, step, 10);
        // Should contain tail lines, not the ##[error]Process completed line
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("Unexpected result"));
    }

    [Fact]
    public void ExtractMeaningfulErrors_DeduplicatesBySameCodeFileLine()
    {
        var lines = BuildStepLines(
            @"C:\src\file.cpp(10,5): error C2084: function has a body",
            @"  113>C:\src\file.cpp(10,5): error C2084: function has a body");
        var step = MakeStep(lines, 0, lines.Length - 1);

        var errors = LogParser.ExtractMeaningfulErrors(lines, step, 20);
        // Both lines have same (C2084, file.cpp, 10) — should dedup to 1
        Assert.Single(errors);
    }

    [Fact]
    public void ExtractMeaningfulErrors_StripsErrorMarkerContent()
    {
        var lines = BuildStepLines(
            "##[error]Real error message here",
            "normal line");
        var step = MakeStep(lines, 0, lines.Length - 1);

        var errors = LogParser.ExtractMeaningfulErrors(lines, step, 20);
        Assert.Contains(errors, e => e == "Real error message here");
        Assert.DoesNotContain(errors, e => e.Contains("##[error]"));
    }

    [Fact]
    public void ExtractMeaningfulErrors_SkipsProcessCompletedError()
    {
        var lines = BuildStepLines(
            "##[error]Process completed with exit code 1");
        var step = MakeStep(lines, 0, lines.Length - 1);

        // With no other content, should fall through to tail (empty)
        var errors = LogParser.ExtractMeaningfulErrors(lines, step, 20);
        Assert.DoesNotContain(errors, e => e.Contains("Process completed with exit code"));
    }

    // ── IsSetupStep / ClassifyStepType ──────────────────────────

    [Theory]
    [InlineData("checkout", true)]
    [InlineData("Set up job", true)]
    [InlineData("Post Cache LLVM", true)]
    [InlineData("Pre install", true)]
    [InlineData("Run msbuild", false)]
    [InlineData("Run tests", false)]
    public void IsSetupStep_Classifies(string name, bool expected)
    {
        Assert.Equal(expected, LogParser.IsSetupStep(name));
    }

    [Theory]
    [InlineData("Run msbuild /m", "build")]
    [InlineData("cmake --build", "build")]
    [InlineData("Run unit_tests.exe", "test")]
    [InlineData("stress_tests_um", "test")]
    [InlineData("Run fuzzer", "test")]
    [InlineData("Run benchmark", "test")]
    [InlineData("conformance tests", "test")]
    [InlineData("Deploy to staging", "deploy")]
    [InlineData("Set environment variable", "unknown")]
    public void ClassifyStepType_Classifies(string name, string expected)
    {
        Assert.Equal(expected, LogParser.ClassifyStepType(name));
    }

    [Fact]
    public void SuggestSearchPattern_ByErrorType()
    {
        Assert.Contains("error C", LogParser.SuggestSearchPattern([@"file.cpp(1): error C1234: msg"]));
        Assert.Contains("Mismatch", LogParser.SuggestSearchPattern(["Mismatch found in deps"]));
        Assert.Contains("FAILED:", LogParser.SuggestSearchPattern(["FAILED: test case"]));
    }

    [Fact]
    public void SuggestPatternForStepType_ReturnsTargeted()
    {
        Assert.Contains("FAILED:", LogParser.SuggestPatternForStepType("test"));
        Assert.Contains("error C", LogParser.SuggestPatternForStepType("build"));
        Assert.Contains("error", LogParser.SuggestPatternForStepType("unknown"));
    }

    // ── IsInSetupBlock ──────────────────────────────────────────

    [Fact]
    public void IsInSetupBlock_InsideGroup_ReturnsTrue()
    {
        var lines = new[]
        {
            "##[group]Set up job",
            "Prepare all required actions",
            "Getting action download info",
            "##[endgroup]",
            "Actual build output",
        };
        Assert.True(LogParser.IsInSetupBlock(lines, 1));
        Assert.False(LogParser.IsInSetupBlock(lines, 4));
    }

    [Fact]
    public void IsInSetupBlock_MatchesBoilerplate()
    {
        var lines = new[] { "Prepare all required actions" };
        Assert.True(LogParser.IsInSetupBlock(lines, 0));
    }

    // ── StripTimestamp (additional) ─────────────────────────────

    [Fact]
    public void StripTimestamp_MultipleTimestampsOnlyStripsFirst()
    {
        var line = "2026-01-01T00:00:00.0Z Data at 2026-06-15T12:00:00.0Z end";
        var result = LogParser.StripTimestamp(line);
        Assert.Equal("Data at 2026-06-15T12:00:00.0Z end", result);
    }

    // ── ParseSteps (additional) ─────────────────────────────────

    [Fact]
    public void ParseSteps_NestedGroups()
    {
        // A new ##[group] inside an existing group closes the outer step
        var lines = new[]
        {
            "##[group]Outer Step",
            "outer content",
            "##[group]Inner Step",
            "inner content",
            "##[endgroup]",
        };
        var steps = LogParser.ParseSteps(lines);

        Assert.Equal(2, steps.Count());
        Assert.Equal("Outer Step", steps[0].Name);
        Assert.Equal(0, steps[0].StartLine);
        Assert.Equal(1, steps[0].EndLine); // closed by inner group
        Assert.Equal("Inner Step", steps[1].Name);
        Assert.Equal(2, steps[1].StartLine);
        Assert.Equal(4, steps[1].EndLine);
    }

    // ── ExtractMeaningfulErrors (additional) ────────────────────

    [Fact]
    public void ExtractMeaningfulErrors_EmptyStep_ReturnsEmpty()
    {
        var lines = Array.Empty<string>();
        var step = MakeStep(lines, 0, lines.Length - 1);
        var errors = LogParser.ExtractMeaningfulErrors(lines, step, 20);
        Assert.Empty(errors);
    }

    [Fact]
    public void ExtractMeaningfulErrors_BinaryDepMismatch_CapturesLookahead()
    {
        var lines = BuildStepLines(
            "Checking binary dependencies...",
            "Mismatch found in ebpfapi.dll:",
            "  api-ms-win-core-com-l1-1-0.dll",
            "  api-ms-win-core-debug-l1-1-0.dll",
            "Other output");
        var step = MakeStep(lines, 0, lines.Length - 1);

        var errors = LogParser.ExtractMeaningfulErrors(lines, step, 20);
        Assert.Contains(errors, e => e.Contains("Mismatch found"));
        // Lookahead should capture following dependency lines
        Assert.Contains(errors, e => e.Contains("api-ms-win-core-com"));
    }

    // ── Helpers ─────────────────────────────────────────────────

    private static string[] BuildStepLines(params string[] content)
    {
        return content;
    }

    private static ParsedStep MakeStep(string[] lines, int start, int end)
    {
        var step = new ParsedStep
        {
            Number = 1,
            Name = "Test Step",
            StartLine = start,
            EndLine = end,
        };
        // Collect ##[error] lines like ParseSteps does
        for (int i = start; i <= end && i < lines.Length; i++)
        {
            var stripped = LogParser.StripTimestamp(lines[i]);
            if (stripped.StartsWith("##[error]"))
                step.Errors.Add(stripped["##[error]".Length..]);
        }
        return step;
    }
}
