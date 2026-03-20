// Copyright (c) HyperV MCP contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using HyperVMcp.Engine;
using McpSharp;

namespace HyperVMcp.Tools;

/// <summary>
/// MCP tools for executing commands on VMs: invoke_command, get_command_status,
/// cancel_command, run_script.
/// </summary>
public static class CommandTools
{
    public static void Register(McpServer server, CommandRunner commandRunner)
    {
        server.RegisterTool(new ToolInfo
        {
            Name = "invoke_command",
            Description = "Execute a PowerShell command on a VM via its session. " +
                "Short commands return inline. Long-running commands return a command_id for polling with get_command_status. " +
                "Multiple commands can be separated with `;` in a single call. " +
                "Sessions support only one command at a time — other session tools (get_services, get_vm_info, etc.) will fail while a command is running. " +
                "Output streams line-by-line. Use output_format='text' only for Format-Table/Format-List rendering (buffers all output).",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["session_id"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Target VM session.",
                    },
                    ["command"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "PowerShell command to execute on the VM.",
                    },
                    ["initial_wait"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = $"Max seconds to wait for initial output (default: 30, max {TimeoutHelper.MaxTimeoutSeconds}). Command continues running in background — use get_command_status to poll.",
                        ["maximum"] = TimeoutHelper.MaxTimeoutSeconds,
                    },
                    ["working_directory"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Set working directory before execution.",
                    },
                    ["output_format"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("none", "text", "json"),
                        ["description"] = "Output format: 'none' (default, raw streaming), 'text' (Out-String, buffers all output), 'json'.",
                    },
                    ["timeout"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Hard timeout in seconds. Backend kills the command after this time.",
                    },
                    ["retention"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("full", "tail"),
                        ["description"] = "Output retention: 'full' (default, all lines kept, searchable) or 'tail' (ring buffer only, lightweight).",
                    },
                },
                ["required"] = new JsonArray("session_id", "command"),
            },
            Handler = args =>
            {
                var sessionId = args["session_id"]!.GetValue<string>();
                var command = args["command"]!.GetValue<string>();
                var (timeout, clamped) = TimeoutHelper.ClampTimeout(args["initial_wait"]?.GetValue<int>() ?? 30);
                var workingDir = args["working_directory"]?.GetValue<string>();
                var outputFormat = args["output_format"]?.GetValue<string>() ?? "none";
                var hardTimeout = args["timeout"]?.GetValue<int>();
                var outputMode = args["retention"]?.GetValue<string>() ?? "full";

                var job = commandRunner.ExecuteWithTimeout(sessionId, command, timeout, workingDir,
                    outputFormat, hardTimeout, outputMode);
                var json = SnapshotToJson(job.GetSnapshot());
                if (clamped) json["timeout_clamped"] = $"Timeout capped at {TimeoutHelper.MaxTimeoutSeconds}s. Use get_command_status to continue polling.";
                return json;
            },
        });

        server.RegisterTool(new ToolInfo
        {
            Name = "get_command_status",
            Description = "Poll a running or recently completed command. " +
                "Set timeout > 0 to wait for new output or completion. " +
                "Use since_line to get only new output since last poll (returns last tail_lines of new output).",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["command_id"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Command ID to poll.",
                    },
                    ["timeout"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = $"Seconds to wait for new output or completion (default: 0 = instant, max {TimeoutHelper.MaxTimeoutSeconds}). Returns early if command completes or new output arrives.",
                        ["maximum"] = TimeoutHelper.MaxTimeoutSeconds,
                    },
                    ["tail_lines"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Max output lines to return (default: 100).",
                    },
                    ["since_line"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Only return output after this line number (from a previous poll's total_output_lines). Returns last tail_lines of new output.",
                    },
                    ["include_output"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Include output text (default: true). Set false for cheap status-only poll.",
                    },
                },
                ["required"] = new JsonArray("command_id"),
            },
            Handler = args =>
            {
                var commandId = args["command_id"]!.GetValue<string>();
                var (timeout, _) = TimeoutHelper.ClampTimeout(args["timeout"]?.GetValue<int>() ?? 0);
                var tailLines = args["tail_lines"]?.GetValue<int>() ?? 100;
                var sinceLine = args["since_line"]?.GetValue<int>();
                var includeOutput = args["include_output"]?.GetValue<bool>() ?? true;

                var job = commandRunner.GetCommand(commandId)
                    ?? throw new InvalidOperationException($"Command '{commandId}' not found. It may have expired — use invoke_command to start a new one.");

                if (timeout > 0 && job.Status == CommandStatus.Running)
                    job.WaitForNews(timeout * 1000);

                return SnapshotToJson(job.GetSnapshot(tailLines, includeOutput, sinceLine));
            },
        });

        server.RegisterTool(new ToolInfo
        {
            Name = "cancel_command",
            Description = "Cancel a running command.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["command_id"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Command ID to cancel.",
                    },
                },
                ["required"] = new JsonArray("command_id"),
            },
            Handler = args =>
            {
                var commandId = args["command_id"]!.GetValue<string>();
                var snapshot = commandRunner.CancelCommand(commandId);
                return SnapshotToJson(snapshot);
            },
        });

        server.RegisterTool(new ToolInfo
        {
            Name = "run_script",
            Description = "Run a PowerShell script file on a VM.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["session_id"] = new JsonObject { ["type"] = "string", ["description"] = "Target VM session." },
                    ["script_path"] = new JsonObject { ["type"] = "string", ["description"] = "Script path on the VM." },
                    ["args"] = new JsonObject { ["type"] = "string", ["description"] = "Script arguments." },
                    ["initial_wait"] = new JsonObject { ["type"] = "integer", ["description"] = $"Max seconds to wait for initial output (default: 30, max {TimeoutHelper.MaxTimeoutSeconds}). Script continues running — use get_command_status to poll.", ["maximum"] = TimeoutHelper.MaxTimeoutSeconds },
                    ["timeout"] = new JsonObject { ["type"] = "integer", ["description"] = "Hard timeout in seconds. Backend kills the command after this time." },
                    ["working_directory"] = new JsonObject { ["type"] = "string", ["description"] = "Working directory on the VM." },
                    ["output_format"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("none", "text", "json"),
                        ["description"] = "Output format: 'none' (default, raw streaming), 'text' (Out-String, buffers), 'json'.",
                    },
                },
                ["required"] = new JsonArray("session_id", "script_path"),
            },
            Handler = args =>
            {
                var sessionId = args["session_id"]!.GetValue<string>();
                var scriptPath = args["script_path"]!.GetValue<string>();
                var scriptArgs = args["args"]?.GetValue<string>() ?? "";
                var (timeout, clamped) = TimeoutHelper.ClampTimeout(args["initial_wait"]?.GetValue<int>() ?? 30);
                var hardTimeout = args["timeout"]?.GetValue<int>();
                var workingDir = args["working_directory"]?.GetValue<string>();
                var outputFormat = args["output_format"]?.GetValue<string>() ?? "none";

                var command = $"& '{PsUtils.PsEscape(scriptPath)}'";
                if (!string.IsNullOrEmpty(scriptArgs))
                    command += $" {scriptArgs}";

                var job = commandRunner.ExecuteWithTimeout(sessionId, command, timeout, workingDir, outputFormat, hardTimeout);
                var json = SnapshotToJson(job.GetSnapshot());
                if (clamped) json["timeout_clamped"] = $"Timeout capped at {TimeoutHelper.MaxTimeoutSeconds}s. Use get_command_status to continue polling.";
                return json;
            },
        });
    }

    internal static JsonObject SnapshotToJson(CommandSnapshot snapshot)
    {
        var isTerminal = snapshot.Status != CommandStatus.Running;
        var isCompact = isTerminal && snapshot.TotalOutputLines <= 100
            && snapshot.OutputMode == "full" && snapshot.SavedTo == null;
        // Also compact when running but no output yet (polling metadata is all zeros).
        var hasOutput = snapshot.TotalOutputLines > 0;

        var json = new JsonObject
        {
            ["command_id"] = snapshot.CommandId,
            ["session_id"] = snapshot.SessionId,
            ["status"] = snapshot.Status.ToString().ToLowerInvariant(),
        };

        if (snapshot.ExitCode.HasValue)
            json["exit_code"] = snapshot.ExitCode.Value;

        if (snapshot.Output != null && !string.IsNullOrEmpty(snapshot.Output))
            json["output"] = snapshot.Output;

        if (!string.IsNullOrEmpty(snapshot.Errors))
            json["errors"] = snapshot.Errors;

        json["elapsed_seconds"] = Math.Round(snapshot.ElapsedSeconds, 1);
        json["total_output_lines"] = snapshot.TotalOutputLines;

        // Skip output-management metadata when it adds no value:
        // - completed commands with small output (compact inline)
        // - running commands with no output yet (all zeros)
        if (!isCompact && hasOutput)
        {
            json["retained_lines"] = snapshot.RetainedOutputLines;
            json["first_available_line"] = snapshot.FirstAvailableLine;
            json["retention"] = snapshot.OutputMode;

            if (snapshot.SavedTo != null)
                json["saved_to"] = snapshot.SavedTo;
        }

        // Line range for delta polling (only when there's actual output to show).
        if (snapshot.FromLine > 0 && snapshot.ToLine >= snapshot.FromLine)
        {
            json["from_line"] = snapshot.FromLine;
            json["to_line"] = snapshot.ToLine;
        }
        if (snapshot.SkippedLines > 0)
            json["skipped_lines"] = snapshot.SkippedLines;

        if (snapshot.Status == CommandStatus.Running)
            json["hint"] = $"Command running — session locked. Poll with get_command_status(command_id='{snapshot.CommandId}'), cancel with cancel_command(command_id='{snapshot.CommandId}').";

        // Detect raw .NET object output (e.g. "Microsoft.PowerShell.Commands.GenericMeasureInfo")
        // and suggest output_format='text' for human-readable rendering.
        if (isTerminal && snapshot.Output != null && LooksLikeDotNetOutput(snapshot.Output))
        {
            json["hint"] = "Output looks like raw .NET type names. Use output_format='text' for formatted output.";
        }

        return json;
    }

    /// <summary>
    /// Heuristic: does the output look like bare .NET type names?
    /// Checks each line individually to handle multi-line Format-* output
    /// (e.g., FormatStartData, FormatEntryData, FormatEndData — one per line).
    /// </summary>
    private static bool LooksLikeDotNetOutput(string output)
    {
        var lines = output.Split('\n');
        // For single-line output, check the whole thing.
        if (lines.Length <= 3)
            return LooksLikeDotNetTypeName(output.Trim());

        // For multi-line output, check if most lines are .NET type names.
        var typeCount = lines.Count(l => LooksLikeDotNetTypeName(l.Trim()));
        return typeCount >= 2 && typeCount >= lines.Length / 2;
    }

    private static bool LooksLikeDotNetTypeName(string line)
    {
        // Must have at least two dots (namespace.class) and no spaces.
        return line.Length > 10
            && line.IndexOf('.') > 0
            && line.IndexOf('.') != line.LastIndexOf('.')
            && !line.Contains(' ');
    }

    internal static void RegisterOutputTools(McpServer server, CommandRunner commandRunner)
    {
        server.RegisterTool(new ToolInfo
        {
            Name = "search_command_output",
            Description = "Regex search over a command's retained output. Returns matches with context lines and pagination.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["command_id"] = new JsonObject { ["type"] = "string", ["description"] = "Command whose output to search." },
                    ["pattern"] = new JsonObject { ["type"] = "string", ["description"] = "Regex pattern." },
                    ["context_lines"] = new JsonObject { ["type"] = "integer", ["description"] = "Lines of context around each match (default: 3)." },
                    ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "Max matches to return (default: 20)." },
                    ["skip"] = new JsonObject { ["type"] = "integer", ["description"] = "Skip first N matches for pagination (default: 0)." },
                },
                ["required"] = new JsonArray("command_id", "pattern"),
            },
            Handler = args =>
            {
                var commandId = args["command_id"]!.GetValue<string>();
                var pattern = args["pattern"]!.GetValue<string>();
                var contextLines = args["context_lines"]?.GetValue<int>() ?? 3;
                var maxResults = args["max_results"]?.GetValue<int>() ?? 20;
                var skip = args["skip"]?.GetValue<int>() ?? 0;

                var job = commandRunner.GetCommand(commandId)
                    ?? throw new InvalidOperationException($"Command '{commandId}' not found. It may have expired — use invoke_command to start a new one.");

                var result = job.Output.Search(pattern, contextLines, maxResults, skip);

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

                var json = new JsonObject
                {
                    ["command_id"] = commandId,
                    ["total_matches"] = result.TotalMatches,
                    ["showing"] = $"{skip + 1}-{skip + result.Matches.Count} of {result.TotalMatches}",
                    ["matches"] = matchArr,
                    ["total_output_lines"] = result.TotalLines,
                    ["retained_lines"] = result.RetainedLines,
                    ["first_available_line"] = result.FirstAvailableLine,
                };

                if (result.Freed)
                    json["warning"] = "Output has been freed. Re-run the command to capture new output.";
                else if (result.FirstAvailableLine > 1)
                    json["warning"] = $"Output was truncated. Only lines {result.FirstAvailableLine}-{result.FirstAvailableLine + result.RetainedLines - 1} are searchable. Use retention='full' to capture complete output.";

                return json;
            },
        });

        server.RegisterTool(new ToolInfo
        {
            Name = "get_command_output",
            Description = "View a range of lines from a command's output. Supports tail, line range, centering on a line number, or centering on a regex match.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["command_id"] = new JsonObject { ["type"] = "string", ["description"] = "Command whose output to view." },
                    ["from_line"] = new JsonObject { ["type"] = "integer", ["description"] = "Start line (1-indexed). Omit for tail." },
                    ["to_line"] = new JsonObject { ["type"] = "integer", ["description"] = "End line (inclusive)." },
                    ["max_lines"] = new JsonObject { ["type"] = "integer", ["description"] = "Max lines to return (default: 200)." },
                    ["pattern"] = new JsonObject { ["type"] = "string", ["description"] = "Center output around first regex match." },
                    ["around_line"] = new JsonObject { ["type"] = "integer", ["description"] = "Center output around this line number." },
                },
                ["required"] = new JsonArray("command_id"),
            },
            Handler = args =>
            {
                var commandId = args["command_id"]!.GetValue<string>();
                var fromLine = args["from_line"]?.GetValue<int>();
                var toLine = args["to_line"]?.GetValue<int>();
                var maxLines = args["max_lines"]?.GetValue<int>() ?? 200;
                var pattern = args["pattern"]?.GetValue<string>();
                var aroundLine = args["around_line"]?.GetValue<int>();

                var job = commandRunner.GetCommand(commandId)
                    ?? throw new InvalidOperationException($"Command '{commandId}' not found. It may have expired — use invoke_command to start a new one.");

                OutputSlice slice;
                int? matchLine = null;

                if (!string.IsNullOrEmpty(pattern))
                {
                    matchLine = job.Output.FindFirstMatch(pattern);
                    slice = matchLine.HasValue
                        ? job.Output.GetAroundLine(matchLine.Value, maxLines)
                        : job.Output.GetTail(maxLines); // pattern not found, fall back to tail
                }
                else if (aroundLine.HasValue)
                {
                    slice = job.Output.GetAroundLine(aroundLine.Value, maxLines);
                }
                else if (fromLine.HasValue)
                {
                    var to = toLine ?? (fromLine.Value + maxLines - 1);
                    slice = job.Output.GetLines(fromLine.Value, to, maxLines);
                }
                else
                {
                    slice = job.Output.GetTail(maxLines);
                }

                var json = new JsonObject
                {
                    ["command_id"] = commandId,
                    ["from_line"] = slice.FromLine,
                    ["to_line"] = slice.ToLine,
                    ["total_lines"] = slice.TotalLines,
                    ["first_available_line"] = slice.FirstAvailableLine,
                    ["lines"] = string.Join("\n", slice.Lines),
                };

                if (matchLine.HasValue)
                    json["match_line"] = matchLine.Value;
                if (slice.Freed)
                    json["error"] = "Output has been freed. Re-run the command to capture new output.";

                return json;
            },
        });

        server.RegisterTool(new ToolInfo
        {
            Name = "save_command_output",
            Description = "Write a command's retained output to a file on the host.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["command_id"] = new JsonObject { ["type"] = "string", ["description"] = "Command whose output to save." },
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Local file path to write to." },
                },
                ["required"] = new JsonArray("command_id", "path"),
            },
            Handler = args =>
            {
                var commandId = args["command_id"]!.GetValue<string>();
                var path = args["path"]!.GetValue<string>();

                var job = commandRunner.GetCommand(commandId)
                    ?? throw new InvalidOperationException($"Command '{commandId}' not found. It may have expired — use invoke_command to start a new one.");

                var (linesWritten, bytesWritten) = job.Output.SaveTo(path);

                return new JsonObject
                {
                    ["path"] = path,
                    ["lines_written"] = linesWritten,
                    ["size_mb"] = Math.Round(bytesWritten / (1024.0 * 1024.0), 1),
                    ["total_output_lines"] = job.Output.TotalLinesReceived,
                };
            },
        });

        server.RegisterTool(new ToolInfo
        {
            Name = "free_command_output",
            Description = "Release a command's output buffer to free memory. After freeing, search/view tools will report the output as unavailable.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["command_id"] = new JsonObject { ["type"] = "string", ["description"] = "Command whose output to free." },
                },
                ["required"] = new JsonArray("command_id"),
            },
            Handler = args =>
            {
                var commandId = args["command_id"]!.GetValue<string>();
                var freedLines = commandRunner.FreeCommandOutput(commandId);
                return new JsonObject
                {
                    ["command_id"] = commandId,
                    ["freed_lines"] = freedLines,
                };
            },
        });
    }
}
