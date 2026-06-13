// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json.Nodes;
using MsBuildMcp.Engine;
using MsBuildMcp.Tools;

namespace MsBuildMcp.Tests;

public class BuildToolsTests
{
    private static BuildDiagnostic MakeError(int index) => new()
    {
        File = $"file{index}.cpp",
        Line = index,
        Severity = DiagnosticSeverity.Error,
        Code = $"C{2000 + index}",
        Message = $"error message {index}",
    };

    private static BuildDiagnostic MakeWarning(int index) => new()
    {
        File = $"file{index}.cpp",
        Line = index,
        Severity = DiagnosticSeverity.Warning,
        Code = $"C{4000 + index}",
        Message = $"warning message {index}",
    };

    private static BuildStatus MakeStatus(int errorCount, int warningCount) => new()
    {
        BuildId = "test-1",
        Status = "failed",
        ElapsedMs = 1000,
        ErrorCount = errorCount,
        WarningCount = warningCount,
        Errors = Enumerable.Range(0, errorCount).Select(MakeError).ToList(),
        Warnings = Enumerable.Range(0, warningCount).Select(MakeWarning).ToList(),
        Command = "msbuild test.sln",
    };

    [Fact]
    public void DefaultsReturnFirstErrorsAndWarnings()
    {
        var status = MakeStatus(errorCount: 10, warningCount: 5);

        var json = BuildTools.StatusToJson(status);

        Assert.Equal(10, json["error_count"]!.GetValue<int>());
        Assert.Equal(5, json["warning_count"]!.GetValue<int>());
        Assert.Equal(10, json["errors"]!.AsArray().Count);
        Assert.Equal(5, json["warnings"]!.AsArray().Count);
    }

    [Fact]
    public void CursorAtExactCountReturnsEmptyArrays()
    {
        var status = MakeStatus(errorCount: 5, warningCount: 10);

        var json = BuildTools.StatusToJson(status, errorsFrom: 5, warningsFrom: 10);

        // Counts always reflect totals
        Assert.Equal(5, json["error_count"]!.GetValue<int>());
        Assert.Equal(10, json["warning_count"]!.GetValue<int>());
        // But arrays are empty — nothing new
        Assert.Empty(json["errors"]!.AsArray());
        Assert.Empty(json["warnings"]!.AsArray());
    }

    [Fact]
    public void CursorAtMidpointReturnsOnlyNewItems()
    {
        var status = MakeStatus(errorCount: 10, warningCount: 8);

        var json = BuildTools.StatusToJson(status, errorsFrom: 7, warningsFrom: 3);

        Assert.Equal(10, json["error_count"]!.GetValue<int>());
        Assert.Equal(8, json["warning_count"]!.GetValue<int>());
        // 3 new errors (indices 7, 8, 9)
        Assert.Equal(3, json["errors"]!.AsArray().Count);
        Assert.Equal("C2007", json["errors"]![0]!["code"]!.GetValue<string>());
        // 5 new warnings (indices 3, 4, 5, 6, 7)
        Assert.Equal(5, json["warnings"]!.AsArray().Count);
        Assert.Equal("C4003", json["warnings"]![0]!["code"]!.GetValue<string>());
    }

    [Fact]
    public void CursorBeyondCountReturnsEmptyArrays()
    {
        var status = MakeStatus(errorCount: 5, warningCount: 5);

        var json = BuildTools.StatusToJson(status, errorsFrom: 999, warningsFrom: 999);

        Assert.Equal(5, json["error_count"]!.GetValue<int>());
        Assert.Equal(5, json["warning_count"]!.GetValue<int>());
        Assert.Empty(json["errors"]!.AsArray());
        Assert.Empty(json["warnings"]!.AsArray());
    }

    [Fact]
    public void MaxErrorsCapsReturnedErrors()
    {
        var status = MakeStatus(errorCount: 100, warningCount: 0);

        var json = BuildTools.StatusToJson(status, maxErrors: 50);

        Assert.Equal(100, json["error_count"]!.GetValue<int>());
        Assert.Equal(50, json["errors"]!.AsArray().Count);
        // First error returned is index 0
        Assert.Equal("C2000", json["errors"]![0]!["code"]!.GetValue<string>());
    }

