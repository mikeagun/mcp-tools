using System.Text.RegularExpressions;

namespace CiDebugMcp.Engine;

/// <summary>
/// A parsed step from a GitHub Actions job log.
/// </summary>
public sealed class ParsedStep
{
    public required int Number { get; init; }
    public required string Name { get; init; }
    public int StartLine { get; init; }
    public int EndLine { get; set; }
    public string? Conclusion { get; set; }
    public List<string> Errors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

/// <summary>
/// Parses GitHub Actions log format into structured steps.
/// </summary>
public static partial class LogParser
{
    // GitHub Actions log line format: "2026-03-03T22:07:45.1234567Z content"
    private static readonly Regex TimestampPrefix = TimestampRegex();

    // Step group markers
    private const string GroupStart = "##[group]";
    private const string GroupEnd = "##[endgroup]";
    private const string ErrorMarker = "##[error]";
    private const string WarningMarker = "##[warning]";

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+Z\s?")]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"\x1b\[[0-9;]*m")]
    private static partial Regex AnsiEscapeRegex();

    /// <summary>
    /// Strip timestamp prefix and ANSI escape codes from a log line.
    /// </summary>
    public static string StripTimestamp(string line)
    {
        var stripped = TimestampPrefix.Replace(line, "").TrimEnd('\r');
        return AnsiEscapeRegex().Replace(stripped, "");
    }

    /// <summary>
    /// Parse a full job log into steps with line numbers.
    /// </summary>
    public static ParsedStep[] ParseSteps(string[] lines)
    {
        var steps = new List<ParsedStep>();
        ParsedStep? current = null;
        int stepNumber = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var stripped = StripTimestamp(lines[i]);

            if (stripped.StartsWith(GroupStart))
            {
                // Close previous step
                if (current != null)
                {
                    current.EndLine = i - 1;
                    steps.Add(current);
                }

                stepNumber++;
                current = new ParsedStep
                {
                    Number = stepNumber,
                    Name = stripped[GroupStart.Length..],
                    StartLine = i,
                };
            }
            else if (stripped == GroupEnd)
            {
                // GroupEnd is part of the step but marks its visual boundary
            }
            else if (current != null)
            {
                if (stripped.StartsWith(ErrorMarker))
                {
                    current.Errors.Add(stripped[ErrorMarker.Length..]);
                }
                else if (stripped.StartsWith(WarningMarker))
                {
                    current.Warnings.Add(stripped[WarningMarker.Length..]);
                }
            }
        }

        // Close last step
        if (current != null)
        {
            current.EndLine = lines.Length - 1;
            steps.Add(current);
        }

        return [.. steps];
    }

    /// <summary>
    /// Parse common build error formats into structured data.
    /// Supports MSVC, GCC/Clang, and MSBuild error formats.
    /// </summary>
    public static ParsedError? TryParseError(string line)
    {
        // Strip MSBuild project-number prefix (e.g., "113>" or "  42>")
        var cleaned = MsbuildPrefixRegex().Replace(line, "").TrimStart();

        // MSVC: "file(line,col): error C1234: message"
        var msvc = MsvcErrorRegex().Match(cleaned);
        if (msvc.Success)
        {
            return new ParsedError
            {
                Type = "msvc",
                Code = msvc.Groups["code"].Value,
                Message = msvc.Groups["msg"].Value,
                File = msvc.Groups["file"].Value.Trim(),
                Line = int.TryParse(msvc.Groups["line"].Value, out var l) ? l : null,
            };
        }

        // GCC/Clang: "file:line:col: error: message"
        var gcc = GccErrorRegex().Match(cleaned);
        if (gcc.Success)
        {
            return new ParsedError
            {
                Type = "gcc",
                Message = gcc.Groups["msg"].Value,
                File = gcc.Groups["file"].Value.Trim(),
                Line = int.TryParse(gcc.Groups["line"].Value, out var l2) ? l2 : null,
            };
        }

        return null;
    }

