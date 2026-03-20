// Copyright (c) HyperV MCP contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using HyperVMcp.Engine;
using McpSharp;

namespace HyperVMcp.Tools;

/// <summary>
/// MCP tools for Hyper-V VM lifecycle: list, start, stop, restart, checkpoint, restore.
/// All tools accept vm_name as a string or array for bulk parallel operations.
/// </summary>
public static class VmTools
{
    public static void Register(McpServer server, VmManager vmManager, SessionManager sessionManager)
    {
        server.RegisterTool(new ToolInfo
        {
            Name = "list_vms",
            Description = "List Hyper-V VMs on the local host with state, CPU, memory, and checkpoint info. " +
                "REQUIRES: Administrator privileges.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["name_filter"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Filter VMs by name substring.",
                    },
                },
            },
            Handler = args =>
            {
                var vms = vmManager.ListVms(args["name_filter"]?.GetValue<string>());
                var arr = new JsonArray();
                foreach (var vm in vms)
                {
                    arr.Add(new JsonObject
                    {
                        ["name"] = vm.Name,
                        ["state"] = vm.State,
                        ["cpu_count"] = vm.CpuCount,
                        ["memory_mb"] = vm.MemoryMb,
                        ["uptime"] = vm.Uptime?.ToString(),
                        ["checkpoint_count"] = vm.Checkpoints.Count,
                    });
                }
                return new JsonObject { ["vms"] = arr, ["count"] = vms.Count };
            },
        });

        server.RegisterTool(new ToolInfo
        {
            Name = "start_vm",
            Description = "Start one or more Hyper-V VMs. Accepts a single vm_name or an array for parallel startup. " +
                "Waits for the VM to be ready (heartbeat + Guest Services + PowerShell Direct). " +
                "If VM isn't ready within timeout, use get_vm_info to check status.",
            InputSchema = VmOpSchema("start"),
            Handler = args =>
            {
                var names = ParseVmNames(args);
                var wait = args["wait_for_ready"]?.GetValue<bool>() ?? true;
                var (timeout, clamped) = TimeoutHelper.ClampTimeout(args["timeout"]?.GetValue<int>() ?? 45);
                var results = vmManager.StartVmsAsync(names, wait, timeout).GetAwaiter().GetResult();
                var json = ResultsToJson(results);
                if (results.Any(r => r.Success))
                {
                    var vmName = results.First(r => r.Success).VmName;
                    json["hint"] = $"Use connect_vm(vm_name='{vmName}') to establish a session.";
                }
                if (clamped) json["timeout_clamped"] = $"Timeout capped at {TimeoutHelper.MaxTimeoutSeconds}s.";
                return json;
            },
        });

        server.RegisterTool(new ToolInfo
        {
            Name = "stop_vm",
            Description = "Stop one or more Hyper-V VMs. Accepts a single vm_name or an array for parallel shutdown.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["vm_name"] = VmNameSchema(),
                    ["force"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Force turn off (default: true).",
                    },
                },
                ["required"] = new JsonArray("vm_name"),
            },
            Handler = args =>
            {
                var names = ParseVmNames(args);
                var force = args["force"]?.GetValue<bool>() ?? true;
                var results = vmManager.StopVmsAsync(names, force).GetAwaiter().GetResult();
                return ResultsToJson(results);
            },
        });

        server.RegisterTool(new ToolInfo
        {
            Name = "restart_vm",
            Description = "Restart one or more Hyper-V VMs. Optionally reconnects an existing session after restart. " +
                "If VM isn't ready within timeout, use get_vm_info to check status.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["vm_name"] = VmNameSchema(),
                    ["wait_for_ready"] = new JsonObject { ["type"] = "boolean", ["description"] = "Wait for heartbeat + PS remoting (default: true)." },
                    ["timeout"] = new JsonObject { ["type"] = "integer", ["description"] = $"Max seconds to wait for VM ready (default: 45, max {TimeoutHelper.MaxTimeoutSeconds}).", ["maximum"] = TimeoutHelper.MaxTimeoutSeconds },
                    ["session_id"] = new JsonObject { ["type"] = "string", ["description"] = "Session to reconnect after restart." },
                },
                ["required"] = new JsonArray("vm_name"),
            },
            Handler = args =>
            {
                var names = ParseVmNames(args);
                var wait = args["wait_for_ready"]?.GetValue<bool>() ?? true;
                var (timeout, clamped) = TimeoutHelper.ClampTimeout(args["timeout"]?.GetValue<int>() ?? 45);
                var results = vmManager.RestartVmsAsync(names, wait, timeout).GetAwaiter().GetResult();

                // Reconnect session if requested.
                var sessionId = args["session_id"]?.GetValue<string>();
                if (sessionId != null && results.Any(r => r.Success))
                {
                    try
                    {
                        sessionManager.Reconnect(sessionId);
                    }
                    catch (Exception ex)
                    {
                        var json = ResultsToJson(results);
                        json!["session_reconnect_error"] = ex.Message;
                        var vmName = results.First(r => r.Success).VmName;
                        json["hint"] = $"Use connect_vm(vm_name='{vmName}') to create a new session.";
                        return json;
                    }
                }

                var result = ResultsToJson(results);
                if (sessionId != null)
                    result!["session_reconnected"] = sessionId;
                if (clamped) result!["timeout_clamped"] = $"Timeout capped at {TimeoutHelper.MaxTimeoutSeconds}s.";
                return result;
            },
        });

        server.RegisterTool(new ToolInfo
        {
            Name = "checkpoint_vm",
            Description = "Create a named checkpoint (snapshot) on one or more VMs.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["vm_name"] = VmNameSchema(),
                    ["checkpoint_name"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Name for the checkpoint.",
                    },
                },
                ["required"] = new JsonArray("vm_name", "checkpoint_name"),
            },
            Handler = args =>
            {
                var names = ParseVmNames(args);
                var checkpointName = args["checkpoint_name"]!.GetValue<string>();
                var results = vmManager.CheckpointVmsAsync(names, checkpointName).GetAwaiter().GetResult();
                return ResultsToJson(results);
            },
        });

        server.RegisterTool(new ToolInfo
        {
            Name = "restore_vm",
            Description = "Restore one or more VMs to a named checkpoint and optionally wait for readiness. " +
                "Optionally reconnects an existing session after restore. " +
                "If VM isn't ready within timeout, use get_vm_info to check status.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["vm_name"] = VmNameSchema(),
                    ["checkpoint_name"] = new JsonObject { ["type"] = "string", ["description"] = "Checkpoint name (default: 'baseline')." },
                    ["wait_for_ready"] = new JsonObject { ["type"] = "boolean", ["description"] = "Wait for heartbeat + PS remoting (default: true)." },
                    ["timeout"] = new JsonObject { ["type"] = "integer", ["description"] = $"Max seconds to wait for VM ready (default: 45, max {TimeoutHelper.MaxTimeoutSeconds}).", ["maximum"] = TimeoutHelper.MaxTimeoutSeconds },
                    ["session_id"] = new JsonObject { ["type"] = "string", ["description"] = "Session to reconnect after restore." },
                },
                ["required"] = new JsonArray("vm_name"),
            },
            Handler = args =>
            {
                var names = ParseVmNames(args);
                var checkpointName = args["checkpoint_name"]?.GetValue<string>() ?? "baseline";
                var wait = args["wait_for_ready"]?.GetValue<bool>() ?? true;
                var (timeout, clamped) = TimeoutHelper.ClampTimeout(args["timeout"]?.GetValue<int>() ?? 45);
                var results = vmManager.RestoreVmsAsync(names, checkpointName, wait, timeout).GetAwaiter().GetResult();

                // Reconnect session if requested.
                var sessionId = args["session_id"]?.GetValue<string>();
                if (sessionId != null && results.Any(r => r.Success))
                {
                    try
                    {
                        sessionManager.Reconnect(sessionId);
                    }
                    catch (Exception ex)
                    {
                        var json = ResultsToJson(results);
                        json!["session_reconnect_error"] = ex.Message;
                        var vmName = results.First(r => r.Success).VmName;
                        json["hint"] = $"Use connect_vm(vm_name='{vmName}') to create a new session.";
                        return json;
                    }
                }

                var result = ResultsToJson(results);
                if (sessionId != null)
                    result!["session_reconnected"] = sessionId;
                if (clamped) result!["timeout_clamped"] = $"Timeout capped at {TimeoutHelper.MaxTimeoutSeconds}s.";
                return result;
            },
        });
    }

    private static IReadOnlyList<string> ParseVmNames(JsonObject args)
    {
        var vmName = args["vm_name"];
        if (vmName is JsonArray arr)
            return arr.Select(n => n!.GetValue<string>()).ToList();
        return [vmName!.GetValue<string>()];
    }

    private static JsonObject VmNameSchema() => new()
    {
        ["oneOf"] = new JsonArray(
            new JsonObject { ["type"] = "string", ["description"] = "Single VM name." },
            new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "Multiple VM names for parallel operation.",
            }
        ),
    };

    private static JsonObject VmOpSchema(string operation) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["vm_name"] = VmNameSchema(),
            ["wait_for_ready"] = new JsonObject { ["type"] = "boolean", ["description"] = "Wait for heartbeat + PS remoting (default: true)." },
            ["timeout"] = new JsonObject { ["type"] = "integer", ["description"] = $"Max seconds to wait for VM ready (default: 45, max {TimeoutHelper.MaxTimeoutSeconds}). If VM isn't ready, use get_vm_info to check status.", ["maximum"] = TimeoutHelper.MaxTimeoutSeconds },
        },
        ["required"] = new JsonArray("vm_name"),
    };

    private static JsonObject ResultsToJson(List<VmOperationResult> results)
    {
        if (results.Count == 1)
        {
            var r = results[0];
            var json = new JsonObject
            {
                ["vm_name"] = r.VmName,
                ["success"] = r.Success,
                ["state"] = r.State,
                ["elapsed_seconds"] = Math.Round(r.ElapsedSeconds, 1),
            };
            if (r.Error != null)
                json["error"] = r.Error;
            return json;
        }

        var arr = new JsonArray();
        foreach (var r in results)
        {
            var entry = new JsonObject
            {
                ["vm_name"] = r.VmName,
                ["success"] = r.Success,
                ["state"] = r.State,
                ["elapsed_seconds"] = Math.Round(r.ElapsedSeconds, 1),
            };
            if (r.Error != null)
                entry["error"] = r.Error;
            arr.Add(entry);
        }

        var allSuccess = results.All(r => r.Success);
        return new JsonObject { ["results"] = arr, ["all_success"] = allSuccess, ["count"] = results.Count };
    }
}
