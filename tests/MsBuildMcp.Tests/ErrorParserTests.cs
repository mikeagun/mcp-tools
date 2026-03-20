using MsBuildMcp.Engine;

namespace MsBuildMcp.Tests;

public class ErrorParserTests
{
    [Fact]
    public void ParsesStandardError()
    {
        var output = @"C:\git\libs\api\Verifier.cpp(42,5): error C2039: 'verify': is not a member of 'prevail' [C:\git\libs\user\api.vcxproj]";
        var results = ErrorParser.Parse(output);

        Assert.Single(results);
        var d = results[0];
        Assert.Equal(@"C:\git\libs\api\Verifier.cpp", d.File);
        Assert.Equal(42, d.Line);
        Assert.Equal(5, d.Column);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Equal("C2039", d.Code);
        Assert.Contains("'verify'", d.Message);
        Assert.Equal(@"C:\git\libs\user\api.vcxproj", d.Project);
    }

    [Fact]
    public void ParsesWarning()
    {
        var output = @"C:\git\src\foo.cpp(10): warning C4996: 'deprecated_func': was declared deprecated [C:\git\proj.vcxproj]";
        var results = ErrorParser.Parse(output);

        Assert.Single(results);
        Assert.Equal(DiagnosticSeverity.Warning, results[0].Severity);
        Assert.Equal("C4996", results[0].Code);
    }

    [Fact]
    public void ParsesLinkerError()
    {
        var output = @"LINK : error LNK1181: cannot open input file 'foo.lib' [C:\git\proj.vcxproj]";
        var results = ErrorParser.Parse(output);

        Assert.Single(results);
        Assert.Equal("LNK1181", results[0].Code);
        Assert.Equal(DiagnosticSeverity.Error, results[0].Severity);
    }

    [Fact]
    public void ParsesMultipleErrors()
    {
        var output = """
            Build started.
            C:\src\a.cpp(1,1): error C1234: msg1 [C:\proj1.vcxproj]
            C:\src\b.cpp(2): warning C5678: msg2 [C:\proj2.vcxproj]
            C:\src\c.cpp(3,10): error C9012: msg3 [C:\proj1.vcxproj]
            Build completed.
            """;
        var results = ErrorParser.Parse(output);

        Assert.Equal(3, results.Count);
        Assert.Equal(2, results.Count(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Equal(1, results.Count(d => d.Severity == DiagnosticSeverity.Warning));
    }

    [Fact]
    public void IgnoresNonErrorLines()
    {
        var output = """
            Build started 1/1/2026 12:00:00 AM.
            Project "foo.vcxproj" on node 1 (default targets).
            Done Building Project "foo.vcxproj" (default targets).
            Build succeeded.
                0 Warning(s)
                0 Error(s)
            """;
        var results = ErrorParser.Parse(output);
        Assert.Empty(results);
    }

    [Fact]
    public void ParsesErrorWithoutProject()
    {
        var output = @"C:\src\a.cpp(5,1): error C1234: some message";
        var results = ErrorParser.Parse(output);

        Assert.Single(results);
        Assert.Null(results[0].Project);
    }

    [Fact]
    public void ParsesEmptyInput()
    {
        Assert.Empty(ErrorParser.Parse(""));
        Assert.Empty(ErrorParser.Parse("   \n\n  "));
    }

    [Fact]
    public void ParsesErrorWithLineNoColumn()
    {
        var output = @"file.cpp(42): error C1234: msg [C:\proj.vcxproj]";
        var results = ErrorParser.Parse(output);

        Assert.Single(results);
        var d = results[0];
        Assert.Equal("file.cpp", d.File);
        Assert.Equal(42, d.Line);
        Assert.Null(d.Column);
        Assert.Equal("C1234", d.Code);
    }

    [Fact]
    public void RawLinePreserved()
    {
        var output = @"C:\src\a.cpp(1,2): error C1000: something broke [C:\proj.vcxproj]";
        var results = ErrorParser.Parse(output);

        Assert.Single(results);
        Assert.Equal(output, results[0].RawLine);
    }

    [Fact]
    public void ParsesMultipleOnSameFile()
    {
        var output = """
            C:\src\shared.cpp(10,1): error C1001: first error [C:\proj.vcxproj]
            C:\src\shared.cpp(20,5): error C1002: second error [C:\proj.vcxproj]
            """;
        var results = ErrorParser.Parse(output);

        Assert.Equal(2, results.Count);
        Assert.All(results, d => Assert.Equal(@"C:\src\shared.cpp", d.File));
        Assert.Equal("C1001", results[0].Code);
        Assert.Equal("C1002", results[1].Code);
    }
}