    /// <summary>
    /// Regex patterns for meaningful error lines in CI logs.
    /// </summary>
    private static readonly Regex[] MeaningfulErrorPatterns =
    [
        MsvcErrorRegex(),
        GccErrorRegex(),
        MsvcLinkerErrorRegex(),
        TestFailureRegex(),
        ScriptErrorRegex(),
        Catch2FailedRegex(),
        Catch2AssertRegex(),
        BinaryDepMismatchRegex(),
    ];

    /// <summary>
    /// Patterns that trigger lookahead — when matched, grab following lines for context.
    /// </summary>
    private static readonly (Regex pattern, int lookahead)[] LookaheadPatterns =
    [
        (Catch2FailedRegex(), 4),        // FAILED: + REQUIRE(...) + expansion + location
        (BinaryDepMismatchRegex(), 6),   // Mismatch + Missing/Extra deps lines
    ];

    /// <summary>
    /// Known runner boilerplate lines that should be treated as setup noise.
    /// </summary>
    private static readonly string[] SetupBoilerplate =
    [
        "Prepare all required actions",
        "Getting action download info",
        "Complete job",
        "Cleaning up orphan processes",
        "Post job cleanup",
        "Evaluate and set job outputs",
        "Set output '",
        "Removing credentials config",
        "Removing SSH command configuration",
        "Temporarily overriding HOME=",
    ];

    /// <summary>
    /// Check if a line is a meaningful error (not boilerplate).
    /// </summary>
    public static bool IsMeaningfulError(string line)
    {
        // Reject passed-test timing lines and test-passed summaries
        if (PassedTestNoiseRegex().IsMatch(line)) return false;

        return MeaningfulErrorPatterns.Any(p => p.IsMatch(line));
    }

    /// <summary>
    /// Extract meaningful errors from a step's log lines.
    /// Returns real error lines (compiler errors, test failures, mismatches)
    /// instead of useless ##[error] annotations.
    /// Performs lookahead for multi-line errors (Catch2 failures, binary dep mismatches).
    /// Falls back to the last non-blank lines before ##[error] if no patterns match.
    /// </summary>
    public static List<string> ExtractMeaningfulErrors(string[] allLines, ParsedStep step, int maxErrors)
    {
        var errors = new List<string>();
        int start = step.StartLine;
        int end = step.EndLine;
        // Scan the full step range — don't stop at maxErrors during scanning.
        // We'll truncate at the end. Use a generous internal cap to avoid runaway memory.
        int scanCap = Math.Max(maxErrors * 5, 100);

        // Pass 1: Find lines matching known error patterns, with lookahead
        for (int i = start; i <= end && i < allLines.Length && errors.Count < scanCap; i++)
        {
            var stripped = StripTimestamp(allLines[i]).Trim();
            if (string.IsNullOrEmpty(stripped)) continue;
            if (stripped.StartsWith(GroupStart) || stripped == GroupEnd) continue;
            if (stripped.StartsWith(ErrorMarker))
            {
                var msg = stripped[ErrorMarker.Length..];
                if (msg.StartsWith("Process completed with exit code")) continue;
                // Filter env var assignments (UPPER_SNAKE_CASE=value or UPPER_SNAKE_CASE: value)
                if (IsEnvVarLine(msg)) continue;
                errors.Add(msg);
                continue;
            }

            // Check for lookahead patterns first (multi-line errors)
            bool handled = false;
            foreach (var (pattern, lookahead) in LookaheadPatterns)
            {
                if (pattern.IsMatch(stripped))
                {
                    errors.Add(stripped);
                    // Grab following lines as context
                    for (int j = i + 1; j <= Math.Min(i + lookahead, end) && j < allLines.Length && errors.Count < scanCap; j++)
                    {
                        var next = StripTimestamp(allLines[j]).Trim();
                        if (string.IsNullOrEmpty(next)) continue;
                        if (next.StartsWith(GroupStart) || next.StartsWith("##[") ||
                            next.StartsWith("Checking binary")) break;
                        errors.Add(next);
                    }
                    handled = true;
                    break;
                }
            }
            if (handled) continue;

            // Single-line error patterns
            if (IsMeaningfulError(stripped))
            {
                errors.Add(stripped);
            }
        }

        // Pass 2: If no patterns matched, take error-like lines from the tail,
        // or fall back to the last non-blank lines before ##[error]Process completed
        if (errors.Count == 0)
        {
            // First try: scan backward for lines containing error/fail/exception keywords
            var errorTail = new List<string>();
            for (int i = end; i >= start && errorTail.Count < maxErrors; i--)
            {
                var stripped = StripTimestamp(allLines[i]).Trim();
                if (string.IsNullOrEmpty(stripped)) continue;
                if (stripped.StartsWith(ErrorMarker)) continue;
                if (stripped.StartsWith(GroupStart) || stripped == GroupEnd) continue;
                if (IsEnvVarLine(stripped)) continue;
                if (HasErrorKeyword(stripped))
                    errorTail.Add(stripped);
            }

            if (errorTail.Count > 0)
            {
                errorTail.Reverse();
                errors.AddRange(errorTail);
            }
            else
            {
                // Last resort: take the last non-blank, non-env-var lines
                var tail = new List<string>();
                for (int i = end; i >= start && tail.Count < maxErrors; i--)
                {
                    var stripped = StripTimestamp(allLines[i]).Trim();
                    if (string.IsNullOrEmpty(stripped)) continue;
                    if (stripped.StartsWith(ErrorMarker)) continue;
                    if (stripped.StartsWith(GroupStart) || stripped == GroupEnd) continue;
                    if (IsEnvVarLine(stripped)) continue;
                    tail.Add(stripped);
                }
                tail.Reverse();
                errors.AddRange(tail);
            }
        }

        // Deduplicate: by parsed error key (code+file+line) for structured errors,
        // exact string match for unstructured lines. Preserves order.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<string>();
        foreach (var err in errors)
        {
            var parsed = TryParseError(err);
            var key = parsed != null
                ? $"{parsed.Code}|{parsed.File}|{parsed.Line}"
                : err;
            if (seen.Add(key))
                deduped.Add(err);
        }

        // Truncate to requested max after dedup
        if (deduped.Count > maxErrors)
            deduped.RemoveRange(maxErrors, deduped.Count - maxErrors);
        return deduped;
    }