    [Fact]
    public void MaxErrorsOverrideAllowsFullList()
    {
        var status = MakeStatus(errorCount: 100, warningCount: 0);

        var json = BuildTools.StatusToJson(status, maxErrors: 200);

        Assert.Equal(100, json["error_count"]!.GetValue<int>());
        Assert.Equal(100, json["errors"]!.AsArray().Count);
    }

    [Fact]
    public void WarningsCappedAtTwenty()
    {
        var status = MakeStatus(errorCount: 0, warningCount: 30);

        var json = BuildTools.StatusToJson(status);

        Assert.Equal(30, json["warning_count"]!.GetValue<int>());
        Assert.Equal(20, json["warnings"]!.AsArray().Count);
    }

    [Fact]
    public void CursorAndCapComposeCorrectly()
    {
        // 100 errors, agent already saw 80, cap at 10 → errors 80-89
        var status = MakeStatus(errorCount: 100, warningCount: 0);

        var json = BuildTools.StatusToJson(status, errorsFrom: 80, maxErrors: 10);

        Assert.Equal(100, json["error_count"]!.GetValue<int>());
        Assert.Equal(10, json["errors"]!.AsArray().Count);
        Assert.Equal("C2080", json["errors"]![0]!["code"]!.GetValue<string>());
        Assert.Equal("C2089", json["errors"]![9]!["code"]!.GetValue<string>());
    }

    [Fact]
    public void ZeroCursorReturnsAll()
    {
        var status = MakeStatus(errorCount: 5, warningCount: 3);

        var json = BuildTools.StatusToJson(status, errorsFrom: 0, warningsFrom: 0);

        Assert.Equal(5, json["error_count"]!.GetValue<int>());
        Assert.Equal(3, json["warning_count"]!.GetValue<int>());
        Assert.Equal(5, json["errors"]!.AsArray().Count);
        Assert.Equal(3, json["warnings"]!.AsArray().Count);
    }

    [Fact]
    public void MaxErrorsZero_ReturnsEmptyErrors()
    {
        var status = MakeStatus(errorCount: 10, warningCount: 2);

        var json = BuildTools.StatusToJson(status, maxErrors: 0);

        Assert.Equal(10, json["error_count"]!.GetValue<int>());
        Assert.Empty(json["errors"]!.AsArray());
        Assert.Equal(2, json["warnings"]!.AsArray().Count);
    }

    [Theory]
    [InlineData(30, 30, false)]
    [InlineData(45, 45, false)]
    [InlineData(46, 45, true)]
    [InlineData(120, 45, true)]
    [InlineData(0, 0, false)]
    public void ClampTimeout_ClampsAboveMax(int requested, int expectedTimeout, bool expectedClamped)
    {
        var (timeout, wasClamped) = BuildTools.ClampTimeout(requested);

        Assert.Equal(expectedTimeout, timeout);
        Assert.Equal(expectedClamped, wasClamped);
    }
}

public class OutputBufferTests : IDisposable
{
    private readonly List<OutputBuffer> _buffers = [];

    private OutputBuffer CreateBuffer(string mode = "full")
    {
        var buf = new OutputBuffer(mode, $"test-{_buffers.Count}");
        _buffers.Add(buf);
        return buf;
    }

    public void Dispose()
    {
        foreach (var b in _buffers) b.Dispose();
    }

    [Fact]
    public void FullMode_RetainsAllLines()
    {
        var buf = CreateBuffer();
        for (int i = 1; i <= 100; i++)
            buf.AddLine($"line {i}");

        Assert.Equal(100, buf.TotalLinesReceived);
        Assert.Equal(100, buf.RetainedLineCount);
        Assert.Equal(1, buf.FirstAvailableLine);
        Assert.False(buf.HasDiskSpill);
    }

