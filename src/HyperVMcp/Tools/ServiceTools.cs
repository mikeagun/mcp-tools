// Copyright (c) HyperV MCP contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using HyperVMcp.Engine;
using McpSharp;

namespace HyperVMcp.Tools;

/// <summary>
/// MCP tools for managing services on VMs.
/// </summary>
public static class ServiceTools
{
    public static void Register(McpServer server, SessionManager sessionManager)
    {
        server.RegisterTool(new ToolInfo
        {
            Name = "get_services",
            Description = "Get service status on a VM. Fails if a command is running on the session — wait for it to complete first.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["session_id"] = new JsonObject { ["type"] = "string", ["description"] = "Target VM session." },
                    ["names"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Service names to query (e.g., ['eBPFSvc','NetEbpfExt']).",
                    },
                },
                ["required"] = new JsonArray("session_id", "names"),
            },
            Handler = args =>
            {
                var sessionId = args["session_id"]!.GetValue<string>();
                var names = args["names"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();

                var nameList = string.Join("','", names.Select(PsUtils.PsEscape));
                var cmd = $"Get-Service @('{nameList}') -ErrorAction SilentlyContinue | " +
                    "ForEach-Object { [PSCustomObject]@{ Name=$_.Name; Status=$_.Status.ToString(); StartType=$_.StartType.ToString() } } | " +
                    "ConvertTo-Json -Depth 2 -Compress";

                var (output, errors) = sessionManager.ExecuteOnVmSync(sessionId, cmd);
                var errorText = string.Join("\n", errors.Where(e => !string.IsNullOrWhiteSpace(e)));

                if (!string.IsNullOrEmpty(errorText))
                    return new JsonObject { ["error"] = errorText };

                var jsonText = string.Join("\n", output);

                try
                {
                    var parsed = JsonNode.Parse(jsonText);
                    return new JsonObject { ["services"] = parsed?.DeepClone() };
                }
                catch
                {
                    return new JsonObject { ["output"] = jsonText, ["errors"] = errorText };
                }
            },
        });

        server.RegisterTool(new ToolInfo
        {
            Name = "manage_service",
            Description = "Start, stop, or restart a service on a VM. Fails if a command is running on the session — wait for it to complete first.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["session_id"] = new JsonObject { ["type"] = "string", ["description"] = "Target VM session." },
                    ["name"] = new JsonObject { ["type"] = "string", ["description"] = "Service name." },
                    ["action"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("start", "stop", "restart"),
                        ["description"] = "Action: 'start', 'stop', or 'restart'.",
                    },
                    ["timeout"] = new JsonObject { ["type"] = "integer", ["description"] = $"Max seconds to wait for service state change (default: 30, max {TimeoutHelper.MaxTimeoutSeconds}).", ["maximum"] = TimeoutHelper.MaxTimeoutSeconds },
                },
                ["required"] = new JsonArray("session_id", "name", "action"),
            },
            Handler = args =>
            {
                var sessionId = args["session_id"]!.GetValue<string>();
                var name = args["name"]!.GetValue<string>();
                var action = args["action"]!.GetValue<string>();

                var cmd = action.ToLowerInvariant() switch
                {
                    "start" => $"Start-Service '{PsUtils.PsEscape(name)}' -ErrorAction Stop",
                    "stop" => $"Stop-Service '{PsUtils.PsEscape(name)}' -Force -ErrorAction Stop",
                    "restart" => $"Restart-Service '{PsUtils.PsEscape(name)}' -Force -ErrorAction Stop",
                    _ => throw new ArgumentException($"Invalid action: {action}. Use 'start', 'stop', or 'restart'."),
                };
                cmd += $"; Get-Service '{PsUtils.PsEscape(name)}' | ForEach-Object {{ [PSCustomObject]@{{ Name=$_.Name; Status=$_.Status.ToString() }} }} | ConvertTo-Json -Compress";

                var (output, errors) = sessionManager.ExecuteOnVmSync(sessionId, cmd);

                return new JsonObject
                {
                    ["service"] = name,
                    ["action"] = action,
                    ["output"] = string.Join("\n", output),
                    ["errors"] = errors.Count > 0 ? string.Join("\n", errors) : null,
                };
            },
        });
    }
}