    /// <summary>
    /// Detect environment variable assignments (UPPER_SNAKE_CASE=value, UPPER_SNAKE_CASE: value).
    /// These appear in ##[error] lines from tools like CodeQL but aren't actual errors.
    /// </summary>
    private static bool IsEnvVarLine(string line)
    {
        // Match: CODEQL_EXTRACTOR_CPP_ROOT=/path, JAVA_HOME: /usr/lib, etc.
        if (line.Length < 3) return false;
        var sep = line.IndexOfAny(['=', ':']);
        if (sep < 2 || sep > 60) return false;
        var key = line[..sep].Trim();
        // Key must be UPPER_SNAKE_CASE (all uppercase letters, digits, underscores)
        if (key.Length < 2) return false;
        foreach (var c in key)
        {
            if (c is not (>= 'A' and <= 'Z' or >= '0' and <= '9' or '_')) return false;
        }
        return true;
    }

    /// <summary>
    /// Check if a line contains error/failure keywords (for tail fallback prioritization).
    /// </summary>
    private static bool HasErrorKeyword(string line) =>
        line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("REQUIRE(", StringComparison.Ordinal) ||
        line.Contains("CRASHED", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("fatal", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Check if a log line is setup/boilerplate noise.
    /// Checks both ##[group] blocks and known runner boilerplate patterns.
    /// </summary>
    public static bool IsInSetupBlock(string[] lines, int lineIndex)
    {
        var stripped = StripTimestamp(lines[lineIndex]);

        // Check against known boilerplate strings
        if (SetupBoilerplate.Any(b => stripped.Contains(b, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Check if inside a ##[group]...##[endgroup] block
        for (int i = lineIndex; i >= Math.Max(0, lineIndex - 50); i--)
        {
            var s = StripTimestamp(lines[i]);
            if (s == GroupEnd) return false;
            if (s.StartsWith(GroupStart)) return true;
        }
        return false;
    }

    /// <summary>
    /// Extract Catch2 test case name by scanning backwards from a FAILED: line.
    /// Catch2 format:
    ///   -------------------------------------------------------------------------------
    ///   Test case name here
    ///   -------------------------------------------------------------------------------
    ///   source_file.cpp(line)
    ///   ...............................................................................
    ///   source_file.cpp(line): FAILED:
    /// Also handles gtest format: [ RUN      ] TestSuite.TestName
    /// </summary>
    public static string? ExtractTestCaseName(string[] lines, int failedLineIdx, int stepStart)
    {
        int scanStart = Math.Max(stepStart, failedLineIdx - 30);
        int separatorCount = 0;

        for (int i = failedLineIdx - 1; i >= scanStart; i--)
        {
            var line = StripTimestamp(lines[i]).Trim();

            // Catch2: count separator lines (--- or ...) going backwards
            if (line.Length > 10 && (line.All(c => c == '-') || line.All(c => c == '.')))
            {
                separatorCount++;
                // After the second separator (the one above the test name),
                // the test name is the line BELOW this separator (i+1)
                if (separatorCount >= 2 && i + 1 < failedLineIdx)
                {
                    var candidate = StripTimestamp(lines[i + 1]).Trim();
                    if (!string.IsNullOrEmpty(candidate) &&
                        !candidate.All(c => c == '-') && !candidate.All(c => c == '.') &&
                        !candidate.Contains('(')) // not a source_file.cpp(line) reference
                        return candidate;
                }
            }

            // gtest: "[ RUN      ] TestSuite.TestName"
            if (line.StartsWith("[ RUN") && line.Contains(']'))
            {
                var nameStart = line.IndexOf(']') + 1;
                if (nameStart < line.Length)
                    return line[nameStart..].Trim();
            }
        }
        return null;
    }

    /// <summary>
    /// Extract all failed test case names from a step's log lines.
    /// Scans for Catch2 FAILED: sections and gtest [ FAILED ] lines.
    /// </summary>
    public static List<string> ExtractFailedTestNames(string[] allLines, ParsedStep step)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = step.StartLine; i <= step.EndLine && i < allLines.Length; i++)
        {
            var line = StripTimestamp(allLines[i]).Trim();

            // Catch2: "FAILED:" line → scan backwards for test name
            if (Catch2FailedRegex().IsMatch(line))
            {
                var name = ExtractTestCaseName(allLines, i, step.StartLine);
                if (name != null && seen.Add(name))
                    names.Add(name);
            }

            // gtest: "[  FAILED  ] TestSuite.TestName (123 ms)"
            if (line.StartsWith("[  FAILED") && line.Contains(']'))
            {
                var nameStart = line.IndexOf(']') + 1;
                if (nameStart < line.Length)
                {
                    var name = line[nameStart..].Trim();
                    // Strip trailing timing info "(123 ms)"
                    var parenIdx = name.LastIndexOf('(');
                    if (parenIdx > 0) name = name[..parenIdx].Trim();
                    if (name.Length > 0 && !name.All(char.IsDigit) && seen.Add(name))
                        names.Add(name);
                }
            }
        }

        return names;
    }

    /// <summary>
    /// Suggest a targeted search pattern based on the type of errors found.
    /// </summary>
    public static string SuggestSearchPattern(List<string> errors)
    {
        bool hasCompiler = errors.Any(e => MsvcErrorRegex().IsMatch(e) || GccErrorRegex().IsMatch(e) || MsvcLinkerErrorRegex().IsMatch(e));
        bool hasDeps = errors.Any(e => BinaryDepMismatchRegex().IsMatch(e));
        bool hasTest = errors.Any(e => Catch2FailedRegex().IsMatch(e) || Catch2AssertRegex().IsMatch(e) || TestSummaryRegex().IsMatch(e));

        if (hasCompiler) return "error C|error LNK|fatal error";
        if (hasDeps) return "Mismatch|Extra Dependencies|Missing Dependencies";
        if (hasTest) return "FAILED:|REQUIRE\\(|CHECK\\(|CRASHED";
        return "FAIL|Mismatch|Exception|error";
    }

    /// <summary>
    /// Classify a step as setup/infrastructure based on its name.
    /// </summary>
    public static bool IsSetupStep(string stepName)
    {
        var lower = stepName.ToLowerInvariant();
        return lower.Contains("checkout") || lower.Contains("set up job") ||
               lower.Contains("cache") || lower.Contains("install") ||
               lower.Contains("download") || lower.Contains("setup") ||
               lower.StartsWith("post ") || lower.StartsWith("pre ");
    }

    /// <summary>
    /// Classify a step's likely type from its name.
    /// Returns "build", "test", "deploy", or "unknown".
    /// </summary>
    public static string ClassifyStepType(string stepName)
    {
        var lower = stepName.ToLowerInvariant();
        if (lower.Contains("test") || lower.Contains("unit_test") ||
            lower.Contains("_test") || lower.Contains("bvt") ||
            lower.Contains("regression") || lower.Contains("catch2") ||
            lower.Contains("gtest") || lower.Contains("pytest") ||
            lower.Contains("jest") || lower.Contains("xunit") ||
            lower.Contains("nunit") || lower.Contains("verify") ||
            lower.Contains("stress") || lower.Contains("perf") ||
            lower.Contains("fuzz") || lower.Contains("benchmark") ||
            lower.Contains("conformance"))
            return "test";
        if (lower.Contains("msbuild") || lower.Contains("build") ||
            lower.Contains("compile") || lower.Contains("cmake") ||
            lower.Contains("make") || lower.Contains("cargo") ||
            lower.Contains("dotnet build") || lower.Contains("npm run build"))
            return "build";
        if (lower.Contains("deploy") || lower.Contains("publish") ||
            lower.Contains("release") || lower.Contains("upload"))
            return "deploy";
        return "unknown";
    }

    /// <summary>
    /// Suggest a search pattern for a specific step type.
    /// More targeted than generic 'error|FAIL'.
    /// </summary>
    public static string SuggestPatternForStepType(string stepType)
    {
        return stepType switch
        {
            "test" => "FAILED:|CRASHED|test cases:.*failed|tests? failed|failures?:|ERROR",
            "build" => "error C|error LNK|fatal error|error:",
            _ => "error|FAIL|Exception",
        };
    }

    /// <summary>
    /// Extract test runner summary from step output.
    /// Supports Catch2, gtest, pytest, jest, xunit, and generic patterns.
    /// Returns null if no summary found.
    /// </summary>
    public static TestSummary? ExtractTestSummary(string[] allLines, ParsedStep step)
    {
        // Scan last 100 lines of step (summaries near end, but post-test output can push them up)
        int scanStart = Math.Max(step.StartLine, step.EndLine - 100);

        // gtest: collect both PASSED and FAILED in a single pass
        int gtestPassed = -1, gtestFailed = -1;
        string? gtestLine = null;

        for (int i = step.EndLine; i >= scanStart; i--)
        {
            if (i >= allLines.Length) continue;
            var line = StripTimestamp(allLines[i]).Trim();

            // Catch2: "test cases: 247 | 245 passed | 2 failed"
            var catch2 = Catch2SummaryRegex().Match(line);
            if (catch2.Success)
            {
                return new TestSummary
                {
                    Framework = "catch2",
                    Total = int.TryParse(catch2.Groups["total"].Value, out var t) ? t : 0,
                    Passed = int.TryParse(catch2.Groups["passed"].Value, out var p) ? p : 0,
                    Failed = int.TryParse(catch2.Groups["failed"].Value, out var f) ? f : 0,
                    SummaryLine = line,
                };
            }

            // gtest: collect both "[  PASSED  ] N tests" and "[  FAILED  ] N tests"
            var gtest = GtestSummaryRegex().Match(line);
            if (gtest.Success)
            {
                var count = int.TryParse(gtest.Groups["count"].Value, out var c) ? c : 0;
                var status = gtest.Groups["status"].Value;
                if (status == "FAILED") { gtestFailed = count; gtestLine ??= line; }
                else if (status == "PASSED") { gtestPassed = count; }
            }

            // Generic: "N tests failed" or "N failures"
            var generic = GenericTestSummaryRegex().Match(line);
            if (generic.Success)
            {
                return new TestSummary
                {
                    Framework = "unknown",
                    Failed = int.TryParse(generic.Groups["count"].Value, out var gc) ? gc : 1,
                    SummaryLine = line,
                };
            }
        }

        // Build gtest summary if we found at least the FAILED line
        if (gtestFailed >= 0)
        {
            var passed = gtestPassed >= 0 ? gtestPassed : 0;
            return new TestSummary
            {
                Framework = "gtest",
                Total = passed + gtestFailed,
                Passed = passed,
                Failed = gtestFailed,
                SummaryLine = gtestLine ?? "",
            };
        }

        return null;
    }

    [GeneratedRegex(@"^\s*\d+>")]
    private static partial Regex MsbuildPrefixRegex();

    [GeneratedRegex(@"(?<file>[^(]+)\((?<line>\d+)(?:,\d+)?\)\s*:\s*error\s+(?<code>\w+)\s*:\s*(?<msg>.+)")]
    private static partial Regex MsvcErrorRegex();

    [GeneratedRegex(@"(?<file>[^:]+):(?<line>\d+):\d+:\s*error:\s*(?<msg>.+)")]
    private static partial Regex GccErrorRegex();

    [GeneratedRegex(@"error\s+LNK\d+", RegexOptions.IgnoreCase)]
    private static partial Regex MsvcLinkerErrorRegex();

    [GeneratedRegex(@"\b(FAILED:|CRASHED|Assertion failed|Mismatch found)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TestFailureRegex();

    [GeneratedRegex(@"\b(Exception|not found|permission denied|access denied)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptErrorRegex();

    [GeneratedRegex(@"^\s*FAILED:\s*")]
    private static partial Regex Catch2FailedRegex();

    [GeneratedRegex(@"^\s*(REQUIRE|CHECK|REQUIRE_THROWS|CHECK_THROWS)\s*\(")]
    private static partial Regex Catch2AssertRegex();

    [GeneratedRegex(@"Mismatch found|Extra Dependencies|Missing Dependencies", RegexOptions.IgnoreCase)]
    private static partial Regex BinaryDepMismatchRegex();

    // Test runner summary patterns (framework-agnostic)
    [GeneratedRegex(@"test cases:\s*(?<total>\d+)\s*\|\s*(?<passed>\d+)\s*passed\s*\|\s*(?<failed>\d+)\s*failed", RegexOptions.IgnoreCase)]
    private static partial Regex Catch2SummaryRegex();

    [GeneratedRegex(@"\[\s*(?<status>PASSED|FAILED)\s*\]\s*(?<count>\d+)\s*tests?")]
    private static partial Regex GtestSummaryRegex();

    [GeneratedRegex(@"(?<count>\d+)\s+(?:tests?\s+failed|failures?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex GenericTestSummaryRegex();

    // General test summary detection (for IsMeaningfulError)
    [GeneratedRegex(@"test cases:.*failed|tests?\s+failed|\[\s*FAILED\s*\]\s*\d+\s+tests?|^Failed:\s*\d+", RegexOptions.IgnoreCase)]
    private static partial Regex TestSummaryRegex();

    // Noise patterns: passed-test timing lines, "All tests passed", Catch2 passed summaries, SPD/harden-runner warnings
    [GeneratedRegex(@"^\s*\d+\.\d+\s*s:\s|^All\s+\d+\s+tests?\s+passed|^All\s+tests\s+passed|\[\s*OK\s*\]|\[\s*PASSED\s*\].*\d+\s*tests?.*\bpassed\b|StepSecurity|harden-runner|spd-", RegexOptions.IgnoreCase)]
    private static partial Regex PassedTestNoiseRegex();
}

public sealed class ParsedError
{
    public required string Type { get; init; }
    public string? Code { get; init; }
    public required string Message { get; init; }
    public string? File { get; init; }
    public int? Line { get; init; }
}

public sealed class TestSummary
{
    public required string Framework { get; init; }
    public int Total { get; init; }
    public int Passed { get; init; }
    public int Failed { get; init; }
    public string? SummaryLine { get; init; }
}
