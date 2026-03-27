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
}
