using System.Text.RegularExpressions;

namespace MsBuildMcp.Engine;

/// <summary>
/// Parses MSBuild text output into structured error/warning objects.
/// </summary>
public static class ErrorParser
{
    // Standard MSBuild error format:
    // path(line,col): error CODE: message [project]
    // path(line): error CODE: message [project]
    // LINK : error LNK1234: message [project]
    private static readonly Regex ErrorRegex = new(
        @"^(?<file>[^(]+?)(?:\((?<line>\d+)(?:,(?<col>\d+))?\))?\s*:\s*(?<severity>error|warning)\s+(?<code>\w+)\s*:\s*(?<message>.+?)(?:\s*\[(?<project>[^\]]+)\])?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parse MSBuild text output (multi-line string) into structured diagnostics.
    /// </summary>
    public static List<BuildDiagnostic> Parse(string output)
    {
        var results = new List<BuildDiagnostic>();
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            var match = ErrorRegex.Match(trimmed);
            if (match.Success)
            {
                results.Add(new BuildDiagnostic
                {
                    File = match.Groups["file"].Value.Trim(),
                    Line = match.Groups["line"].Success ? int.Parse(match.Groups["line"].Value) : null,
                    Column = match.Groups["col"].Success ? int.Parse(match.Groups["col"].Value) : null,
                    Severity = match.Groups["severity"].Value.ToLowerInvariant() == "error"
                        ? DiagnosticSeverity.Error
                        : DiagnosticSeverity.Warning,
                    Code = match.Groups["code"].Value,
                    Message = match.Groups["message"].Value.Trim(),
                    Project = match.Groups["project"].Success ? match.Groups["project"].Value.Trim() : null,
                    RawLine = trimmed,
                });
            }
        }
        return results;
    }
}

public sealed class BuildDiagnostic
{
    public required string File { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public required DiagnosticSeverity Severity { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? Project { get; init; }
    public string? RawLine { get; init; }
    /// <summary>Line number in the build output where this diagnostic appeared (1-indexed).</summary>
    public int? OutputLine { get; init; }
}

public enum DiagnosticSeverity
{
    Error,
    Warning,
}