    [Fact]
    public void TailMode_EvictsOldLines()
    {
        var buf = CreateBuffer("tail");
        for (int i = 1; i <= 2000; i++)
            buf.AddLine($"line {i}");

        Assert.Equal(2000, buf.TotalLinesReceived);
        Assert.Equal(1000, buf.RetainedLineCount); // TailModeMaxLines
        Assert.Equal(1001, buf.FirstAvailableLine);
        Assert.False(buf.HasDiskSpill);
    }

    [Fact]
    public void GetTail_ReturnsLastNLines()
    {
        var buf = CreateBuffer();
        for (int i = 1; i <= 50; i++)
            buf.AddLine($"line {i}");

        var slice = buf.GetTail(10);
        Assert.Equal(10, slice.Lines.Count);
        Assert.Equal("line 41", slice.Lines[0]);
        Assert.Equal("line 50", slice.Lines[9]);
        Assert.Equal(41, slice.FromLine);
        Assert.Equal(50, slice.ToLine);
    }

    [Fact]
    public void GetLines_ReturnsRange()
    {
        var buf = CreateBuffer();
        for (int i = 1; i <= 50; i++)
            buf.AddLine($"line {i}");

        var slice = buf.GetLines(10, 15);
        Assert.Equal(6, slice.Lines.Count);
        Assert.Equal("line 10", slice.Lines[0]);
        Assert.Equal("line 15", slice.Lines[5]);
    }

    [Fact]
    public void GetAroundLine_CentersCorrectly()
    {
        var buf = CreateBuffer();
        for (int i = 1; i <= 100; i++)
            buf.AddLine($"line {i}");

        var slice = buf.GetAroundLine(50, 10);
        Assert.Equal(10, slice.Lines.Count);
        Assert.Contains("line 50", slice.Lines);
    }

    [Fact]
    public void Search_FindsMatchesWithContext()
    {
        var buf = CreateBuffer();
        for (int i = 1; i <= 20; i++)
            buf.AddLine(i == 10 ? "error C2039: not found" : $"normal line {i}");

        var result = buf.Search("error C\\d+", contextLines: 2);

        Assert.Equal(1, result.TotalMatches);
        Assert.Single(result.Matches);
        Assert.Equal(10, result.Matches[0].Line);
        Assert.Contains("C2039", result.Matches[0].Text);
        Assert.True(result.Matches[0].Context.Count > 0);
    }

    [Fact]
    public void Search_Pagination()
    {
        var buf = CreateBuffer();
        for (int i = 1; i <= 50; i++)
            buf.AddLine($"error line {i}");

        var result = buf.Search("error", maxResults: 10, skip: 5);

        Assert.Equal(50, result.TotalMatches);
        Assert.Equal(10, result.Matches.Count);
        Assert.Equal(6, result.Matches[0].Line); // 1-indexed, skipped 5
    }

    [Fact]
    public void FindFirstMatch_ReturnsLineNumber()
    {
        var buf = CreateBuffer();
        for (int i = 1; i <= 20; i++)
            buf.AddLine(i == 15 ? "FATAL ERROR here" : $"ok {i}");

        var line = buf.FindFirstMatch("FATAL");
        Assert.Equal(15, line);
    }

