using System.Text.Json.Nodes;
using MsBuildMcp.Engine;
using McpSharp;

namespace MsBuildMcp.Tools;

public static class BuildTools
{
    internal const int MaxTimeoutSeconds = 45;

    /// <summary>
    /// Clamp timeout to MaxTimeoutSeconds. Returns the clamped value and whether clamping occurred.
    /// </summary>
    internal static (int timeout, bool wasClamped) ClampTimeout(int requested)
    {
        if (requested > MaxTimeoutSeconds)
            return (MaxTimeoutSeconds, true);
        return (requested, false);
    }

    public static void Register(McpServer server, BuildManager buildManager)
    {
        server.RegisterTool(new ToolInfo
        {
            Name = "build",
            Description = "Start an MSBuild build and wait up to 'timeout' seconds for it to finish. " +
                          "Returns immediately with progress if the build takes longer than the timeout. " +
                          "If a build is already running, polls it instead of starting a new one. " +
                          "Use get_build_status to continue polling, or cancel_build to stop it. " +
                          "Returns early if a new error is detected (so you can cancel and fix without waiting).",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["sln_path"] = new JsonObject { ["type"] = "string", ["description"] = "Path to .sln file" },
                    ["targets"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "MSBuild /t: targets (e.g. 'tools\\bpf2c' or 'drivers\\EbpfCore,tests\\api_test')",
                    },
                    ["configuration"] = new JsonObject { ["type"] = "string", ["default"] = "Debug" },
                    ["platform"] = new JsonObject { ["type"] = "string", ["default"] = "x64" },
                    ["restore"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Add -Restore for NuGet restore",
                        ["default"] = false,
                    },
                    ["additional_args"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Additional MSBuild arguments (e.g. '/p:Analysis=True')",
                    },
                    ["retention"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("full", "tail"),
                        ["description"] = "Output retention: 'full' (default, searchable, up to 50MB in memory " +
                                          "with disk spill) or 'tail' (1K-line ring buffer, lightweight).",
                        ["default"] = "full",
                    },
                    ["timeout"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Max seconds to wait for the build. 0 = start and return immediately. " +
                                          "Default 30, max 45. Returns early if a new error is detected.",
                        ["default"] = 30,
                        ["maximum"] = MaxTimeoutSeconds,
                    },
                    ["errors_from"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Skip this many errors — pass error_count from previous response to get only new errors.",
                        ["default"] = 0,
                    },
                    ["warnings_from"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Skip this many warnings — pass warning_count from previous response to get only new warnings.",
                        ["default"] = 0,
                    },
                    ["max_errors"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Max error items to return (default 50). Set higher for full error lists, lower for polling.",
                        ["default"] = 50,
                    },
                },
                ["required"] = new JsonArray("sln_path"),
            },
            Handler = args =>
            {
                var slnPath = args["sln_path"]!.GetValue<string>();
                var targets = args["targets"]?.GetValue<string>();
                var config = args["configuration"]?.GetValue<string>() ?? "Debug";
                var platform = args["platform"]?.GetValue<string>() ?? "x64";
                var restore = args["restore"]?.GetValue<bool>() ?? false;
                var additionalArgs = args["additional_args"]?.GetValue<string>();
                var retention = args["retention"]?.GetValue<string>() ?? "full";
                var (timeout, timeoutClamped) = ClampTimeout(args["timeout"]?.GetValue<int>() ?? 30);
                var errorsFrom = args["errors_from"]?.GetValue<int>() ?? 0;
                var warningsFrom = args["warnings_from"]?.GetValue<int>() ?? 0;
                var maxErrors = args["max_errors"]?.GetValue<int>() ?? 50;

                var status = buildManager.StartOrPoll(slnPath, targets, config, platform,
                    restore, additionalArgs, timeout, retention: retention);
                var json = StatusToJson(status, errorsFrom, warningsFrom, maxErrors);
                if (timeoutClamped)
                    json["timeout_clamped"] = $"Requested timeout exceeded max ({MaxTimeoutSeconds}s). " +
                        "Use get_build_status to continue polling.";
                return json;
            },
        });

        server.RegisterTool(new ToolInfo
        {
            Name = "get_build_status",
            Description = "Poll the current or most recent build. Set 'timeout' to wait for the build " +
                          "to complete or for new errors to appear — returns early on either event. " +
                          "With timeout=0, returns an instant snapshot. For efficient polling, pass " +
                          "error_count/warning_count from the previous response as errors_from/warnings_from " +
                          "to receive only new diagnostics.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["build_id"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Build ID to poll. Omit for the most recent build.",
                    },
                    ["timeout"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Max seconds to wait for news (completion or new errors). Default 0 (instant), max 45.",
                        ["default"] = 0,
                        ["maximum"] = MaxTimeoutSeconds,
                    },
                    ["include_output"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Include tail of raw build output",
                        ["default"] = false,
                    },
                    ["output_lines"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Number of output tail lines to include",
                        ["default"] = 50,
                    },
                    ["errors_from"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Skip this many errors — pass error_count from previous response to get only new errors.",
                        ["default"] = 0,
                    },
                    ["warnings_from"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Skip this many warnings — pass warning_count from previous response to get only new warnings.",
                        ["default"] = 0,
                    },
                    ["max_errors"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Max error items to return (default 50). Set higher for full error lists, lower for polling.",
                        ["default"] = 50,
                    },
                },
                ["required"] = new JsonArray(),
            },
            Handler = args =>
            {
                var buildId = args["build_id"]?.GetValue<string>();
                var (timeout, timeoutClamped) = ClampTimeout(args["timeout"]?.GetValue<int>() ?? 0);
                var includeOutput = args["include_output"]?.GetValue<bool>() ?? false;
                var outputLines = args["output_lines"]?.GetValue<int>() ?? 50;
                var errorsFrom = args["errors_from"]?.GetValue<int>() ?? 0;
                var warningsFrom = args["warnings_from"]?.GetValue<int>() ?? 0;
                var maxErrors = args["max_errors"]?.GetValue<int>() ?? 50;

                var status = buildManager.GetStatus(buildId, timeout, includeOutput, outputLines);
                if (status == null)
                    return new JsonObject { ["error"] = "No build found" };
                var json = StatusToJson(status, errorsFrom, warningsFrom, maxErrors);
                if (timeoutClamped)
                    json["timeout_clamped"] = $"Requested timeout exceeded max ({MaxTimeoutSeconds}s). " +
                        "Use get_build_status to continue polling.";
                return json;
            },
        });

        server.RegisterTool(new ToolInfo
        {
            Name = "cancel_build",
            Description = "Cancel the current running build. Returns the partial build status " +
                          "with errors collected so far.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["build_id"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Build ID to cancel. Omit for the current build.",
                    },
                },
                ["required"] = new JsonArray(),
            },
            Handler = args =>
            {
                var buildId = args["build_id"]?.GetValue<string>();
                var status = buildManager.Cancel(buildId);
                if (status == null)
                    return new JsonObject { ["error"] = "No running build to cancel" };
                return StatusToJson(status, 0, 0, 50);
            },
        });

        server.RegisterTool(new ToolInfo
        {
            Name = "parse_build_output",
            Description = "Parse raw MSBuild text output into structured errors/warnings. Use when you " +
                          "already have build output text and need it structured.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["output"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Raw MSBuild output text to parse"
                    },
                },
                ["required"] = new JsonArray("output"),
            },
            Handler = args =>
            {
                var output = args["output"]!.GetValue<string>();
                var diagnostics = ErrorParser.Parse(output);

                var errors = new JsonArray();
                var warnings = new JsonArray();
                foreach (var d in diagnostics)
                {
                    var j = DiagnosticToJson(d);
                    if (d.Severity == DiagnosticSeverity.Error) errors.Add(j);
                    else warnings.Add(j);
                }

                return new JsonObject
                {
                    ["error_count"] = errors.Count,
                    ["warning_count"] = warnings.Count,
                    ["errors"] = errors,
                    ["warnings"] = warnings,
                };
            },
        });

        RegisterOutputTools(server, buildManager);
    }

    private static void RegisterOutputTools(McpServer server, BuildManager buildManager)
    {
        server.RegisterTool(new ToolInfo
        {
            Name = "search_build_output",
            Description = "Regex search over a build's retained output. Returns matches with context " +
                          "lines and pagination. Transparently searches disk when output exceeded memory.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["build_id"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Build ID to search. Omit for the current/most recent build.",
                    },
                    ["pattern"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Regex pattern.",
                    },
                    ["context_lines"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Lines of context around each match (default: 3).",
                        ["default"] = 3,
                    },
                    ["max_results"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Max matches to return (default: 20).",
                        ["default"] = 20,
                    },
                    ["skip"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Skip first N matches for pagination (default: 0).",
                        ["default"] = 0,
                    },
                },
                ["required"] = new JsonArray("pattern"),
            },
            Handler = args =>
            {
                var buildId = args["build_id"]?.GetValue<string>();
                var pattern = args["pattern"]!.GetValue<string>();
                var contextLines = args["context_lines"]?.GetValue<int>() ?? 3;
                var maxResults = args["max_results"]?.GetValue<int>() ?? 20;
                var skip = args["skip"]?.GetValue<int>() ?? 0;

                var output = buildManager.GetBuildOutput(buildId)
                    ?? throw new InvalidOperationException(
                        $"No build output found{(buildId != null ? $" for build '{buildId}'" : "")}. " +
                        "Start a build first.");

                var result = output.Search(pattern, contextLines, maxResults, skip);

                var matchArr = new JsonArray();
                foreach (var m in result.Matches)
                {
                    var ctxArr = new JsonArray();
                    foreach (var c in m.Context) ctxArr.Add(c);
                    matchArr.Add(new JsonObject
                    {
                        ["line"] = m.Line,
                        ["text"] = m.Text,
                        ["context"] = ctxArr,
                    });
                }

                var obj = new JsonObject
                {
                    ["build_id"] = buildManager.GetCurrentBuildId(),
                    ["total_matches"] = result.TotalMatches,
                    ["showing"] = $"{skip + 1}-{skip + result.Matches.Count} of {result.TotalMatches}",
                    ["matches"] = matchArr,
                    ["total_output_lines"] = result.TotalLines,
                    ["retained_lines"] = result.RetainedLines,
                };
                return obj;
            },
        });

        server.RegisterTool(new ToolInfo
        {
            Name = "get_build_output",
            Description = "View a range of lines from a build's output. Supports tail, line range, " +
                          "centering on a line number, or centering on a regex match.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["build_id"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Build ID. Omit for the current/most recent build.",
                    },
                    ["from_line"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Start line (1-indexed). Omit for tail.",
                    },
                    ["to_line"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "End line (inclusive).",
                    },
                    ["max_lines"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Max lines to return (default: 200).",
                        ["default"] = 200,
                        ["maximum"] = 500,
                    },
                    ["pattern"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Center output around first regex match.",
                    },
                    ["around_line"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Center output around this line number.",
                    },
                },
                ["required"] = new JsonArray(),
            },
            Handler = args =>
            {
                var buildId = args["build_id"]?.GetValue<string>();
                var fromLine = args["from_line"]?.GetValue<int>();
                var toLine = args["to_line"]?.GetValue<int>();
                var maxLines = Math.Min(args["max_lines"]?.GetValue<int>() ?? 200, 500);
                var pattern = args["pattern"]?.GetValue<string>();
                var aroundLine = args["around_line"]?.GetValue<int>();

                var output = buildManager.GetBuildOutput(buildId)
                    ?? throw new InvalidOperationException(
                        $"No build output found{(buildId != null ? $" for build '{buildId}'" : "")}. " +
                        "Start a build first.");

                OutputSlice slice;
                int? matchLine = null;

                // Priority: pattern > around_line > from_line > tail
                if (!string.IsNullOrEmpty(pattern))
                {
                    matchLine = output.FindFirstMatch(pattern);
                    slice = matchLine.HasValue
                        ? output.GetAroundLine(matchLine.Value, maxLines)
                        : output.GetTail(maxLines);
                }
                else if (aroundLine.HasValue)
                {
                    slice = output.GetAroundLine(aroundLine.Value, maxLines);
                }
                else if (fromLine.HasValue)
                {
                    var to = toLine ?? (fromLine.Value + maxLines - 1);
                    slice = output.GetLines(fromLine.Value, to, maxLines);
                }
                else
                {
                    slice = output.GetTail(maxLines);
                }

                var linesText = string.Join("\n", slice.Lines.Select(
                    (l, i) => $"{slice.FromLine + i}: {l}"));

                var obj = new JsonObject
                {
                    ["build_id"] = buildManager.GetCurrentBuildId(),
                    ["from_line"] = slice.FromLine,
                    ["to_line"] = slice.ToLine,
                    ["total_lines"] = slice.TotalLines,
                    ["lines"] = linesText,
                };
                if (matchLine.HasValue) obj["match_line"] = matchLine.Value;
                return obj;
            },
        });

        server.RegisterTool(new ToolInfo
        {
            Name = "save_build_output",
            Description = "Write a build's output to a file on the host.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["build_id"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Build ID. Omit for the current/most recent build.",
                    },
                    ["path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Local file path to write to.",
                    },
                },
                ["required"] = new JsonArray("path"),
            },
            Handler = args =>
            {
                var buildId = args["build_id"]?.GetValue<string>();
                var path = args["path"]!.GetValue<string>();

                var output = buildManager.GetBuildOutput(buildId)
                    ?? throw new InvalidOperationException(
                        $"No build output found{(buildId != null ? $" for build '{buildId}'" : "")}. " +
                        "Start a build first.");

                var (linesWritten, bytesWritten) = output.SaveTo(path);

                return new JsonObject
                {
                    ["build_id"] = buildManager.GetCurrentBuildId(),
                    ["path"] = path,
                    ["lines_written"] = linesWritten,
                    ["size_mb"] = Math.Round(bytesWritten / (1024.0 * 1024.0), 1),
                };
            },
        });
    }

    internal static JsonObject StatusToJson(BuildStatus s,
        int errorsFrom = 0, int warningsFrom = 0, int maxErrors = 50)
    {
        var errors = new JsonArray();
        foreach (var e in s.Errors.Skip(errorsFrom).Take(maxErrors))
            errors.Add(DiagnosticToJson(e));

        var warnings = new JsonArray();
        foreach (var w in s.Warnings.Skip(warningsFrom).Take(20))
            warnings.Add(DiagnosticToJson(w));

        var obj = new JsonObject
        {
            ["build_id"] = s.BuildId,
            ["status"] = s.Status,
            ["elapsed_ms"] = s.ElapsedMs,
            ["error_count"] = s.ErrorCount,
            ["warning_count"] = s.WarningCount,
            ["command"] = s.Command,
            ["errors"] = errors,
            ["warnings"] = warnings,
        };

        if (s.ExitCode.HasValue) obj["exit_code"] = s.ExitCode.Value;

        // Progress info (only meaningful while running)
        if (s.ProjectsCompleted > 0 || s.ProjectsStarted > 0)
        {
            var progress = new JsonObject
            {
                ["projects_completed"] = s.ProjectsCompleted,
                ["projects_started"] = s.ProjectsStarted,
            };
            if (s.ProjectsTotal.HasValue)
                progress["projects_total"] = s.ProjectsTotal.Value;
            if (s.CurrentProject != null)
                progress["current_project"] = s.CurrentProject;
            obj["progress"] = progress;
        }

        if (s.OutputTail != null)
        {
            var tail = new JsonArray();
            foreach (var line in s.OutputTail) tail.Add(line);
            obj["output_tail"] = tail;
        }

        if (s.Collision != null)
        {
            obj["collision"] = new JsonObject
            {
                ["message"] = $"A build is already running for different targets. " +
                              $"Running: {s.Collision.RunningTargets ?? "(full solution)"} " +
                              $"Requested: {s.Collision.RequestedTargets ?? "(full solution)"}. " +
                              $"Use cancel_build to stop it, or get_build_status to poll it.",
                ["running_targets"] = s.Collision.RunningTargets,
                ["requested_targets"] = s.Collision.RequestedTargets,
            };
        }

        return obj;
    }

    private static JsonObject DiagnosticToJson(BuildDiagnostic d)
    {
        var obj = new JsonObject
        {
            ["file"] = d.File,
            ["severity"] = d.Severity == DiagnosticSeverity.Error ? "error" : "warning",
            ["code"] = d.Code,
            ["message"] = d.Message,
        };
        if (d.Line.HasValue) obj["line"] = d.Line.Value;
        if (d.Column.HasValue) obj["column"] = d.Column.Value;
        if (d.Project != null) obj["project"] = d.Project;
        if (d.OutputLine.HasValue) obj["output_line"] = d.OutputLine.Value;
        return obj;
    }
}
