// Copyright (c) HyperV MCP contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using HyperVMcp.Engine;
using McpSharp;

namespace HyperVMcp.Tools;

/// <summary>
/// MCP tools for managing environment variables on VM sessions.
/// </summary>
public static class EnvTools
{
    public static void Register(McpServer server, SessionManager sessionManager)
    {
        server.RegisterTool(new ToolInfo
        {
            Name = "set_env",
            Description = "Set environment variables for a VM session. " +
                "These are injected into all subsequent commands on the session. " +
                "WARNING: Setting PATH replaces the entire value. To extend PATH, use invoke_command with $env:PATH += ';C:\\new\\path'.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["session_id"] = new JsonObject { ["type"] = "string", ["description"] = "Target VM session." },
                    ["variables"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["description"] = "Environment variables as name→value pairs.",
                    },
                },
                ["required"] = new JsonArray("session_id", "variables"),
            },
            Handler = args =>
            {
                var sessionId = args["session_id"]!.GetValue<string>();
                var variables = args["variables"]!.AsObject();
                var session = sessionManager.GetSession(sessionId);

                foreach (var (key, value) in variables)
                {
                    if (string.IsNullOrWhiteSpace(key))
                        throw new ArgumentException("Environment variable name cannot be empty.");
                    if (key.Any(c => char.IsControl(c) || c == '=' || c == ';'))
                        throw new ArgumentException($"Environment variable name '{key}' contains invalid characters.");
                    session.EnvironmentVariables[key] = value?.GetValue<string>() ?? "";
                }

                return new JsonObject
                {
                    ["session_id"] = sessionId,
                    ["variables_set"] = variables.Count,
                    ["total_env_vars"] = session.EnvironmentVariables.Count,
                };
            },
        });
    }
}
