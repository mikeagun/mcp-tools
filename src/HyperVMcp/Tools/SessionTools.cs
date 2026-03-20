// Copyright (c) HyperV MCP contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using HyperVMcp.Engine;
using McpSharp;

namespace HyperVMcp.Tools;

/// <summary>
/// MCP tools for VM session lifecycle: connect, disconnect, reconnect, list.
/// </summary>
public static class SessionTools
{
    public static void Register(McpServer server, SessionManager sessionManager)
    {
        server.RegisterTool(new ToolInfo
        {
            Name = "connect_vm",
            Description = "Connect to a Hyper-V VM and establish a persistent PowerShell session. " +
                "Use vm_name for local VMs (PSDirect) or computer_name for remote VMs (WinRM). " +
                "Returns a session_id for use in subsequent commands. " +
                "REQUIRES: Administrator privileges on the host. One of vm_name or computer_name must be provided.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["vm_name"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Local Hyper-V VM name (PSDirect mode).",
                    },
                    ["computer_name"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Remote VM IP or hostname (WinRM mode).",
                    },
                    ["credential_target"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Credential Manager target name (default: 'TEST_VM').",
                    },
                    ["username"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Explicit username (overrides credential_target).",
                    },
                    ["password"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Explicit password (overrides credential_target).",
                    },
                    ["session_id"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Custom session ID (auto-generated if omitted).",
                    },
                    ["max_retries"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Max connection retry attempts. Default: 10 for PSDirect (VM boot window), 2 for WinRM.",
                        ["minimum"] = 1,
                        ["maximum"] = 20,
                    },
                },
                ["required"] = new JsonArray("vm_name"),
            },
            Handler = args =>
            {
                var session = sessionManager.Connect(
                    vmName: args["vm_name"]?.GetValue<string>(),
                    computerName: args["computer_name"]?.GetValue<string>(),
                    credentialTarget: args["credential_target"]?.GetValue<string>(),
                    username: args["username"]?.GetValue<string>(),
                    password: args["password"]?.GetValue<string>(),
                    sessionId: args["session_id"]?.GetValue<string>(),
                    maxRetries: args["max_retries"]?.GetValue<int>());

                return new JsonObject
                {
                    ["session_id"] = session.SessionId,
                    ["vm_name"] = session.VmName,
                    ["mode"] = session.Mode.ToString().ToLowerInvariant(),
                    ["status"] = "connected",
                };
            },
        });

        server.RegisterTool(new ToolInfo
        {
            Name = "disconnect_vm",
            Description = "Close a VM session and release resources.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["session_id"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Session ID to disconnect.",
                    },
                },
                ["required"] = new JsonArray("session_id"),
            },
            Handler = args =>
            {
                var sessionId = args["session_id"]!.GetValue<string>();
                sessionManager.Disconnect(sessionId);
                return new JsonObject
                {
                    ["session_id"] = sessionId,
                    ["status"] = "disconnected",
                };
            },
        });

        server.RegisterTool(new ToolInfo
        {
            Name = "reconnect_vm",
            Description = "Reconnect a broken or stale VM session (same session ID, fresh connection).",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["session_id"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Session ID to reconnect.",
                    },
                },
                ["required"] = new JsonArray("session_id"),
            },
            Handler = args =>
            {
                var sessionId = args["session_id"]!.GetValue<string>();
                var session = sessionManager.Reconnect(sessionId);
                return new JsonObject
                {
                    ["session_id"] = session.SessionId,
                    ["vm_name"] = session.VmName,
                    ["mode"] = session.Mode.ToString().ToLowerInvariant(),
                    ["status"] = "connected",
                };
            },
        });

        server.RegisterTool(new ToolInfo
        {
            Name = "list_sessions",
            Description = "List all active VM sessions with their status and connection info.",
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            Handler = _ =>
            {
                var sessions = sessionManager.ListSessions();
                var arr = new JsonArray();
                foreach (var s in sessions)
                {
                    arr.Add(new JsonObject
                    {
                        ["session_id"] = s.SessionId,
                        ["vm_name"] = s.VmName,
                        ["mode"] = s.Mode.ToString().ToLowerInvariant(),
                        ["status"] = s.Status.ToString().ToLowerInvariant(),
                        ["connected_since"] = s.ConnectedSince.ToString("o"),
                        ["command_count"] = s.CommandCount,
                    });
                }
                return new JsonObject { ["sessions"] = arr, ["count"] = sessions.Count };
            },
        });
    }
}
