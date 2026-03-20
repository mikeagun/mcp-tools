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
