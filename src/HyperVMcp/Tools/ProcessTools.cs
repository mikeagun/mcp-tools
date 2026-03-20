// Copyright (c) HyperV MCP contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using HyperVMcp.Engine;
using McpSharp;

namespace HyperVMcp.Tools;

/// <summary>
/// MCP tools for process management on VMs.
/// </summary>
public static class ProcessTools
{
    public static void Register(McpServer server, SessionManager sessionManager)
    {
        server.RegisterTool(new ToolInfo
        {
            Name = "kill_process",
            Description = "Kill a process on a VM by PID or name. One of pid or name must be provided. Fails if a command is running on the session — wait for it to complete first.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["session_id"] = new JsonObject { ["type"] = "string", ["description"] = "Target VM session." },
                    ["pid"] = new JsonObject { ["type"] = "integer", ["description"] = "Process ID to kill." },
                    ["name"] = new JsonObject { ["type"] = "string", ["description"] = "Process name to kill." },
                },
                ["required"] = new JsonArray("session_id"),
            },
            Handler = args =>
            {
                var sessionId = args["session_id"]!.GetValue<string>();
                var pid = args["pid"]?.GetValue<int>();
                var name = args["name"]?.GetValue<string>();

                if (pid == null && string.IsNullOrEmpty(name))
                    throw new ArgumentException("Either 'pid' or 'name' is required.");

                var cmd = pid.HasValue
                    ? $"Stop-Process -Id {pid.Value} -Force"
                    : $"Stop-Process -Name '{PsUtils.PsEscape(name!)}' -Force";

                var (output, errors) = sessionManager.ExecuteOnVmSync(sessionId, cmd);
                var errorText = string.Join("\n", errors.Where(e => !string.IsNullOrWhiteSpace(e)));

                return new JsonObject
                {
                    ["output"] = string.Join("\n", output),
                    ["errors"] = !string.IsNullOrEmpty(errorText) ? errorText : null,
                    ["status"] = !string.IsNullOrEmpty(errorText) ? "error" : (output.Any(o => !string.IsNullOrWhiteSpace(o)) ? "ok" : "no_output"),
                };
            },
        });
    }
}
