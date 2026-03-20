// Copyright (c) HyperV MCP contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using HyperVMcp.Engine;
using McpSharp;

namespace HyperVMcp.Tools;

/// <summary>
/// MCP tools for file operations: copy_to_vm, copy_from_vm, list_vm_files.
/// File transfers are async — they return a command_id for polling with get_command_status,
/// just like invoke_command. Agents can cancel transfers with cancel_command.
/// </summary>
public static class FileTools
{
    public static void Register(McpServer server, FileTransferManager fileTransferManager)
    {
        server.RegisterTool(new ToolInfo
        {
            Name = "copy_to_vm",
            Description = "Copy files from the local host to a VM. Returns a command_id for polling. " +
                "Supports single files, directories, and glob patterns. " +
                "Large transfers are automatically compressed. " +
                "Transfers run in background independently — poll with get_command_status, cancel with cancel_command.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["session_id"] = new JsonObject { ["type"] = "string", ["description"] = "Target VM session." },
                    ["source"] = new JsonObject
                    {
                        ["oneOf"] = new JsonArray(
                            new JsonObject { ["type"] = "string" },
                            new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }
                        ),
                        ["description"] = "Local path(s) or glob patterns.",
                    },
                    ["destination"] = new JsonObject { ["type"] = "string", ["description"] = "VM destination path." },
                    ["compress"] = new JsonObject { ["type"] = "boolean", ["description"] = "Force compression (default: auto based on size)." },
                    ["initial_wait"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = $"Max seconds to wait for initial progress (default: 30, max {TimeoutHelper.MaxTimeoutSeconds}). Transfer continues in background — use get_command_status to poll.",
                        ["maximum"] = TimeoutHelper.MaxTimeoutSeconds,
                    },
                },
                ["required"] = new JsonArray("session_id", "source", "destination"),
            },
            Handler = args =>
            {
                var sessionId = args["session_id"]!.GetValue<string>();
                var sources = ParseStringOrArray(args["source"]!);
                var destination = args["destination"]!.GetValue<string>();
                var compress = args["compress"]?.GetValue<bool>();
                var (timeout, clamped) = TimeoutHelper.ClampTimeout(args["initial_wait"]?.GetValue<int>() ?? 30);

                var job = fileTransferManager.CopyToVm(sessionId, sources, destination, compress, timeout);
                var json = CommandTools.SnapshotToJson(job.GetSnapshot());
                if (clamped) json["timeout_clamped"] = $"Timeout capped at {TimeoutHelper.MaxTimeoutSeconds}s. Use get_command_status to continue polling.";
                return json;
            },
        });

        server.RegisterTool(new ToolInfo
        {
            Name = "copy_from_vm",
            Description = "Copy files from a VM to the local host. Returns a command_id for polling. " +
                "Poll with get_command_status, cancel with cancel_command.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["session_id"] = new JsonObject { ["type"] = "string", ["description"] = "Source VM session." },
                    ["source"] = new JsonObject
                    {
                        ["oneOf"] = new JsonArray(
                            new JsonObject { ["type"] = "string" },
                            new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }
                        ),
                        ["description"] = "VM path(s) to copy.",
                    },
                    ["destination"] = new JsonObject { ["type"] = "string", ["description"] = "Local destination directory." },
                    ["initial_wait"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = $"Max seconds to wait for initial progress (default: 30, max {TimeoutHelper.MaxTimeoutSeconds}). Transfer continues in background — use get_command_status to poll.",
                        ["maximum"] = TimeoutHelper.MaxTimeoutSeconds,
                    },
                },
                ["required"] = new JsonArray("session_id", "source", "destination"),
            },
            Handler = args =>
            {
                var sessionId = args["session_id"]!.GetValue<string>();
                var sources = ParseStringOrArray(args["source"]!);
                var destination = args["destination"]!.GetValue<string>();
                var (timeout, clamped) = TimeoutHelper.ClampTimeout(args["initial_wait"]?.GetValue<int>() ?? 30);

                var job = fileTransferManager.CopyFromVm(sessionId, sources, destination, timeout);
                var json = CommandTools.SnapshotToJson(job.GetSnapshot());
                if (clamped) json["timeout_clamped"] = $"Timeout capped at {TimeoutHelper.MaxTimeoutSeconds}s. Use get_command_status to continue polling.";
                return json;
            },
        });

        server.RegisterTool(new ToolInfo
        {
            Name = "list_vm_files",
            Description = "List files and directories on a VM. Returns compact entries with summary stats (file/dir counts, total size). " +
                "Results are capped at max_results (default: 200) — use pattern or depth to narrow large directories. " +
                "Fails if a command is running on the session — wait for it to complete first.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["session_id"] = new JsonObject { ["type"] = "string", ["description"] = "Target VM session." },
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Directory path on VM." },
                    ["pattern"] = new JsonObject { ["type"] = "string", ["description"] = "Wildcard filter (e.g., '*.msi', '*.dll')." },
                    ["recurse"] = new JsonObject { ["type"] = "boolean", ["description"] = "Recurse into subdirectories (default: false)." },
                    ["depth"] = new JsonObject { ["type"] = "integer", ["description"] = "Max recursion depth (0 = immediate children only, 1 = one level of subdirs). Implies recurse." },
                    ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "Max entries to return (default: 200). Summary always reflects total counts." },
                },
                ["required"] = new JsonArray("session_id", "path"),
            },
            Handler = args =>
            {
                var sessionId = args["session_id"]!.GetValue<string>();
                var path = args["path"]!.GetValue<string>();
                var pattern = args["pattern"]?.GetValue<string>();
                var recurse = args["recurse"]?.GetValue<bool>() ?? false;
                var depth = args["depth"]?.GetValue<int>();
                var maxResults = args["max_results"]?.GetValue<int>() ?? 200;

                var result = fileTransferManager.ListVmFiles(sessionId, path, pattern, recurse, depth, maxResults);

                var arr = new JsonArray();
                foreach (var f in result.Entries)
                {
                    var entry = new JsonObject { ["name"] = f.Name };
                    if (f.Dir)
                        entry["dir"] = true;
                    else
                        entry["size"] = f.Size;
                    arr.Add(entry);
                }

                var json = new JsonObject
                {
                    ["path"] = result.Path,
                    ["files"] = arr,
                    ["count"] = result.Entries.Count,
                    ["summary"] = new JsonObject
                    {
                        ["files"] = result.TotalFiles,
                        ["directories"] = result.TotalDirectories,
                        ["total_size_mb"] = Math.Round(result.TotalSizeBytes / (1024.0 * 1024.0), 1),
                    },
                };

                if (result.Truncated)
                {
                    json["truncated"] = true;
                    json["total_count"] = result.TotalCount;
                    json["hint"] = $"Results capped at {maxResults}. Use pattern or depth=0 to narrow.";
                }

                return json;
            },
        });
    }

    private static IReadOnlyList<string> ParseStringOrArray(JsonNode node)
    {
        if (node is JsonArray arr)
            return arr.Select(n => n!.GetValue<string>()).ToList();
        return [node.GetValue<string>()];
    }
}