    [Fact]
    public void SaveTo_WritesAllLines()
    {
        var buf = CreateBuffer();
        for (int i = 1; i <= 10; i++)
            buf.AddLine($"line {i}");

        var path = Path.Combine(Path.GetTempPath(), $"msbuild-mcp-test-{Guid.NewGuid()}.txt");
        try
        {
            var (written, bytes) = buf.SaveTo(path);
            Assert.Equal(10, written);
            Assert.True(bytes > 0);
            var fileLines = File.ReadAllLines(path);
            Assert.Equal(10, fileLines.Length);
            Assert.Equal("line 1", fileLines[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Free_ClearsBuffer()
    {
        var buf = CreateBuffer();
        for (int i = 1; i <= 10; i++)
            buf.AddLine($"line {i}");

        var freed = buf.Free();
        Assert.Equal(10, freed);
        Assert.True(buf.IsFreed);
        Assert.Equal(0, buf.RetainedLineCount);

        // Search after free returns empty
        var result = buf.Search("line");
        Assert.Empty(result.Matches);
        Assert.True(result.Freed);
    }

    [Fact]
    public void OutputLine_SetOnParsedDiagnostics()
    {
        // Verify that the BuildDiagnostic can carry OutputLine
        var diag = new BuildDiagnostic
        {
            File = "test.cpp",
            Severity = DiagnosticSeverity.Error,
            Code = "C2039",
            Message = "test error",
            OutputLine = 42,
        };
        Assert.Equal(42, diag.OutputLine);
    }

    [Fact]
    public void DiagnosticToJson_IncludesOutputLine()
    {
        var status = new BuildStatus
        {
            BuildId = "test-1",
            Status = "failed",
            ElapsedMs = 1000,
            ErrorCount = 1,
            WarningCount = 0,
            Errors = [new BuildDiagnostic
            {
                File = "test.cpp",
                Line = 10,
                Severity = DiagnosticSeverity.Error,
                Code = "C2039",
                Message = "test",
                OutputLine = 527,
            }],
            Warnings = [],
            Command = "msbuild test.sln",
        };

        var json = BuildTools.StatusToJson(status);
        var error = json["errors"]![0]!;
        Assert.Equal(527, error["output_line"]!.GetValue<int>());
    }

    [Fact]
    public void OversizedLine_TruncatedInMemory_PreservedOnDisk()
    {
        var buf = CreateBuffer();
        buf.AddLine("normal line 1");

        // Create a line just over the 1MB cap
        var oversized = new string('X', (int)OutputBuffer.MaxLineLengthBytes + 1000);
        buf.AddLine(oversized);
        buf.AddLine("normal line 3");

        Assert.Equal(3, buf.TotalLinesReceived);
        Assert.True(buf.HasTruncatedLines);
        Assert.True(buf.HasDiskSpill);

        // Memory version should be truncated with marker
        var slice = buf.GetLines(1, 3);
        Assert.Equal(3, slice.Lines.Count);
        Assert.Equal("normal line 1", slice.Lines[0]);
        Assert.Contains("LINE TRUNCATED", slice.Lines[1]);
        Assert.Contains("bytes total", slice.Lines[1]);
        Assert.Equal("normal line 3", slice.Lines[2]);

        // Memory version should be shorter than the original
        Assert.True(Encoding.UTF8.GetByteCount(slice.Lines[1]) < oversized.Length);

        // Disk should have the FULL oversized line — verify via search
        var result = buf.Search("XXXXXX");
        Assert.True(result.TotalMatches > 0, "Search should find content in oversized line via disk");
    }

    [Fact]
    public void OversizedLine_SearchFindsPatternsViaDisk()
    {
        var buf = CreateBuffer();
        buf.AddLine("header line");

        // Place the unique pattern AFTER MaxLineLengthBytes so it falls beyond
        // the memory-truncation boundary. The memory copy retains roughly the
        // first 1MB of the line (target = MaxLineLengthBytes − marker length),
        // so a pattern past MaxLineLengthBytes is provably NOT in memory and
        // MUST be located via the disk-first search branch.
        //
        // Adversarial: deleting the `_hasTruncatedLines` disk-first branch in
        // Search and FindFirstMatch makes this test fail (the in-memory scan
        // alone cannot see the pattern).
        var oversized = new string('A', (int)OutputBuffer.MaxLineLengthBytes + 100)
            + "UNIQUE_PATTERN_PAST_TRUNCATION"
            + new string('B', 100);
        buf.AddLine(oversized);
        buf.AddLine("footer line");

        // Sanity: confirm the pattern is indeed missing from the memory copy
        var memorySlice = buf.GetTail(3);
        var truncatedInMemory = memorySlice.Lines.First(l => l.StartsWith("AAAA"));
        Assert.DoesNotContain("UNIQUE_PATTERN_PAST_TRUNCATION", truncatedInMemory);

        var result = buf.Search("UNIQUE_PATTERN_PAST_TRUNCATION");
        Assert.Equal(1, result.TotalMatches);
        Assert.Equal(2, result.Matches[0].Line); // line 2 is the oversized line

        // FindFirstMatch should also work via disk
        var firstMatch = buf.FindFirstMatch("UNIQUE_PATTERN_PAST_TRUNCATION");
        Assert.Equal(2, firstMatch);
    }

    [Fact]
    public void OversizedLine_MemoryStaysBounded()
    {
        var buf = CreateBuffer();

        // Add a line that's 2x the memory cap
        var huge = new string('Z', (int)OutputBuffer.MaxMemoryBytes * 2);
        buf.AddLine(huge);

        // The truncated version in memory should be ~1MB, not ~100MB
        var slice = buf.GetTail(1);
        Assert.Single(slice.Lines);
        var memoryLineBytes = Encoding.UTF8.GetByteCount(slice.Lines[0]);
        Assert.True(memoryLineBytes <= OutputBuffer.MaxLineLengthBytes * 1.1,
            $"Memory line should be ~1MB but was {memoryLineBytes / 1024 / 1024.0:F1}MB");
    }

    [Fact]
    public void TruncateLine_PreservesUtf8CharBoundary()
    {
        // Create a string with multi-byte UTF-8 characters
        var multiByteContent = new string('\u00E9', 500_000); // é = 2 bytes in UTF-8
        var line = "PREFIX_" + multiByteContent + "_SUFFIX";

        var truncated = OutputBuffer.TruncateLine(line, 100_000, diskBacked: true);
        Assert.Contains("LINE TRUNCATED", truncated);

        // Verify the truncated string is valid (no partial multi-byte chars)
        var bytes = Encoding.UTF8.GetBytes(truncated);
        var roundTrip = Encoding.UTF8.GetString(bytes);
        Assert.Equal(truncated, roundTrip);
    }

    [Fact]
    public void TailMode_OversizedLine_TruncatedInMemory()
    {
        var buf = CreateBuffer("tail");
        var oversized = new string('Y', (int)OutputBuffer.MaxLineLengthBytes + 5000);
        buf.AddLine(oversized);

        Assert.True(buf.HasTruncatedLines);
        // Tail mode has no disk spill — truncation is the only option
        Assert.False(buf.HasDiskSpill);

        var slice = buf.GetTail(1);
        Assert.Single(slice.Lines);
        Assert.Contains("LINE TRUNCATED", slice.Lines[0]);
        // The marker must honestly admit that no disk copy exists in tail mode
        // and must point the agent at the way to preserve future overflow.
        Assert.Contains("overflow discarded", slice.Lines[0]);
        Assert.Contains("retention=full", slice.Lines[0]);
        Assert.DoesNotContain("preserved in build log on disk", slice.Lines[0]);
    }

    [Fact]
    public void FullMode_OversizedLine_MarkerStatesDiskBacked()
    {
        var buf = CreateBuffer();
        var oversized = new string('Z', (int)OutputBuffer.MaxLineLengthBytes + 5000);
        buf.AddLine(oversized);

        Assert.True(buf.HasTruncatedLines);
        Assert.True(buf.HasDiskSpill); // full mode spills to disk

        var slice = buf.GetTail(1);
        Assert.Single(slice.Lines);
        Assert.Contains("LINE TRUNCATED", slice.Lines[0]);
        // The marker must promise the full content lives on disk in full mode
        // and must NOT carry the tail-mode "overflow discarded" language.
        Assert.Contains("preserved in build log on disk", slice.Lines[0]);
        Assert.DoesNotContain("overflow discarded", slice.Lines[0]);
    }

    [Fact]
    public void TruncateLine_DiskBackedFlag_SwitchesMarker()
    {
        var line = new string('A', 1000);
        var diskMarker = OutputBuffer.TruncateLine(line, 200, diskBacked: true);
        var noDiskMarker = OutputBuffer.TruncateLine(line, 200, diskBacked: false);

        Assert.Contains("preserved in build log on disk", diskMarker);
        Assert.DoesNotContain("overflow discarded", diskMarker);

        Assert.Contains("overflow discarded", noDiskMarker);
        Assert.Contains("retention=full", noDiskMarker);
        Assert.DoesNotContain("preserved in build log on disk", noDiskMarker);
    }
}
